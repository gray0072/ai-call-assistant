using System.Text;

namespace AiCallAssistant;

enum NoteKind { Live, Question, Final }

/// <summary>One answer of the assistant; <see cref="Text"/> grows while it streams in.</summary>
sealed class AssistantNote
{
    readonly StringBuilder _text = new();

    public required NoteKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Model { get; init; }
    public DateTime At { get; } = DateTime.Now;
    public bool Done { get; set; }
    public string? Error { get; set; }
    public decimal? Cost { get; set; }

    public string Text
    {
        get { lock (_text) return _text.ToString(); }
    }

    public void Append(string s)
    {
        lock (_text) _text.Append(s);
    }
}

/// <summary>
/// Reads the transcript of a call and writes short notes: a running summary, suggestions on what to say
/// or ask, and doubts about what was said. During the call it runs when there is new speech and either
/// the chosen interval has passed or someone else has just asked a question; the user can also ask
/// at any time. After the call it writes a final summary. Notes are also saved next to the recording.
/// </summary>
sealed class CallAssistant : IDisposable
{
    /// <summary>A question from someone else is answered sooner, but not more often than this.</summary>
    static readonly TimeSpan QuestionDelay = TimeSpan.FromSeconds(15);

    readonly CallSession _session;
    readonly AppSettings _settings;
    readonly List<AssistantNote> _notes = new();
    readonly SemaphoreSlim _gate = new(1, 1); // one request at a time, in order
    readonly CancellationTokenSource _cts = new();

    string? _summary;      // the model's own running summary, carried from run to run
    string? _suggestions;  // its last suggestions, so it does not repeat them
    string? _summaryView, _notesView;
    DateTime? _summaryAt, _notesAt;
    bool _summaryFinal;
    int _linesSeen;        // transcript lines covered by the last live run
    DateTime _lastRun = DateTime.MinValue;
    int _queued;

    public decimal Cost { get; private set; }
    public bool CostKnown { get; private set; }

    /// <summary>A note was added or finished. Raised on any thread.</summary>
    public event Action? NotesChanged;

    /// <summary>Text of a note grew. Raised on a background thread.</summary>
    public event Action<AssistantNote>? NoteUpdated;

    /// <summary>A request failed (message for the user). Raised on a background thread.</summary>
    public event Action<string>? Failed;

    public CallAssistant(CallSession session, AppSettings settings)
    {
        _session = session;
        _settings = settings;
    }

    public CallSession Session => _session;

    public AssistantNote[] Notes
    {
        get { lock (_notes) return _notes.ToArray(); }
    }

    public bool IsBusy => Volatile.Read(ref _queued) > 0;

    /// <summary>
    /// The current summary (Markdown) and when it was written: replaced by every update, and after
    /// the call by the summary of the whole call (<see cref="SummaryIsFinal"/>).
    /// </summary>
    public (string? Text, DateTime? At) Summary
    {
        get { lock (_notes) return (_summaryView, _summaryAt); }
    }

    public bool SummaryIsFinal => _summaryFinal;

    /// <summary>Suggestions and doubts of the latest update (Markdown); cleared when the call is over.</summary>
    public (string? Text, DateTime? At) CurrentNotes
    {
        get { lock (_notes) return (_notesView, _notesAt); }
    }

    /// <summary>A live update or the final summary is being written (the question answers are separate).</summary>
    public bool IsUpdating
    {
        get { lock (_notes) return _notes.Any(n => !n.Done && n.Kind != NoteKind.Question); }
    }

    /// <summary>The user's questions with the answers, newest first.</summary>
    public AssistantNote[] Questions
    {
        get { lock (_notes) return _notes.Where(n => n.Kind == NoteKind.Question).Reverse().ToArray(); }
    }

    /// <summary>The error of the latest update, if it failed.</summary>
    public string? LastError { get; private set; }

    static Strings T => Strings.Current;

    /// <summary>Called every couple of seconds from the UI timer; starts a live update when it is due.</summary>
    public void Tick()
    {
        var a = _settings.Assistant;
        if (!a.Enabled || a.IntervalSeconds <= 0 || IsBusy || _session.State != SessionState.Recording)
            return;

        var lines = _session.Lines;
        if (lines.Length <= _linesSeen)
            return;

        var since = DateTime.Now - _lastRun;
        bool due = since >= TimeSpan.FromSeconds(a.IntervalSeconds);
        bool question = since >= QuestionDelay && lines.Skip(_linesSeen)
            .Any(l => l.Speaker != CallSession.MeKey && l.Text.TrimEnd().EndsWith('?'));
        if (due || question)
            Run(NoteKind.Live, null);
    }

    /// <summary>Update the notes now, or answer the user's question.</summary>
    public void Ask(string? question) =>
        Run(string.IsNullOrWhiteSpace(question) ? NoteKind.Live : NoteKind.Question, question?.Trim());

