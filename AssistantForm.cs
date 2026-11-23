using System.Text;

namespace AiCallAssistant;

/// <summary>
/// The main window: project at the top, participants (whose names the user can type in), the live
/// transcript and the AI assistant's notes. It only shows state owned by <see cref="TrayApp"/>,
/// so closing it never stops a recording.
/// </summary>
sealed class AssistantForm : Form
{
    static readonly Color[] SpeakerColors =
    {
        Color.FromArgb(0, 102, 204), Color.FromArgb(196, 72, 0), Color.FromArgb(0, 133, 90),
        Color.FromArgb(150, 50, 170), Color.FromArgb(170, 120, 0), Color.FromArgb(200, 30, 90),
        Color.FromArgb(0, 128, 140), Color.FromArgb(110, 90, 60)
    };
    static readonly Color MeColor = Color.FromArgb(90, 90, 90);

    readonly TrayApp _app;

    // top bar
    readonly Label _projectLabel, _modeLabel, _state;
    readonly ComboBox _project, _mode;
    readonly Button _notes, _projectFolder, _start, _pause, _stop;

    // participants
    readonly Label _participantsTitle, _participantsHint;
    readonly Button _knownVoices;
    readonly DataGridView _people;

    // transcript
    readonly Label _transcriptTitle, _transcriptHint;
    readonly RichTextBox _transcript;

    // assistant
    readonly CheckBox _aiEnabled;
    readonly Label _aiModel, _aiEveryLabel, _aiCost, _aiNeedsLive, _summaryTitle, _notesTitle;
    readonly ComboBox _aiInterval;
    readonly Button _aiSettings, _aiNow, _ask;
    readonly RichTextBox _aiSummary, _aiNotes;
    readonly TextBox _question;

    readonly SplitContainer _outer, _inner, _aiSplit;
    readonly System.Windows.Forms.Timer _refresh;

    CallSession? _session;
    CallAssistant? _assistant;
    volatile bool _transcriptDirty, _peopleDirty, _notesDirty;
    readonly List<Utterance> _pendingLines = new();
    bool _updatingProject;
    string? _summaryShown, _notesShown; // what the panes show, to redraw only on change
    Font? _italic;

    static Strings T => Strings.Current;

    public AssistantForm(TrayApp app)
    {
        _app = app;
        Ui.Prepare(this);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 760);
        MinimumSize = new Size(760, 480);

