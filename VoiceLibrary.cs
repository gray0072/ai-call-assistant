using System.Text.Json;

namespace AiCallAssistant;

/// <summary>Math on voice prints (speaker embeddings): normalized vectors compared by cosine similarity.</summary>
static class VoicePrint
{
    public static void Normalize(float[] v)
    {
        float norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0)
            for (int i = 0; i < v.Length; i++)
                v[i] /= norm;
    }

    /// <summary>Cosine similarity of two normalized vectors.</summary>
    public static float Similarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return -1;
        float dot = 0;
        for (int i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return dot;
    }
}

/// <summary>A known voice recognized in a call.</summary>
sealed record VoiceMatch(string Name, float Score);

/// <summary>
/// Voices of people the user has named, kept between calls in voices.json next to the exe, so the
/// next calls can name them automatically. One sample (voice print) per person per call; a person
/// keeps the latest <see cref="MaxSamples"/>. Thread-safe.
/// </summary>
sealed class VoiceLibrary
{
    /// <summary>
    /// Similarity a voice of a whole call (average over its phrases) needs to be taken for a known person.
    /// Measured on a 3.5-minute call with three people (compressed, one channel): the same person in
    /// the two halves of the call 0.77–0.94, different people 0.57–0.76. A missed name is better than
    /// a wrong one, so the bar is above every different-people pair seen.
    /// </summary>
    public const float FinalThreshold = 0.80f;

    /// <summary>During the call the print is built from fewer phrases and is noisier.</summary>
    public const float LiveThreshold = 0.82f;

    /// <summary>The best person must beat the second best by this much, otherwise nobody is named.</summary>
    const float Margin = 0.06f;

    const int MaxSamples = 12;

    static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "voices.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public sealed class Sample
    {
        public float[] Print { get; set; } = Array.Empty<float>();
        public DateTime At { get; set; }
        public string? Project { get; set; }

        /// <summary>The recording it came from: renaming a voice in the same call replaces its sample.</summary>
        public string? Source { get; set; }
    }

    public sealed class Person
    {
        public string Name { get; set; } = "";
        public List<Sample> Samples { get; set; } = new();
    }

    sealed class Data
    {
        public List<Person> People { get; set; } = new();
    }

    readonly object _sync = new();
    readonly Data _data;

    VoiceLibrary(Data data) => _data = data;

    public static VoiceLibrary Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return new VoiceLibrary(JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath), Json) ?? new());
        }
        catch (Exception ex)
        {
            AppLog.Write("Voice library load failed: " + ex.Message);
        }
        return new VoiceLibrary(new());
    }

    /// <summary>Raised after any change (on the thread that made it).</summary>
    public event Action? Changed;

    /// <summary>Name, number of samples, last time heard, projects — for the list of known voices.</summary>
    public List<(string Name, int Samples, DateTime LastSeen, string Projects)> List()
    {
        lock (_sync)
        {
            return _data.People
                .Select(p => (p.Name, p.Samples.Count,
                    p.Samples.Count == 0 ? DateTime.MinValue : p.Samples.Max(s => s.At),
                    string.Join(", ", p.Samples.Select(s => s.Project).OfType<string>().Distinct())))
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Best known person for one voice, unless their name is in <paramref name="exclude"/>.</summary>
    public VoiceMatch? Match(float[] print, float threshold, ICollection<string> exclude)
    {
        lock (_sync)
        {
            var scores = Score(print, exclude);
            if (scores.Count == 0 || scores[0].Score < threshold)
                return null;
            if (scores.Count > 1 && scores[0].Score - scores[1].Score < Margin)
                return null;
            return scores[0];
        }
    }

    /// <summary>
    /// Names several voices of one call at once: the best pairs first, each person at most once,
    /// so two voices never get the same name.
    /// </summary>
    public Dictionary<string, VoiceMatch> MatchAll(IReadOnlyDictionary<string, float[]> prints, float threshold, ICollection<string> exclude)
    {
        var result = new Dictionary<string, VoiceMatch>();
        lock (_sync)
        {
            var candidates = new List<(string Key, VoiceMatch Match)>();
            foreach (var (key, print) in prints)
            {
                var scores = Score(print, exclude);
                if (scores.Count == 0 || scores[0].Score < threshold)
                    continue;
                if (scores.Count > 1 && scores[0].Score - scores[1].Score < Margin)
                    continue;
                candidates.Add((key, scores[0]));
            }

            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, match) in candidates.OrderByDescending(c => c.Match.Score))
                if (taken.Add(match.Name))
                    result[key] = match;
        }
        return result;
    }

    /// <summary>Per person: the best similarity with any of their samples, best first.</summary>
    List<VoiceMatch> Score(float[] print, ICollection<string> exclude) =>
        _data.People
            .Where(p => p.Samples.Count > 0 && !exclude.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            .Select(p => new VoiceMatch(p.Name, p.Samples.Max(s => VoicePrint.Similarity(print, s.Print))))
            .OrderByDescending(m => m.Score)
            .ToList();

    /// <summary>Remembers that <paramref name="print"/> is <paramref name="name"/>'s voice (replacing what this call said before).</summary>
    public void Enroll(string name, float[] print, string? project, string source)
    {
        lock (_sync)
        {
            RemoveSample(print, source);
            var person = _data.People.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (person == null)
                _data.People.Add(person = new Person { Name = name });
            person.Samples.Add(new Sample { Print = print, At = DateTime.Now, Project = project, Source = source });
            if (person.Samples.Count > MaxSamples)
                person.Samples.RemoveRange(0, person.Samples.Count - MaxSamples);
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Forgets the sample this call gave for a voice (its name was cleared).</summary>
    public void Unenroll(float[] print, string source)
    {
        lock (_sync)
        {
            if (!RemoveSample(print, source))
                return;
            Save();
        }
        Changed?.Invoke();
    }

    public void Delete(string name)
    {
        lock (_sync)
        {
            if (_data.People.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
                return;
            Save();
        }
        Changed?.Invoke();
    }

    bool RemoveSample(float[] print, string source)
    {
        bool removed = false;
        foreach (var p in _data.People)
            removed |= p.Samples.RemoveAll(s => s.Source == source && VoicePrint.Similarity(s.Print, print) > 0.999f) > 0;
        _data.People.RemoveAll(p => p.Samples.Count == 0);
        return removed;
    }

    void Save()
    {
        try
        {
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_data, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Write("Voice library save failed: " + ex.Message);
        }
    }
}