    /// <summary>Notes for the whole call, once the accurate transcript is ready.</summary>
    public void Finish()
    {
        if (_session.LineCount > 0)
            Run(NoteKind.Final, null);
    }

    void Run(NoteKind kind, string? question)
    {
        var settings = _settings.Assistant;
        var note = new AssistantNote
        {
            Kind = kind,
            Title = kind switch
            {
                NoteKind.Question => T.AiYouAsked + question,
                NoteKind.Final => T.AiFinalTitle,
                _ => T.AiNotesTitle
            },
            Model = settings.Model ?? "?"
        };
        lock (_notes) _notes.Add(note);
        Interlocked.Increment(ref _queued);
        _lastRun = DateTime.Now;
        NotesChanged?.Invoke();

        _ = Task.Run(() => RunAsync(note, question));
    }

    async Task RunAsync(AssistantNote note, string? question)
    {
        try
        {
            await _gate.WaitAsync(_cts.Token);
            try
            {
                var settings = _settings.Assistant;
                int lines = _session.LineCount;
                var messages = BuildPrompt(note.Kind, question, settings);
                int maxTokens = note.Kind == NoteKind.Final ? 1500 : 800;

                // Small local models now and then return nothing at all: ask once more.
                ChatResult result = null!;
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    result = await LlmClient.ChatAsync(settings, messages, text =>
                    {
                        note.Append(text);
                        NoteUpdated?.Invoke(note);
                    }, maxTokens, _cts.Token);
                    if (result.Text.Trim().Length > 0 || result.FinishReason == "length")
                        break;
                }

                if (result.Text.Trim().Length == 0)
                    throw new InvalidOperationException(T.AiEmptyAnswer);

                if (note.Kind != NoteKind.Question)
                {
                    _summary = Section(result.Text, T.AiHeadSummary) ?? _summary;
                    _suggestions = Section(result.Text, T.AiHeadSuggestions);
                    LastError = null;
                    lock (_notes)
                    {
                        if (note.Kind == NoteKind.Final)
                        {
                            // The whole call: summary, decisions, action items, open questions.
                            _summaryView = DropEmptySections(result.Text);
                            _summaryAt = DateTime.Now;
                            _summaryFinal = true;
                            _notesView = null; // suggestions for a call that is over are not current any more
                            _notesAt = null;
                        }
                        else if (!_summaryFinal)
                        {
                            if (_summary != null)
                            {
                                _summaryView = _summary;
                                _summaryAt = DateTime.Now;
                            }
                            _notesView = DropEmptySections(WithoutSection(result.Text, T.AiHeadSummary));
                            _notesAt = DateTime.Now;
                        }
                    }
                }
                if (note.Kind == NoteKind.Live)
                    _linesSeen = lines;

                note.Cost = result.Cost;
                if (result.Cost is { } cost)
                {
                    Cost += cost;
                    CostKnown = true;
                }
                Save(note);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            note.Error = T.AiCancelled;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Assistant ({note.Kind}) failed: {ex}");
            note.Error = ex.GetBaseException().Message;
            if (note.Kind != NoteKind.Question)
                LastError = note.Error;
            Failed?.Invoke(note.Error);
        }
        finally
        {
            note.Done = true;
            Interlocked.Decrement(ref _queued);
            NotesChanged?.Invoke();
        }
    }

    List<ChatMessage> BuildPrompt(NoteKind kind, string? question, AssistantSettings settings)
    {
        var me = _session.Label(CallSession.MeKey);
        var project = _settings.FindProject(_session.Project);

        var system = new StringBuilder();
        system.AppendLine($"You are a live assistant for the user during a call{(project != null ? $" in the project \"{project.Name}\"" : "")}.");
        system.AppendLine("You read the transcript of the call as it happens and help the user follow it and respond well.");
        system.AppendLine($"The transcript comes from automatic speech recognition: expect misheard words, missing punctuation and occasionally wrong speaker labels; infer what was meant. Lines labelled \"{me}\" are the user; other labels are the other participants.");
        system.AppendLine($"Write in {T.AiLanguageName}. The user reads your notes while talking, so be brief and concrete: short bullets, no preamble, no filler.");
        if (!string.IsNullOrWhiteSpace(project?.Notes))
        {
            system.AppendLine();
            system.AppendLine("Background on the project, written by the user:");
            system.AppendLine(project.Notes.Trim());
        }

        // Fit the transcript into the model's context: roughly 2 characters per token for mixed languages.
        int context = settings.Provider == LlmProvider.LmStudio ? settings.LocalContext ?? 8192 : 32000;
        int budget = Math.Clamp((context - 2500) * 2, 3000, 60000);
        var transcript = _session.RenderTail(budget, out bool truncated);

