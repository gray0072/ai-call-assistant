using System.Text;

namespace AiCallAssistant;

/// <summary>One recognized phrase. <see cref="Speaker"/> is a key: "@me", "@other" or "#N".</summary>
sealed record Utterance(TimeSpan Start, TimeSpan End, string Speaker, string Text);

enum SessionState { Recording, Transcribing, Done, Failed }

/// <summary>A voice in a call. <see cref="Recognized"/> — similarity, if the name was given by voice recognition.</summary>
sealed record SpeakerInfo(string Key, string Label, string DefaultLabel, int Phrases, float? Recognized);

/// <summary>
/// Everything known about one call (or one transcribed file): the phrases, who said them, the names
/// of the voices (typed by the user or recognized from known voices) and their voice prints.
/// The transcript file is always rendered from here, so renaming a speaker rewrites it.
/// Thread-safe: phrases arrive from the recognition worker, names from the UI.
/// </summary>
sealed class CallSession
{
    public const string MeKey = "@me";
    public const string OtherKey = "@other";

    readonly object _sync = new();
    readonly List<Utterance> _lines = new();
    readonly Dictionary<string, string> _names = new();
    readonly Dictionary<string, float> _recognized = new();                    // names given by recognition, with the score
    readonly HashSet<string> _touched = new();                                 // voices the user named or cleared: recognition keeps off
    readonly Dictionary<string, float[]> _prints = new();                      // voice prints of the accurate pass
    readonly Dictionary<string, (float[] Sum, int Count)> _livePrints = new(); // sums of live phrase prints
    readonly Strings _text = Strings.Current; // default labels stay in the language the session started with

    string _audioPath;
    public string AudioPath
    {
        get { lock (_sync) return _audioPath; }
    }
    public string TranscriptPath => Path.ChangeExtension(AudioPath, ".txt");
    public string NotesPath => Path.ChangeExtension(AudioPath, ".assistant.md");

    /// <summary>A call recorded by the app (its files belong to the app), not a transcribed file.</summary>
    public bool IsRecording { get; }

    /// <summary>Stable id of the call (the original file name), even if its files are moved.</summary>
    public string Id { get; }
    public string? Project { get; set; }

    SessionState _state;
    public SessionState State
    {
        get => _state;
        set { _state = value; Changed?.Invoke(); }
    }

    /// <summary>The lines are the live draft (true) or the accurate transcript made after the call (false).</summary>
    public bool IsDraft { get; private set; }

    /// <summary>Lines replaced, names changed or files moved — redraw everything. Raised on any thread.</summary>
    public event Action? Changed;

    /// <summary>A draft phrase was appended. Raised on the recognition thread.</summary>
    public event Action<Utterance>? LineAdded;

