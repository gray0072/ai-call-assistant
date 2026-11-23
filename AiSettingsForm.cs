using System.Diagnostics;
using System.Globalization;

namespace AiCallAssistant;

/// <summary>
/// Where the assistant's model runs: LM Studio on this machine or OpenRouter in the cloud (API key,
/// models with prices, most popular first). The last chosen model of each source is remembered.
/// </summary>
sealed class AiSettingsForm : Form
{
    readonly AppSettings _settings;
    readonly RadioButton _local, _cloud;
    readonly Label _urlLabel, _keyLabel, _keyStatus, _modelInfo;
    readonly TextBox _url, _key;
    readonly LinkLabel _getKey;
    readonly Button _checkKey, _refresh;
    readonly ComboBox _model;
    readonly TableLayoutPanel _grid;

    string? _localModel, _cloudModel;
    int? _localContext;
    List<LlmModel> _models = new();
    CancellationTokenSource? _loading;
    bool _keyChanged;

    static Strings T => Strings.Current;

    sealed record Item(LlmModel Model)
    {
        public override string ToString()
        {
            var m = Model;
            if (m.IsLocal)
                return $"{m.Id}  —  {(m.Loaded ? T.AiLoaded + ", " : "")}{(m.Context is { } ctx ? T.AiContext(ctx) : "")}";
            if (m.Free)
                return $"{m.Name}  —  {T.AiFree}";
            return $"{m.Name}  —  {Price(m.InputPrice)} / {Price(m.OutputPrice)} {T.AiPerMillion}";
        }

        static string Price(decimal? p) => p is { } v ? "$" + v.ToString(v < 1 ? "0.###" : "0.##", CultureInfo.InvariantCulture) : "?";
    }