        var user = new StringBuilder();
        if (_summary != null && (truncated || kind != NoteKind.Final))
        {
            user.AppendLine("Your summary of the call so far:");
            user.AppendLine(_summary);
            user.AppendLine();
        }
        if (kind == NoteKind.Live && _suggestions != null)
        {
            user.AppendLine("Your previous suggestions:");
            user.AppendLine(_suggestions);
            user.AppendLine();
        }

        user.AppendLine(kind == NoteKind.Final
            ? $"The call has ended. Accurate transcript{(truncated ? " (latest part)" : "")}:"
            : $"Transcript{(truncated ? " (latest part)" : "")}, times from the start of the call:");
        user.AppendLine(transcript.Length > 0 ? transcript : "(nobody has spoken yet)");
        user.AppendLine();

        switch (kind)
        {
            case NoteKind.Question:
                user.AppendLine($"The user asks: {question}");
                user.AppendLine("Answer briefly and directly, in the language of the question, using the transcript and your own knowledge. Say so if you are not sure.");
                break;

            case NoteKind.Final:
                user.AppendLine("Write the final notes of the call. Use exactly these headings, in this order, and leave out a heading with nothing under it:");
                user.AppendLine($"## {T.AiHeadSummary}\n5–10 bullets: what the call was about and what was said.");
                user.AppendLine($"## {T.AiHeadDecisions}\nWhat was decided.");
                user.AppendLine($"## {T.AiHeadActions}\nBullets \"who — what — when\".");
                user.AppendLine($"## {T.AiHeadOpen}\nQuestions left unanswered.");
                break;

            default:
                user.AppendLine("Update your notes. Use exactly these headings, in this order, and nothing else:");
                user.AppendLine($"## {T.AiHeadSummary}\nUp to 5 short bullets: the call so far, with decisions and agreed next steps.");
                user.AppendLine($"## {T.AiHeadSuggestions}\nUp to 3 bullets for the user right now: an answer to a question put to them, a question worth asking, a topic not covered yet. Do not repeat earlier suggestions unless they are still the most relevant.");
                user.AppendLine($"## {T.AiHeadCheck}\nOnly if something said looks factually wrong, contradictory or unclear: one bullet each, saying why. If there is nothing like that, leave out this heading completely — do not write \"nothing to check\".");
                break;
        }

        return new() { new("system", system.ToString()), new("user", user.ToString()) };
    }

    /// <summary>Text under "## heading" up to the next heading.</summary>
    static string? Section(string text, string heading)
    {
        var lines = text.Replace("\r", "").Split('\n');
        int start = Array.FindIndex(lines, l => l.TrimStart('#', ' ').Trim().Equals(heading, StringComparison.OrdinalIgnoreCase) && l.TrimStart().StartsWith('#'));
        if (start < 0)
            return null;
        var body = lines.Skip(start + 1).TakeWhile(l => !l.TrimStart().StartsWith('#'));
        var result = string.Join("\n", body).Trim();
        return result.Length == 0 ? null : result;
    }

    /// <summary>Things models write under a heading they were told to leave out when it has nothing.</summary>
    static readonly System.Text.RegularExpressions.Regex Nothing = new(
        @"^[\s\(\-\*•_]*(none|n/?a|nothing( to (check|report|suggest))?|no (issues|concerns)|нет|ничего( нет)?|не требуется|inget|ingen|inga)[\s\)\.!_]*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Removes headings with nothing (or only "(None)" and the like) under them.</summary>
    static string DropEmptySections(string text)
    {
        var sections = new List<(string? Heading, List<string> Body)> { (null, new()) };
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            if (line.TrimStart().StartsWith('#'))
                sections.Add((line, new()));
            else
                sections[^1].Body.Add(line);
        }

        var result = new List<string>();
        foreach (var (heading, body) in sections)
        {
            var content = body.Where(l => l.Trim().Length > 0).ToList();
            if (heading != null && content.All(l => Nothing.IsMatch(l.Trim())))
                continue;
            if (heading != null)
                result.Add(heading);
            result.AddRange(body);
        }
        return string.Join("\n", result).Trim();
    }

    /// <summary>The text without "## heading" and what is under it.</summary>
    static string WithoutSection(string text, string heading)
    {
        var result = new List<string>();
        bool skipping = false;
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            if (line.TrimStart().StartsWith('#'))
                skipping = line.TrimStart('#', ' ').Trim().Equals(heading, StringComparison.OrdinalIgnoreCase);
            if (!skipping)
                result.Add(line);
        }
        return string.Join("\n", result).Trim();
    }

    void Save(AssistantNote note)
    {
        try
        {
            var header = $"### {note.At:HH:mm:ss} — {note.Title} ({note.Model})";
            _session.AppendNotes($"{header}\n\n{note.Text.Trim()}\n\n");
        }
        catch (Exception ex)
        {
            AppLog.Write("Saving assistant notes failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
    }
}