    public CallSession(string audioPath, string? project, string? myName, bool isRecording)
    {
        _audioPath = audioPath;
        Id = Path.GetFileName(audioPath);
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
    public List<SpeakerInfo> Speakers
    {
        get
        {
            lock (_sync)
            {
                return _lines.GroupBy(l => l.Speaker)
                    .Select(g => new SpeakerInfo(g.Key, Label(g.Key), DefaultLabel(g.Key), g.Count(),
                        _recognized.TryGetValue(g.Key, out var score) ? score : null))
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

    // ---------- names and voice prints ----------

    /// <summary>True if the user typed a name for this voice (not given by recognition).</summary>
    public bool IsNamedByUser(string key)
    {
        lock (_sync)
            return _names.ContainsKey(key) && !_recognized.ContainsKey(key);
    }

    /// <summary>The name of a voice if it has one (typed or recognized).</summary>
    public string? NameOf(string key)
    {
        lock (_sync)
            return _names.TryGetValue(key, out var name) ? name : null;
    }

    /// <summary>Names already given in this call, except the one of <paramref name="exceptKey"/>.</summary>
    public HashSet<string> UsedNames(string? exceptKey = null)
    {
        lock (_sync)
            return new(_names.Where(n => n.Key != exceptKey).Select(n => n.Value), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The best voice print of a voice: the accurate one, else the average of its live phrases.</summary>
    public float[]? Print(string key)
    {
        lock (_sync)
        {
            if (_prints.TryGetValue(key, out var print))
                return print;
            if (!_livePrints.TryGetValue(key, out var live))
                return null;
            var average = (float[])live.Sum.Clone();
            VoicePrint.Normalize(average);
            return average;
        }
    }

    /// <summary>Accurate voice prints of the voices of the call, by key.</summary>
    public Dictionary<string, float[]> Prints
    {
        get { lock (_sync) return new(_prints); }
    }

    /// <summary>Adds the print of one live phrase; returns the voice's average so far and how many phrases it has.</summary>
    public (float[] Average, int Phrases) AddLivePrint(string key, float[] print)
    {
        lock (_sync)
        {
            if (!_livePrints.TryGetValue(key, out var live))
                live = (new float[print.Length], 0);
            if (live.Sum.Length == print.Length)
            {
                for (int i = 0; i < print.Length; i++)
                    live.Sum[i] += print[i];
                live.Count++;
            }
            _livePrints[key] = live;
            var average = (float[])live.Sum.Clone();
            VoicePrint.Normalize(average);
            return (average, live.Count);
        }
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

    /// <summary>
    /// Names a voice by voice recognition, unless the user has named it; <paramref name="name"/> null
    /// takes back a name recognition gave. Returns true if the name changed.
    /// </summary>
    public bool SetRecognized(string key, string? name, float score)
    {
        lock (_sync)
        {
            if (_touched.Contains(key) || (_names.ContainsKey(key) && !_recognized.ContainsKey(key)))
                return false; // what the user typed (or cleared) always wins
            if (name == null)
            {
                if (!_recognized.Remove(key))
                    return false;
                _names.Remove(key);
            }
            else
            {
                bool same = _names.TryGetValue(key, out var current) && current == name;
                _names[key] = name;
                _recognized[key] = score;
                if (same)
                    return false;
            }
            if (_lines.Count > 0)
                WriteFile();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Gives a voice a name (null or empty — back to "Speaker N") and rewrites the transcript.</summary>
    public void Rename(string key, string? name)
    {
        lock (_sync)
        {
            _recognized.Remove(key);
            _touched.Add(key);
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
    public void AdoptFinal(TranscriptResult result)
    {
        lock (_sync)
        {
            var final = result.Lines;
            var map = _lines.Count > 0
                ? MapToDraftSpeakers(final, _lines)
                : final.Select(u => u.Speaker).Distinct().ToDictionary(k => k, k => k);
            _lines.Clear();
            _lines.AddRange(final.Select(u => u with { Speaker = map[u.Speaker] }));
            _prints.Clear();
            foreach (var (key, print) in result.Prints)
                _prints[map.TryGetValue(key, out var mapped) ? mapped : key] = print;
            IsDraft = false;
            WriteFile();
        }
        Changed?.Invoke();
    }

    /// <summary>Final speaker key → draft speaker key.</summary>
    Dictionary<string, string> MapToDraftSpeakers(List<Utterance> final, List<Utterance> draft)
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
        return map;
    }

    /// <summary>Appends a note of the assistant to the notes file next to the recording.</summary>
    public void AppendNotes(string text)
    {
        lock (_sync)
            File.AppendAllText(NotesPath, text, Utf8);
    }

    /// <summary>
    /// Moves the recording and the files next to it (transcript, assistant notes) to <paramref name="folder"/>.
    /// Call only when nothing writes to them any more. Returns false if they are there already.
    /// </summary>
    public bool MoveTo(string folder)
    {
        lock (_sync)
        {
            var from = Path.GetDirectoryName(_audioPath)!;
            if (string.Equals(Path.GetFullPath(from).TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return false;

            Directory.CreateDirectory(folder);
            var name = Path.GetFileNameWithoutExtension(_audioPath);
            var extensions = new[] { Path.GetExtension(_audioPath), ".txt", ".assistant.md" };
            var target = name;
            for (int n = 2; extensions.Any(e => File.Exists(Path.Combine(folder, target + e))); n++)
                target = $"{name} ({n})";

            foreach (var ext in extensions)
            {
                var source = Path.Combine(from, name + ext);
                if (File.Exists(source))
                    File.Move(source, Path.Combine(folder, target + ext));
            }
            _audioPath = Path.Combine(folder, target + extensions[0]);
        }
        Changed?.Invoke();
        return true;
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
