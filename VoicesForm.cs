namespace AiCallAssistant;

/// <summary>People whose voices the app remembers, with a way to forget them.</summary>
sealed class VoicesForm : Form
{
    readonly VoiceLibrary _voices;
    readonly ListView _list;
    readonly Button _delete;

    static Strings T => Strings.Current;

    public VoicesForm(VoiceLibrary voices)
    {
        _voices = voices;
        Text = T.VoicesTitle;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Ui.Prepare(this);
        ClientSize = new Size(640, 400);
        MinimumSize = new Size(420, 260);

        var hint = Ui.Label(T.VoicesHint);
        hint.MaximumSize = new Size(600, 0);
        hint.ForeColor = SystemColors.GrayText;

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true,
            HideSelection = false
        };
        _list.Columns.Add(T.ColumnName, 180);
        _list.Columns.Add(T.VoicesCalls, 70, HorizontalAlignment.Right);
        _list.Columns.Add(T.VoicesLastHeard, 130);
        _list.Columns.Add(T.VoicesProjects, 200);
        _list.SelectedIndexChanged += (_, _) => _delete!.Enabled = _list.SelectedItems.Count > 0;
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
                DeleteSelected();
        };

        _delete = Ui.Button(T.VoicesForget, (_, _) => DeleteSelected());
        _delete.Enabled = false;
        var close = Ui.Button(T.Close);
        close.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { close, _delete });

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(hint, 0, 0);
        layout.Controls.Add(_list, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        CancelButton = close;
        Ui.Done(this);

        Fill();
        // Column widths above are in 96-DPI units; scale them for this screen.
        Shown += (_, _) =>
        {
            foreach (ColumnHeader c in _list.Columns)
                c.Width = LogicalToDeviceUnits(c.Width);
        };
    }

    void Fill()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var (name, samples, lastSeen, projects) in _voices.List())
            _list.Items.Add(new ListViewItem(new[] { name, samples.ToString(), lastSeen.ToString("g"), projects }) { Tag = name });
        _list.EndUpdate();
        _delete.Enabled = false;
    }

    void DeleteSelected()
    {
        var names = _list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList();
        if (names.Count == 0)
            return;
        if (MessageBox.Show(this, T.VoicesForgetConfirm(string.Join(", ", names)), Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        foreach (var name in names)
            _voices.Delete(name);
        Fill();
    }
}