    public AiSettingsForm(AppSettings settings)
    {
        _settings = settings;
        var a = settings.Assistant;
        _localModel = a.LocalModel;
        _cloudModel = a.CloudModel;
        _localContext = a.LocalContext;

        Text = T.AiSettingsTitle;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Ui.Prepare(this);

        _local = new RadioButton { Text = T.AiLocal, AutoSize = true, Checked = a.Provider == LlmProvider.LmStudio };
        _cloud = new RadioButton { Text = T.AiCloud, AutoSize = true, Checked = a.Provider == LlmProvider.OpenRouter };
        _local.CheckedChanged += (_, _) => { if (_local.Checked) OnProviderChanged(); };
        _cloud.CheckedChanged += (_, _) => { if (_cloud.Checked) OnProviderChanged(); };
        var providers = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
        providers.Controls.AddRange(new Control[] { _local, _cloud });

        _urlLabel = Ui.Label(T.AiLmStudioUrl);
        _url = new TextBox { Text = a.LmStudioUrl, Dock = DockStyle.Fill };
        _url.Leave += (_, _) => { if (_local.Checked) LoadModels(); };

        _keyLabel = Ui.Label(T.AiApiKey);
        _key = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill, PlaceholderText = "sk-or-…" };
        if (a.OpenRouterKey is { } existing)
            _key.Text = existing;
        _key.TextChanged += (_, _) => _keyChanged = true;
        _checkKey = Ui.Button(T.AiCheckKey, (_, _) => LoadModels());
        _getKey = new LinkLabel { Text = T.AiGetKey, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 6) };
        _getKey.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(LlmClient.OpenRouterKeysPage) { UseShellExecute = true });
        _keyStatus = Ui.Label();
        _keyStatus.MaximumSize = new Size(560, 0);

        _model = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            MaxDropDownItems = 25,
            MinimumSize = new Size(480, 0)
        };
        _model.SelectedIndexChanged += (_, _) => OnModelSelected();
        _refresh = Ui.Button(T.AiRefresh, (_, _) => LoadModels());
        _modelInfo = Ui.Label();
        _modelInfo.ForeColor = SystemColors.GrayText;
        _modelInfo.MaximumSize = new Size(560, 0);

        var ok = Ui.Button("OK", (_, _) => Save());
        var cancel = Ui.Button(T.Cancel);
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange(new Control[] { cancel, ok });

        _grid = new TableLayoutPanel
        {
            ColumnCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
            Dock = DockStyle.Fill
        };
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void Row(Control? label, Control control, Control? extra = null)
        {
            int row = _grid.RowCount++;
            _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (label != null)
                _grid.Controls.Add(label, 0, row);
            _grid.Controls.Add(control, 1, row);
            if (extra != null)
                _grid.Controls.Add(extra, 2, row);
            else
                _grid.SetColumnSpan(control, 2);
        }

        Row(Ui.Label(T.AiSource), providers);
        Row(_urlLabel, _url);
        Row(_keyLabel, _key, _checkKey);
        Row(null, _getKey);
        Row(null, _keyStatus);
        Row(Ui.Label(T.AiModel), _model, _refresh);
        Row(null, _modelInfo);
        Row(null, buttons);
        _grid.SetColumnSpan(buttons, 2);

        Controls.Add(_grid);
        AcceptButton = ok;
        CancelButton = cancel;
        Ui.Done(this);

        Shown += (_, _) => OnProviderChanged();
        FormClosed += (_, _) => _loading?.Cancel();
    }

    bool Local => _local.Checked;

    void OnProviderChanged()
    {
        _urlLabel.Visible = _url.Visible = Local;
        _keyLabel.Visible = _key.Visible = _checkKey.Visible = _getKey.Visible = _keyStatus.Visible = !Local;
        LoadModels();
    }

    async void LoadModels()
    {
        _loading?.Cancel();
        var cts = _loading = new CancellationTokenSource();
        bool local = Local;

        _model.Items.Clear();
        _model.Enabled = false;
        _refresh.Enabled = false;
        _modelInfo.Text = T.AiLoadingModels;

        try
        {
            if (!local)
            {
                var key = _key.Text.Trim();
                if (key.Length == 0)
                {
                    _keyStatus.Text = T.AiEnterKey;
                    _modelInfo.Text = "";
                    return;
                }
                _keyStatus.Text = T.AiCheckingKey;
                var (usage, limit) = await LlmClient.CheckKeyAsync(key, cts.Token);
                _keyStatus.Text = T.AiKeyOk(usage, limit);
            }

            var models = local
                ? await LlmClient.ListLocalAsync(_url.Text, cts.Token)
                : await LlmClient.ListCloudAsync(cts.Token);
            if (cts.IsCancellationRequested)
                return;

            _models = models;
            _model.Items.AddRange(models.Select(m => (object)new Item(m)).ToArray());
            var remembered = local ? _localModel : _cloudModel;
            int index = models.FindIndex(m => m.Id == remembered);
            if (index < 0 && local)
                index = models.FindIndex(m => m.Loaded); // a model LM Studio already has in memory
            _model.SelectedIndex = models.Count == 0 ? -1 : Math.Max(index, 0);
            _model.Enabled = models.Count > 0;
            _modelInfo.Text = models.Count == 0 ? (local ? T.AiNoLocalModels : T.AiNoCloudModels) : _modelInfo.Text;
            if (models.Count > 0)
                OnModelSelected();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested)
                return;
            var message = ex.GetBaseException().Message;
            if (local)
                _modelInfo.Text = T.AiLmStudioUnavailable(message);
            else
            {
                _keyStatus.Text = T.AiKeyFailed(message);
                _modelInfo.Text = "";
            }
        }
        finally
        {
            if (!cts.IsCancellationRequested)
                _refresh.Enabled = true;
        }
    }

    void OnModelSelected()
    {
        if (_model.SelectedItem is not Item { Model: var m })
            return;

        if (Local)
        {
            _localModel = m.Id;
            _localContext = m.Context;
            _modelInfo.Text = m.Loaded ? T.AiLocalLoadedInfo : T.AiLocalJitInfo;
        }
        else
        {
            _cloudModel = m.Id;
            _modelInfo.Text = $"{m.Id}" + (m.Context is { } c ? $" · {T.AiContext(c)}" : "");
        }
    }

    void Save()
    {
        var a = _settings.Assistant;
        a.Provider = Local ? LlmProvider.LmStudio : LlmProvider.OpenRouter;
        a.LmStudioUrl = string.IsNullOrWhiteSpace(_url.Text) ? "http://localhost:1234" : _url.Text.Trim();
        a.LocalModel = _localModel;
        a.LocalContext = _localContext;
        a.CloudModel = _cloudModel;
        if (_keyChanged)
            a.OpenRouterKey = _key.Text;
        DialogResult = DialogResult.OK;
    }
}