        // ---------- top bar ----------
        _projectLabel = Ui.Label();
        _project = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            Width = 280,
            Anchor = AnchorStyles.Left,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems
        };
        _project.SelectionChangeCommitted += (_, _) => BeginInvoke(CommitProject);
        _project.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                CommitProject();
                e.SuppressKeyPress = true;
            }
        };
        _project.Leave += (_, _) => CommitProject();
        _notes = Ui.Button("", (_, _) => EditProjectNotes());
        _projectFolder = Ui.Button("", (_, _) => { CommitProject(); _app.OpenProjectFolder(); });

        _modeLabel = Ui.Label();
        _modeLabel.Margin = new Padding(16, 6, 3, 6);
        _mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, Anchor = AnchorStyles.Left };
        _mode.SelectionChangeCommitted += (_, _) => _app.SetMode(Modes[Math.Max(0, _mode.SelectedIndex)]);

        _state = Ui.Label(bold: true);
        _state.Anchor = AnchorStyles.Right;
        _start = Ui.Button("", (_, _) => _app.Start());
        _pause = Ui.Button("", (_, _) => _app.TogglePause());
        _stop = Ui.Button("", (_, _) => _app.Stop());

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 10, Padding = new Padding(8, 6, 8, 2) };
        for (int i = 0; i < 6; i++)
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++)
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.AddRange(new Control[] { _projectLabel, _project, _notes, _projectFolder, _modeLabel, _mode, _state, _start, _pause, _stop });

        // ---------- participants ----------
        _participantsTitle = Ui.Label(bold: true);
        _participantsHint = Ui.Label();
        _participantsHint.ForeColor = SystemColors.GrayText;
        _participantsHint.MaximumSize = new Size(300, 0);
        _people = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
            EditMode = DataGridViewEditMode.EditOnEnter,
            GridColor = SystemColors.ControlLight
        };
        _people.Columns.Add(new DataGridViewTextBoxColumn { Name = "Voice", ReadOnly = true, FillWeight = 40 });
        _people.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", FillWeight = 60 });
        _people.Columns.Add(new DataGridViewTextBoxColumn { Name = "Phrases", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells });
        _people.CellEndEdit += (_, e) => OnNameEdited(e.RowIndex);
        _people.EditingControlShowing += (_, e) => OfferKnownNames(e.Control as TextBox);
        _people.CellFormatting += (_, e) =>
        {
            // A name recognized by voice is shown in italics until the user confirms or changes it.
            if (e.ColumnIndex == 1 && e.RowIndex >= 0 && _people.Rows[e.RowIndex].Cells[1].Tag is float score)
            {
                e.CellStyle!.Font = _italic ??= new Font(_people.Font, FontStyle.Italic);
                e.CellStyle.ForeColor = Color.FromArgb(79, 70, 229);
                _people.Rows[e.RowIndex].Cells[1].ToolTipText = T.RecognizedByVoice(score);
            }
            else if (e.ColumnIndex == 1 && e.RowIndex >= 0)
            {
                _people.Rows[e.RowIndex].Cells[1].ToolTipText = "";
            }
        };
        _knownVoices = Ui.Button("", (_, _) => _app.ShowVoices(this));
        var participants = Column(_participantsTitle, _participantsHint, _people);
        ((TableLayoutPanel)participants).RowStyles.Add(new RowStyle(SizeType.AutoSize));
        ((TableLayoutPanel)participants).Controls.Add(_knownVoices, 0, 3);

        // ---------- transcript ----------
        _transcriptTitle = Ui.Label(bold: true);
        _transcriptHint = Ui.Label();
        _transcriptHint.ForeColor = SystemColors.GrayText;
        _transcriptHint.MaximumSize = new Size(600, 0);
        _transcript = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
            DetectUrls = false,
            Font = new Font("Segoe UI", 10F)
        };
        var transcript = Column(_transcriptTitle, _transcriptHint, _transcript);

        // ---------- assistant ----------
        _aiEnabled = new CheckBox { AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Margin = new Padding(3, 5, 3, 3) };
        _aiEnabled.Click += (_, _) => _app.SetAssistantEnabled(_aiEnabled.Checked);
        _aiModel = Ui.Label();
        _aiModel.ForeColor = SystemColors.GrayText;
        _aiSettings = Ui.Button("", (_, _) => _app.ShowAiSettings(this));
        var aiHead = Flow(_aiEnabled, _aiModel, _aiSettings);

        _aiEveryLabel = Ui.Label();
        _aiInterval = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, Anchor = AnchorStyles.Left };
        _aiInterval.SelectionChangeCommitted += (_, _) =>
            _app.SetAssistantInterval(AssistantSettings.Intervals[Math.Max(0, _aiInterval.SelectedIndex)]);
        _aiNow = Ui.Button("", (_, _) => _assistant?.Ask(null));
        _aiCost = Ui.Label();
        _aiCost.ForeColor = SystemColors.GrayText;
        var aiControls = Flow(_aiEveryLabel, _aiInterval, _aiNow, _aiCost);

        _aiNeedsLive = Ui.Label();
        _aiNeedsLive.ForeColor = Color.FromArgb(160, 90, 0);
        _aiNeedsLive.MaximumSize = new Size(520, 0);

        _summaryTitle = Ui.Label(bold: true);
        _aiSummary = NotesBox();
        _notesTitle = Ui.Label(bold: true);
        _aiNotes = NotesBox();
        _aiSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        _aiSplit.Panel1.Controls.Add(Pane(_summaryTitle, _aiSummary));
        _aiSplit.Panel2.Controls.Add(Pane(_notesTitle, _aiNotes));
        _question = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 3) };
        _question.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                AskQuestion();
                e.SuppressKeyPress = true;
            }
        };
        _ask = Ui.Button("", (_, _) => AskQuestion());
        var askRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        askRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        askRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        askRow.Controls.Add(_question, 0, 0);
        askRow.Controls.Add(_ask, 1, 0);

        var assistant = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(4) };
        assistant.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        assistant.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        assistant.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        assistant.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        assistant.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        assistant.Controls.Add(aiHead, 0, 0);
        assistant.Controls.Add(aiControls, 0, 1);
        assistant.Controls.Add(_aiNeedsLive, 0, 2);
        assistant.Controls.Add(_aiSplit, 0, 3);
        assistant.Controls.Add(askRow, 0, 4);

        // ---------- three columns: participants | transcript | assistant ----------
        _inner = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.None };
        _inner.Panel1.Controls.Add(transcript);
        _inner.Panel2.Controls.Add(assistant);
        _outer = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        _outer.Panel1.Controls.Add(participants);
        _outer.Panel2.Controls.Add(_inner);

        Controls.Add(_outer);
        Controls.Add(top);
        Ui.Done(this);

        // Splitter positions in pixels are set once the window has its real (DPI-scaled) size.
        Shown += (_, _) =>
        {
            _outer.SplitterDistance = Math.Max(LogicalToDeviceUnits(220), ClientSize.Width / 5);
            _inner.SplitterDistance = _inner.Width * 52 / 100;
            _aiSplit.SplitterDistance = _aiSplit.Height * 45 / 100;
        };

        _refresh = new System.Windows.Forms.Timer { Interval = 150 };
        _refresh.Tick += (_, _) => Flush();
        _refresh.Start();

        _app.StateChanged += OnAppState;
        _app.SessionChanged += Bind;
        FormClosed += (_, _) =>
        {
            _refresh.Dispose();
            _app.StateChanged -= OnAppState;
            _app.SessionChanged -= Bind;
            Unbind();
        };

        ApplyTexts();
        Bind();
    }

    static Control Column(Control title, Control hint, Control body)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(4) };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(title, 0, 0);
        panel.Controls.Add(hint, 0, 1);
        panel.Controls.Add(body, 0, 2);
        return panel;
    }

    static readonly TranscriptionMode[] Modes = { TranscriptionMode.Live, TranscriptionMode.AfterRecording, TranscriptionMode.Off };

    static RichTextBox NotesBox() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = SystemColors.Window,
        BorderStyle = BorderStyle.None,
        Font = new Font("Segoe UI", 10F)
    };

    static Control Pane(Control title, Control body)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = Padding.Empty };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(title, 0, 0);
        panel.Controls.Add(body, 0, 1);
        return panel;
    }

    static FlowLayoutPanel Flow(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = true };
        flow.Controls.AddRange(controls);
        return flow;
    }

    // ---------- texts and state ----------

    public void ApplyTexts()
    {
        Text = AppInfo.Name;
        _projectLabel.Text = T.Project;
        _notes.Text = T.ProjectNotes;
        _projectFolder.Text = T.ProjectFolder;
        _knownVoices.Text = T.KnownVoices;
        _start.Text = T.StartRecording;
        _stop.Text = T.Stop;
        _participantsTitle.Text = T.Participants;
        _participantsHint.Text = T.ParticipantsHint;
        _people.Columns["Voice"]!.HeaderText = T.ColumnVoice;
        _people.Columns["Name"]!.HeaderText = T.ColumnName;
        _people.Columns["Phrases"]!.HeaderText = T.ColumnPhrases;
        _transcriptTitle.Text = T.TranscriptTitle;
        _modeLabel.Text = T.RecognitionLabel;
        _mode.Items.Clear();
        _mode.Items.AddRange(new object[] { T.ModeLive, T.ModeAfter, T.ModeOff });
        _aiNeedsLive.Text = T.AiNeedsLive;
        _aiEnabled.Text = T.AiAssistant;
        _aiSettings.Text = T.AiSettingsButton;
        _aiEveryLabel.Text = T.AiRuns;
        _aiNow.Text = T.AiNow;
        _ask.Text = T.AiAsk;
        _question.PlaceholderText = T.AiQuestionHint;

        _aiInterval.Items.Clear();
        _aiInterval.Items.AddRange(AssistantSettings.Intervals.Select(s => (object)T.AiInterval(s)).ToArray());

        _summaryShown = _notesShown = null;
        _peopleDirty = _transcriptDirty = _notesDirty = true;
        OnAppState();
    }

    void OnAppState()
    {
        if (IsDisposed)
            return;

        var settings = _app.Settings;
        bool recording = _app.IsRecording;
        _start.Enabled = !recording;
        _pause.Enabled = _stop.Enabled = recording;
        _pause.Text = _app.IsPaused ? T.Resume : T.Pause;

        string state;
        if (recording)
        {
            state = $"{(_app.IsPaused ? "❚❚ " + T.StatePaused : "● " + T.StateRecording)} {Transcript.Time(_app.Duration)}";
            _state.ForeColor = _app.IsPaused ? Color.FromArgb(200, 130, 0) : Color.FromArgb(210, 30, 30);
        }
        else
        {
            state = T.StateIdle;
            _state.ForeColor = SystemColors.GrayText;
        }
        if (_app.Work is { } w)
            state += "   ·   " + (w.Fraction is { } f ? $"{w.Text} — {f:P0}" : w.Text);
        _state.Text = state;

        // Project list: most recently used first.
        if (!_project.Focused)
        {
            _updatingProject = true;
            var names = settings.Projects.Select(p => p.Name).ToArray();
            if (!_project.Items.Cast<string>().SequenceEqual(names))
            {
                _project.Items.Clear();
                _project.Items.AddRange(names);
            }
            _project.Text = settings.Project ?? "";
            _updatingProject = false;
        }
        _notes.Enabled = settings.Project != null;

        // Recognition mode: chosen before a call; the assistant needs the live transcript.
        bool live = _app.Mode == TranscriptionMode.Live;
        int mode = Array.IndexOf(Modes, _app.Mode);
        if (_mode.SelectedIndex != mode)
            _mode.SelectedIndex = mode;
        _mode.Enabled = !recording;
        _transcriptHint.Text = live ? "" : _app.Mode == TranscriptionMode.Off ? T.RecognitionOffHint : T.LiveOffHint;
        _transcriptHint.Visible = !live;
        _aiNeedsLive.Visible = !live;

        var a = settings.Assistant;
        if (_aiEnabled.Checked != a.Enabled || _aiEnabled.Enabled != live)
            _notesDirty = true; // the hints in the empty panes depend on it
        _aiEnabled.Checked = a.Enabled;
        _aiEnabled.Enabled = _aiInterval.Enabled = live;
        _aiModel.Text = a.Model == null ? T.AiNoModelChosen
            : $"{(a.Provider == LlmProvider.LmStudio ? "LM Studio" : "OpenRouter")} · {a.Model}";
        int index = Array.IndexOf(AssistantSettings.Intervals, a.IntervalSeconds);
        if (_aiInterval.SelectedIndex != index && index >= 0)
            _aiInterval.SelectedIndex = index;
        bool canAsk = live && _assistant != null && a.Model != null;
        _aiNow.Enabled = _ask.Enabled = _question.Enabled = canAsk;
        _aiCost.Text = _assistant is { CostKnown: true } x ? T.AiCost(x.Cost) : "";
    }

    // ---------- session binding ----------

    void Bind()
    {
        Unbind();
        _session = _app.Session;
        _assistant = _app.Assistant;
        if (_session != null)
        {
            _session.Changed += OnSessionChanged;
            _session.LineAdded += OnLineAdded;
        }
        if (_assistant != null)
        {
            _assistant.NotesChanged += OnNotesChanged;
            _assistant.NoteUpdated += OnNoteUpdated;
        }
        lock (_pendingLines) _pendingLines.Clear();
        _transcriptDirty = _peopleDirty = _notesDirty = true;
        OnAppState();
    }

    void Unbind()
    {
        if (_session != null)
        {
            _session.Changed -= OnSessionChanged;
            _session.LineAdded -= OnLineAdded;
        }
        if (_assistant != null)
        {
            _assistant.NotesChanged -= OnNotesChanged;
            _assistant.NoteUpdated -= OnNoteUpdated;
        }
    }

    // These come from worker threads: just mark what to redraw; the timer redraws on the UI thread.
    void OnSessionChanged() => _transcriptDirty = _peopleDirty = true;
    void OnNotesChanged() => _notesDirty = true;
    void OnNoteUpdated(AssistantNote _) => _notesDirty = true;

    void OnLineAdded(Utterance u)
    {
        lock (_pendingLines) _pendingLines.Add(u);
        _peopleDirty = true;
    }

    void Flush()
    {
        if (_transcriptDirty)
        {
            _transcriptDirty = false;
            lock (_pendingLines) _pendingLines.Clear();
            RenderTranscript();
        }
        else
        {
            Utterance[] lines;
            lock (_pendingLines)
            {
                lines = _pendingLines.ToArray();
                _pendingLines.Clear();
            }
            if (lines.Length > 0)
                AppendLines(lines, keepAtBottom: Ui.IsAtBottom(_transcript));
        }

        if (_peopleDirty && !_people.IsCurrentCellInEditMode)
        {
            _peopleDirty = false;
            RenderPeople();
        }

        if (_notesDirty)
        {
            _notesDirty = false;
            RenderNotes();
            OnAppState();
        }
    }

    // ---------- transcript ----------

    void RenderTranscript()
    {
        _transcript.SuspendLayout();
        _transcript.Clear();
        if (_session == null)
        {
            _transcript.SelectionColor = SystemColors.GrayText;
            _transcript.AppendText(T.NoCallYet);
        }
        else
        {
            var title = Path.GetFileName(_session.AudioPath) + (_session.IsDraft ? "  ·  " + T.DraftNote : "");
            _transcript.SelectionColor = SystemColors.GrayText;
            _transcript.AppendText(title + "\n\n");
            AppendLines(_session.Lines, keepAtBottom: true);
        }
        _transcript.ResumeLayout();
    }

    void AppendLines(IEnumerable<Utterance> lines, bool keepAtBottom)
    {
        if (_session == null)
            return;
        var keys = _session.Speakers.Select(s => s.Key).ToList();
        foreach (var u in lines)
        {
            _transcript.SelectionStart = _transcript.TextLength;
            _transcript.SelectionColor = SystemColors.GrayText;
            _transcript.SelectionFont = _transcript.Font;
            _transcript.AppendText($"[{Transcript.Time(u.Start)}] ");
            _transcript.SelectionColor = ColorOf(u.Speaker, keys);
            _transcript.SelectionFont = new Font(_transcript.Font, FontStyle.Bold);
            _transcript.AppendText(_session.Label(u.Speaker) + ": ");
            _transcript.SelectionColor = SystemColors.WindowText;
            _transcript.SelectionFont = _transcript.Font;
            _transcript.AppendText(u.Text + "\n");
        }
        if (keepAtBottom)
            Ui.ScrollToEnd(_transcript);
    }

    static Color ColorOf(string key, List<string> keys)
    {
        if (key == CallSession.MeKey)
            return MeColor;
        int i = keys.Where(k => k != CallSession.MeKey).ToList().IndexOf(key);
        return SpeakerColors[Math.Max(0, i) % SpeakerColors.Length];
    }

    // ---------- participants ----------

    void RenderPeople()
    {
        var speakers = _session?.Speakers ?? new();
        _participantsHint.Text = _session == null ? "" : speakers.Count == 0 ? T.ParticipantsEmpty : T.ParticipantsHint;

        // Update in place so that the selection and scroll position survive.
        var keys = speakers.Select(s => s.Key).ToList();
        while (_people.Rows.Count > speakers.Count)
            _people.Rows.RemoveAt(_people.Rows.Count - 1);
        for (int i = 0; i < speakers.Count; i++)
        {
            var (key, label, defaultLabel, phrases, recognized) = speakers[i];
            if (i == _people.Rows.Count)
                _people.Rows.Add();
            var row = _people.Rows[i];
            row.Tag = key;
            row.Cells[0].Value = defaultLabel;
            row.Cells[0].Style.ForeColor = ColorOf(key, keys);
            row.Cells[1].Value = label == defaultLabel ? "" : label;
            row.Cells[1].Tag = recognized;
            row.Cells[2].Value = phrases;
            _people.InvalidateRow(i); // recognized names are styled in CellFormatting
        }
    }

    void OnNameEdited(int row)
    {
        if (row < 0 || row >= _people.Rows.Count || _people.Rows[row].Tag is not string key)
            return;
        var name = _people.Rows[row].Cells[1].Value as string;
        BeginInvoke(() => _app.RenameSpeaker(key, name)); // after the grid has finished its edit
    }

    void OfferKnownNames(TextBox? box)
    {
        if (box == null || _people.CurrentCell?.ColumnIndex != 1)
            return;
        var names = new AutoCompleteStringCollection();
        if (_app.Settings.FindProject(_session?.Project) is { } project)
            names.AddRange(project.People.ToArray());
        if (_app.Settings.MyName is { } me)
            names.Add(me);
        box.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        box.AutoCompleteSource = AutoCompleteSource.CustomSource;
        box.AutoCompleteCustomSource = names;
    }

    // ---------- project ----------

    void CommitProject()
    {
        if (_updatingProject)
            return;
        var name = string.IsNullOrWhiteSpace(_project.Text) ? null : _project.Text.Trim();
        if (string.Equals(name, _app.Settings.Project, StringComparison.Ordinal))
            return;
        _app.SetProject(name);
    }

    void EditProjectNotes()
    {
        CommitProject();
        if (_app.Settings.FindProject(_app.Settings.Project) is not { } project)
            return;
        var notes = Ui.Prompt(this, T.ProjectNotesTitle(project.Name), T.ProjectNotesHint, project.Notes, multiline: true);
        if (notes != null)
            _app.SetProjectNotes(notes);
    }

    // ---------- assistant ----------

    void AskQuestion()
    {
        var question = _question.Text.Trim();
        if (question.Length == 0 || _assistant == null || _app.Settings.Assistant.Model == null)
            return;
        _question.Clear();
        _assistant.Ask(question);
    }

    /// <summary>
    /// Two panes: the summary (one current version, replaced by each update and by the summary of the
    /// whole call at the end) and the notes (suggestions and doubts of the latest update, then the
    /// answers to the user's questions, newest first). While an update is being written the panes keep
    /// the previous text and say "updating"; they are redrawn only when what they show changes.
    /// </summary>
    void RenderNotes()
    {
        var a = _assistant;
        bool live = _app.Mode == TranscriptionMode.Live;
        bool enabled = _app.Settings.Assistant.Enabled;
        bool updating = a?.IsUpdating == true;
        var (summary, summaryAt) = a?.Summary ?? (null, null);
        var (notes, notesAt) = a?.CurrentNotes ?? (null, null);
        var questions = a?.Questions ?? Array.Empty<AssistantNote>();

        _summaryTitle.Text = T.AiSummaryTitle + When(summaryAt) +
            (a?.SummaryIsFinal == true ? "  ·  " + T.AiFinalMark : "") + (updating ? "  ·  " + T.AiUpdating : "");
        _notesTitle.Text = T.AiNotesTitle + When(notesAt) + (updating && a?.SummaryIsFinal != true ? "  ·  " + T.AiUpdating : "");

        // ----- summary -----
        string? idle = !live ? T.AiNeedsLive : !enabled ? T.AiOffHint : null;
        var summaryKey = string.Join("\u0001", idle, summary, a?.LastError, summary == null && updating);
        if (summaryKey != _summaryShown)
        {
            _summaryShown = summaryKey;
            Begin(_aiSummary);
            if (summary != null)
                AppendMarkdown(_aiSummary, summary);
            else if (live)
                AppendGray(_aiSummary, idle ?? (updating ? T.AiThinking : T.AiWaiting)); // not live: the line above the panes says why
            if (a?.LastError is { } error)
                AppendColored(_aiSummary, "⚠ " + error, Color.Firebrick);
            End(_aiSummary);
        }

        // ----- notes and answers -----
        var notesKey = new StringBuilder().Append(idle).Append('\u0001').Append(notes).Append('\u0001').Append(a?.SummaryIsFinal);
        foreach (var q in questions)
            notesKey.Append('\u0001').Append(q.Title).Append(q.Text).Append(q.Done).Append(q.Error);
        if (notesKey.ToString() != _notesShown)
        {
            _notesShown = notesKey.ToString();
            Begin(_aiNotes);
            if (notes != null)
                AppendMarkdown(_aiNotes, notes.Length > 0 ? notes : T.AiNothingToSuggest);
            else if (idle == null && a?.SummaryIsFinal != true)
                AppendGray(_aiNotes, T.AiNotesWaiting);

            foreach (var q in questions)
            {
                _aiNotes.AppendText("\n");
                AppendColored(_aiNotes, q.Title, Color.FromArgb(79, 70, 229), bold: true);
                if (q.Text.Length > 0)
                    AppendMarkdown(_aiNotes, q.Text);
                else if (!q.Done)
                    AppendGray(_aiNotes, T.AiThinking);
                if (q.Error != null)
                    AppendColored(_aiNotes, "⚠ " + q.Error, Color.Firebrick);
            }
            End(_aiNotes);
        }
    }

    static string When(DateTime? at) => at is { } t ? $"  ·  {t:HH:mm}" : "";

    static void Begin(RichTextBox box)
    {
        box.SuspendLayout();
        box.Clear();
    }

    static void End(RichTextBox box)
    {
        box.SelectionStart = 0;
        box.ScrollToCaret();
        box.ResumeLayout();
    }

    static void AppendGray(RichTextBox box, string text) => AppendColored(box, text, SystemColors.GrayText);

    static void AppendColored(RichTextBox box, string text, Color color, bool bold = false)
    {
        box.SelectionStart = box.TextLength;
        box.SelectionColor = color;
        box.SelectionFont = bold ? new Font(box.Font, FontStyle.Bold) : box.Font;
        box.AppendText(text + "\n");
    }

    /// <summary>Just enough Markdown for the notes: headings, bullets and **bold**.</summary>
    static void AppendMarkdown(RichTextBox box, string text)
    {
        var bold = new Font(box.Font, FontStyle.Bold);
        bool first = true;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
                continue;

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                if (!first)
                    box.AppendText("\n");
                box.SelectionColor = Color.FromArgb(79, 70, 229);
                box.SelectionFont = bold;
                box.AppendText(trimmed.TrimStart('#', ' ') + "\n");
                first = false;
                continue;
            }
            first = false;

            int indent = line.Length - trimmed.Length;
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                trimmed = (indent > 1 ? "      ◦ " : "  • ") + trimmed[2..].TrimStart();

            var parts = trimmed.Split("**");
            for (int i = 0; i < parts.Length; i++)
            {
                box.SelectionColor = SystemColors.WindowText;
                box.SelectionFont = i % 2 == 1 ? bold : box.Font;
                box.AppendText(parts[i]);
            }
            box.AppendText("\n");
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Finish typing a name before the window goes away.
        if (_people.IsCurrentCellInEditMode)
            _people.EndEdit();
        base.OnFormClosing(e);
    }
}
