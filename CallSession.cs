using System.Text;

namespace AiCallAssistant;

/// <summary>One recognized phrase. <see cref="Speaker"/> is a key: "@me", "@other" or "#N".</summary>
sealed record Utterance(TimeSpan Start, TimeSpan End, string Speaker, string Text);

enum SessionState { Recording, Transcribing, Done, Failed }

/// <summary>
/// Everything known about one call (or one transcribed file): the phrases, who said them and the names
/// the user gave to the voices. The transcript file is always rendered from here, so renaming a speaker
/// rewrites it. Thread-safe: phrases arrive from the recognition worker, names from the UI.
/// </summary>
sealed class CallSession
{
    public const string MeKey = "@me";
    public const string OtherKey = "@other";

    readonly object _sync = new();
    readonly List<Utterance> _lines = new();
    readonly Dictionary<string, string> _names = new();
    readonly Strings _text = Strings.Current; // default labels stay in the language the session started with

    public string AudioPath { get; }
    public string TranscriptPath => Path.ChangeExtension(AudioPath, ".txt");
    public string NotesPath => Path.ChangeExtension(AudioPath, ".assistant.md");
    public bool IsRecording { get; }
    public string? Project { get; set; }

    SessionState _state;
    public SessionState State
    {
        get => _state;
        set { _state = value; Changed?.Invoke(); }
    }

    /// <summary>The lines are the live draft (true) or the accurate transcript made after the call (false).</summary>
    public bool IsDraft { get; private set; }

    /// <summary>Lines replaced or names changed — redraw everything. Raised on any thread.</summary>
    public event Action? Changed;

    /// <summary>A draft phrase was appended. Raised on the recognition thread.</summary>
    public event Action<Utterance>? LineAdded;

    public CallSession(string audioPath, string? project, string? myName, bool isRecording)
    {
        AudioPath = audioPath;
        Project = project;
        IsRecording = isRecording;
        IsDraft = isRecording;
        _state = isRecording ? SessionState.Recording : SessionState.Transcribing;
        if (!string.IsNullOrWhiteSpace(myName))
            _names[MeKey] = myName.Trim();
    }

    public Utterance[] Lines
    {
        get { lock (_sync) return _lines.ToArray(); }
    }

    public int LineCount
    {
        get { lock (_sync) return _lines.Count; }
    }

    /// <summary>Speakers in order of their first phrase.</summary>
    public List<(string Key, string Label, string DefaultLabel, int Phrases)> Speakers
    {
        get
        {
            lock (_sync)
            {
                return _lines.GroupBy(l => l.Speaker)
                    .Select(g => (g.Key, Label(g.Key), DefaultLabel(g.Key), g.Count()))
                    .ToList();
            }
        }
    }

    public string Label(string key)
    {
        lock (_sync)
            return _names.TryGetValue(key, out var name) ? name : DefaultLabel(key);
    }

    public string DefaultLabel(string key) => key switch
    {
        MeKey => _text.Me,
        OtherKey => _text.OtherParty,
        _ when key.StartsWith('#') && int.TryParse(key[1..], out int n) => _text.Speaker(n),
        _ => key
    };

    public string Render(Utterance u) => Transcript.Line(u.Start, Label(u.Speaker), u.Text);

    /// <summary>The latest part of the transcript that fits into <paramref name="maxChars"/>, oldest line first.</summary>
    public string RenderTail(int maxChars, out bool truncated)
    {
        var lines = Lines;
        var picked = new List<string>();
        int size = 0;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = Render(lines[i]);
            if (size + line.Length + 1 > maxChars && picked.Count > 0)
                break;
            picked.Add(line);
            size += line.Length + 1;
        }
        truncated = picked.Count < lines.Length;
        picked.Reverse();
        return string.Join("\n", picked);
    }

    // ---------- changes ----------

    /// <summary>Appends a live draft phrase and writes it to the transcript file.</summary>
    public void Add(Utterance u)
    {
        lock (_sync)
        {
            _lines.Add(u);
            File.AppendAllText(TranscriptPath, Render(u) + Environment.NewLine, Utf8);
        }
        LineAdded?.Invoke(u);
    }

    /// <summary>Gives a voice a name (null or empty — back to "Speaker N") and rewrites the transcript.</summary>
    public void Rename(string key, string? name)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == DefaultLabel(key))
                _names.Remove(key);
            else
                _names[key] = name.Trim();
            if (_lines.Count > 0)
                WriteFile();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Replaces the draft with the accurate transcript. The accurate pass numbers voices on its own,
    /// so each of its voices takes over the draft speaker it overlaps most in time — names given
    /// during the call stay with the right person.
    /// </summary>
    public void AdoptFinal(List<Utterance> final)
    {
        lock (_sync)
        {
            if (_lines.Count > 0)
                final = MapToDraftSpeakers(final, _lines);
            _lines.Clear();
            _lines.AddRange(final);
            IsDraft = false;
            WriteFile();
        }
        Changed?.Invoke();
    }

    List<Utterance> MapToDraftSpeakers(List<Utterance> final, List<Utterance> draft)
    {
        static bool Remote(string key) => key != MeKey;

        var overlaps = new List<(string Final, string Draft, double Seconds)>();
        foreach (var f in final.Where(u => Remote(u.Speaker)).GroupBy(u => u.Speaker))
        foreach (var d in draft.Where(u => Remote(u.Speaker)).GroupBy(u => u.Speaker))
        {
            double seconds = 0;
            foreach (var a in f)
            foreach (var b in d)
            {
                var from = a.Start > b.Start ? a.Start : b.Start;
                var to = a.End < b.End ? a.End : b.End;
                if (to > from)
                    seconds += (to - from).TotalSeconds;
            }
            if (seconds > 0)
                overlaps.Add((f.Key, d.Key, seconds));
        }

        var map = new Dictionary<string, string> { [MeKey] = MeKey };
        foreach (var (f, d, _) in overlaps.OrderByDescending(o => o.Seconds))
            if (!map.ContainsKey(f) && !map.ContainsValue(d))
                map[f] = d;

        // Voices the draft did not have get the smallest free "Speaker N" that no named draft voice uses.
        var taken = new HashSet<string>(map.Values.Concat(_names.Keys));
        int next = 1;
        foreach (var key in final.Select(u => u.Speaker).Distinct())
        {
            if (map.ContainsKey(key))
                continue;
            while (taken.Contains("#" + next))
                next++;
            map[key] = "#" + next;
            taken.Add(map[key]);
        }

        return final.Select(u => u with { Speaker = map[u.Speaker] }).ToList();
    }

    void WriteFile()
    {
        var text = new StringBuilder();
        foreach (var u in _lines)
            text.AppendLine(Render(u));
        if (_lines.Count == 0)
            text.AppendLine(_text.NoSpeech);

        // Replace the file only as a whole.
        var temp = TranscriptPath + ".tmp";
        File.WriteAllText(temp, text.ToString(), Utf8);
        File.Move(temp, TranscriptPath, overwrite: true);
    }

    static readonly UTF8Encoding Utf8 = new(false);
}

static class Transcript
{
    public static string Line(TimeSpan at, string speaker, string text) =>
        $"[{Time(at)}] {speaker}: {text}";

    public static string Time(TimeSpan at) => $"{(int)at.TotalHours:00}:{at:mm\\:ss}";
}
