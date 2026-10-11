// Configurações (desenho §9): abas Sessão, Tampa, Agentes, Bateria, Rede e App. Toda mudança
// é gravada na hora via IConfigSource.Update (sem botão "Salvar"), que normaliza os pisos.
// "Iniciar com o Windows" reflete o que está registrado no HKCU Run, não só o config.json.
// Leituras lentas (processos, perfis Wi-Fi) rodam fora do thread da UI.
using System.Collections.Immutable;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Lid;
using Insomnia.Core.Model;
using Insomnia.Core.Time;

namespace Insomnia.App.Settings;

public sealed class SettingsForm : Form
{
    private readonly IConfigSource _config;
    private readonly IAutoStart _autoStart;
    private readonly IWifi _wifi;
    private readonly IProcessInspector _inspector;
    private readonly string _exePath;
    private readonly ILog _log;
    private bool _loading;
    private int _wifiCheck;

    // Sessão
    private readonly ListBox _presets = new() { Width = 300, Height = 140, IntegralHeight = false };
    private readonly TextBox _presetInput = new() { Width = 200, PlaceholderText = "30m, 2h, 1h30m, 3d" };
    private readonly Button _presetAdd = new() { Text = "Adicionar", AutoSize = true, Enabled = false };
    private readonly Button _presetRemove = new() { Text = "Remover", AutoSize = true, Enabled = false };
    private readonly ComboBox _defaultPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly Label _presetError = new() { AutoSize = true, ForeColor = Color.Firebrick };
    private readonly Label _maxDuration = new() { AutoSize = true };

    // Tampa
    private readonly CheckBox _darken = Check("Apagar a tela ao fechar a tampa (monitores externos também apagam)");
    private readonly NameListEditor _freeze;
    private readonly CheckBox _freezeAll = Check("Congelar também todos os outros apps com janela");
    private readonly Label _wouldFreeze = Note("");
    private readonly CheckBox _docker = Check("Pausar o Docker Desktop quando nenhum contêiner estiver rodando");
    private readonly CheckBox _mute = Check("Mutar o áudio ao fechar a tampa");
    private readonly CheckBox _energy = Check("Ligar a Economia de energia com a tampa fechada (na bateria)");

    // Agentes
    private readonly NameListEditor _agents;

    // Bateria
    private readonly NumericUpDown _lowFloor = new() { Minimum = 0, Maximum = 100, Increment = Config.FloorStep, Width = 70 };
    private readonly NumericUpDown _endFloor = new() { Minimum = 0, Maximum = Config.MaxEndFloor, Increment = Config.FloorStep, Width = 70 };

    // Rede
    private readonly TextBox _ssid = new() { Width = 260, PlaceholderText = "nome da rede do hotspot" };
    private readonly Label _profileState = Note("");

    // App
    private readonly CheckBox _launchAtLogin = Check("Iniciar com o Windows");
    private readonly Label _launchError = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(480, 0) };

    public SettingsForm(IConfigSource config, IAutoStart autoStart, IWifi wifi, IProcessInspector inspector,
        string configPath, string exePath, ILog log)
    {
        _config = config;
        _autoStart = autoStart;
        _wifi = wifi;
        _inspector = inspector;
        _exePath = exePath;
        _log = log;

        Text = "Configurações do Insomnia";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(580, 640);
        MinimumSize = new Size(520, 520);
        Font = new Font("Segoe UI", 9.5f);
        ShowInTaskbar = true;

        _freeze = new NameListEditor("Congelar enquanto a tampa estiver fechada", RunningAppsAsync);
        _agents = new NameListEditor("Nunca congelar (apps de agentes e ferramentas)", RunningAppsAsync);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("Sessão", BuildSessionTab()));
        tabs.TabPages.Add(Page("Tampa", BuildLidTab()));
        tabs.TabPages.Add(Page("Agentes", BuildAgentsTab()));
        tabs.TabPages.Add(Page("Bateria", BuildBatteryTab()));
        tabs.TabPages.Add(Page("Rede", BuildNetworkTab()));
        tabs.TabPages.Add(Page("App", BuildAppTab(configPath)));
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedTab?.Text == "Tampa") _ = RefreshWouldFreezeAsync();
            if (tabs.SelectedTab?.Text == "Rede") _ = RefreshProfileStateAsync();
        };
        Controls.Add(tabs);

        _config.Changed += OnConfigChanged;
        WireEvents();
    }

    /// Mostra (ou traz para a frente) com os valores atuais.
    public void ShowAndActivate()
    {
        LoadFromConfig();
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Fechar só esconde: a janela é reaberta pelo menu sem recriar tudo.
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _config.Changed -= OnConfigChanged;
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ abas

    private Control BuildSessionTab()
    {
        var col = Column();
        col.Controls.Add(Heading("Presets"));
        col.Controls.Add(_presets);
        col.Controls.Add(Row(_presetInput, _presetAdd, _presetRemove));
        col.Controls.Add(_presetError);
        col.Controls.Add(Row(new Label { Text = "Preset padrão:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, _defaultPreset));
        col.Controls.Add(_maxDuration);
        col.Controls.Add(Note("Enter sem valor no popover inicia o preset padrão."));
        return col;
    }

    private Control BuildLidTab()
    {
        var col = Column();
        col.Controls.Add(_darken);
        col.Controls.Add(_freeze);
        col.Controls.Add(_freezeAll);
        col.Controls.Add(_wouldFreeze);
        col.Controls.Add(_docker);
        col.Controls.Add(Note("Confere duas vezes que não há contêiner rodando (antes de registrar e logo antes de pausar). " +
                              "A VM do WSL2 continua rodando: só a interface e o backend do Docker Desktop param."));
        col.Controls.Add(_mute);
        col.Controls.Add(_energy);
        col.Controls.Add(Note("Nunca são congelados: o próprio Insomnia, o Windows, navegadores, terminais, editores, apps de " +
                              "agentes e LLM, reuniões (Zoom, Teams, Webex, OBS), VPN e gerenciadores de senha, WSL e tudo da aba Agentes. " +
                              "Um app posto na lista acima é congelado mesmo se for protegido, menos os da aba Agentes."));
        return col;
    }

    private Control BuildAgentsTab()
    {
        var col = Column();
        col.Controls.Add(_agents);
        col.Controls.Add(Note("Estes apps nunca são congelados, nem quando estão na lista de congelados."));
        return col;
    }

    private Control BuildBatteryTab()
    {
        var col = Column();
        col.Controls.Add(Row(new Label { Text = "Ligar a Economia de energia abaixo de (%):", AutoSize = true, Margin = new Padding(0, 4, 6, 0) }, _lowFloor));
        col.Controls.Add(Row(new Label { Text = "Encerrar a sessão abaixo de (%):", AutoSize = true, Margin = new Padding(0, 4, 6, 0) }, _endFloor));
        col.Controls.Add(Note("Passo de 5%. 0 desliga o fim por bateria. O piso de fim fica sempre abaixo do piso da Economia de energia: " +
                              "mudar um pode ajustar o outro."));
        return col;
    }

    private Control BuildNetworkTab()
    {
        var col = Column();
        col.Controls.Add(Heading("Hotspot de reserva"));
        col.Controls.Add(Row(new Label { Text = "SSID:", AutoSize = true, Margin = new Padding(0, 4, 6, 0) }, _ssid));
        col.Controls.Add(_profileState);
        col.Controls.Add(Note("O Insomnia não guarda senha: quando o Wi-Fi cai durante uma sessão, ele conecta pelo perfil que o " +
                              "Windows já salvou. Conecte-se ao hotspot uma vez pelo Windows para criar o perfil."));
        return col;
    }

    private Control BuildAppTab(string configPath)
    {
        var col = Column();
        col.Controls.Add(_launchAtLogin);
        col.Controls.Add(_launchError);
        col.Controls.Add(Heading("Arquivo de configuração"));
        col.Controls.Add(new TextBox { Text = configPath, ReadOnly = true, Width = 480, BorderStyle = BorderStyle.FixedSingle });
        if (Composition.AppHost.IsSimulationBuild)
            col.Controls.Add(new Label { Text = "Build com simulação de tampa (arquivo simulate-lid).", AutoSize = true, ForeColor = Color.DarkOrange });
        return col;
    }

    // ------------------------------------------------------------------ eventos

    private void WireEvents()
    {
        _presets.SelectedIndexChanged += (_, _) => _presetRemove.Enabled = _presets.SelectedIndex >= 0 && _presets.Items.Count > 1;
        _presetInput.TextChanged += (_, _) => _presetAdd.Enabled = DurationParser.Parse(_presetInput.Text) is not null;
        _presetInput.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddPreset(); } };
        _presetAdd.Click += (_, _) => AddPreset();
        _presetRemove.Click += (_, _) => RemovePreset();
        _defaultPreset.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _defaultPreset.SelectedIndex < 0) return;
            var seconds = _config.Current.Presets[_defaultPreset.SelectedIndex];
            Apply(c => c with { DefaultPreset = seconds });
        };

        _darken.CheckedChanged += (_, _) => Apply(c => c with { DarkenDisplayOnLidClose = _darken.Checked });
        _freeze.ListChanged += list => { Apply(c => c with { FreezeList = list }); _ = RefreshWouldFreezeAsync(); };
        _freezeAll.CheckedChanged += (_, _) => { Apply(c => c with { FreezeAllApps = _freezeAll.Checked }); _ = RefreshWouldFreezeAsync(); };
        _docker.CheckedChanged += (_, _) => Apply(c => c with { DockerRule = _docker.Checked });
        _mute.CheckedChanged += (_, _) => Apply(c => c with { MuteOnLidClose = _mute.Checked });
        _energy.CheckedChanged += (_, _) => Apply(c => c with { EnergySaverOnLidClose = _energy.Checked });
        _agents.ListChanged += list => { Apply(c => c with { AgentList = list }); _ = RefreshWouldFreezeAsync(); };

        _lowFloor.ValueChanged += (_, _) => Apply(c => c.WithLowPowerFloor(Snap(_lowFloor.Value)));
        _endFloor.ValueChanged += (_, _) => Apply(c => c.WithEndFloor(Snap(_endFloor.Value)));

        _ssid.Leave += (_, _) => SaveSsid();
        _ssid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SaveSsid(); } };

        _launchAtLogin.CheckedChanged += (_, _) => ToggleLaunchAtLogin();
    }

    private static int Snap(decimal value) => (int)Math.Round(value / Config.FloorStep) * Config.FloorStep;

    private void Apply(Func<Config, Config> change)
    {
        if (_loading) return;
        try { _config.Update(change); }
        catch (Exception ex) { _log.Error($"configurações: não deu para aplicar: {ex.Message}"); }
    }

    private void OnConfigChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(LoadFromConfig); return; }
        LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        var cfg = _config.Current;
        _loading = true;
        try
        {
            _presets.BeginUpdate();
            _presets.Items.Clear();
            _defaultPreset.Items.Clear();
            foreach (var seconds in cfg.Presets)
            {
                var chip = DurationFormat.Chip(TimeSpan.FromSeconds(seconds));
                _presets.Items.Add(seconds == cfg.DefaultPreset ? chip + "  (padrão)" : chip);
                _defaultPreset.Items.Add(chip);
            }
            _presets.EndUpdate();
            _defaultPreset.SelectedIndex = cfg.Presets.IndexOf(cfg.DefaultPreset);
            _presetRemove.Enabled = false;
            _maxDuration.Text = $"Sessão máxima: {DurationFormat.Chip(TimeSpan.FromSeconds(cfg.MaxDuration))} (somente leitura)";

            _darken.Checked = cfg.DarkenDisplayOnLidClose;
            _freeze.SetItems(cfg.FreezeList);
            _freezeAll.Checked = cfg.FreezeAllApps;
            _docker.Checked = cfg.DockerRule;
            _mute.Checked = cfg.MuteOnLidClose;
            _energy.Checked = cfg.EnergySaverOnLidClose;
            _agents.SetItems(cfg.AgentList);

            _lowFloor.Value = Math.Clamp(cfg.LowPowerFloor, 0, 100);
            _endFloor.Value = Math.Clamp(cfg.EndFloor, 0, Config.MaxEndFloor);

            if (!_ssid.Focused) _ssid.Text = cfg.HotspotSsid;

            bool registered;
            try { registered = _autoStart.IsRegistered(); _launchError.Text = ""; }
            catch (Exception ex) { registered = cfg.LaunchAtLogin; _launchError.Text = $"Inicialização com o Windows ilegível: {ex.Message}"; }
            _launchAtLogin.Checked = registered;
        }
        finally { _loading = false; }
    }

    private void AddPreset()
    {
        if (DurationParser.Parse(_presetInput.Text) is not { } duration) return;
        var cfg = _config.Current;
        var seconds = (int)Math.Min(int.MaxValue, (long)duration.TotalSeconds);
        if (seconds > cfg.MaxDuration)
        {
            _presetError.Text = $"Presets não podem passar de {DurationFormat.Chip(TimeSpan.FromSeconds(cfg.MaxDuration))}.";
            return;
        }
        _presetError.Text = "";
        _presetInput.Clear();
        if (cfg.Presets.Contains(seconds)) return;
        Apply(c => c with { Presets = [.. c.Presets.Add(seconds).Sort()] });
    }

    private void RemovePreset()
    {
        var index = _presets.SelectedIndex;
        var cfg = _config.Current;
        if (index < 0 || index >= cfg.Presets.Length || cfg.Presets.Length <= 1) return;
        var removed = cfg.Presets[index];
        Apply(c =>
        {
            var rest = c.Presets.Remove(removed);
            return c with { Presets = rest, DefaultPreset = c.DefaultPreset == removed ? rest[0] : c.DefaultPreset };
        });
    }

    private void SaveSsid()
    {
        var typed = _ssid.Text;
        if (typed == _config.Current.HotspotSsid) return;
        Apply(c => c with { HotspotSsid = typed });
        _ = RefreshProfileStateAsync();
    }

    private void ToggleLaunchAtLogin()
    {
        if (_loading) return;
        var want = _launchAtLogin.Checked;
        try
        {
            if (want) _autoStart.Register(_exePath);
            else _autoStart.Unregister();
            _launchError.Text = "";
            Apply(c => c with { LaunchAtLogin = want });
        }
        catch (Exception ex)
        {
            _launchError.Text = $"Inicialização com o Windows: {ex.Message}";
            _log.Error(_launchError.Text);
            _loading = true;
            try { _launchAtLogin.Checked = !want; } finally { _loading = false; }
        }
    }

    private async Task RefreshWouldFreezeAsync()
    {
        var cfg = _config.Current;
        if (!cfg.FreezeAllApps) { _wouldFreeze.Text = ""; return; }
        _wouldFreeze.Text = "Congelaria agora: …";
        try
        {
            var names = await Task.Run(() => FreezePlanner.AutomaticCandidates(cfg, _inspector.Snapshot(), PlannerEnvironment.Current));
            if (IsDisposed) return;
            _wouldFreeze.Text = names.Count == 0 ? "Congelaria agora: nenhum outro app" : "Congelaria agora: " + string.Join(", ", names);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _wouldFreeze.Text = $"Não deu para listar os apps: {ex.Message}";
        }
    }

    private async Task RefreshProfileStateAsync()
    {
        var ssid = _config.Current.HotspotSsid.Trim();
        var ticket = ++_wifiCheck;
        if (ssid.Length == 0) { _profileState.Text = "Sem hotspot configurado: quando o Wi-Fi cair, o Insomnia só registra a queda."; return; }
        _profileState.Text = "Procurando o perfil…";
        string text;
        try
        {
            var found = await Task.Run(() => _wifi.HasProfile(ssid));
            text = found ? $"Perfil \"{ssid}\" salvo no Windows." : "Perfil do hotspot não encontrado: conecte-se a ele uma vez pelo Windows.";
        }
        catch (Exception ex) { text = $"Não deu para ler os perfis Wi-Fi: {ex.Message}"; }
        if (!IsDisposed && ticket == _wifiCheck) _profileState.Text = text;
    }

    private async Task<IReadOnlyList<string>> RunningAppsAsync()
    {
        var own = PlannerEnvironment.Current.OwnExeName;
        return await Task.Run(() => (IReadOnlyList<string>)[.. _inspector.Snapshot()
            .Where(p => p.HasVisibleWindow && !string.IsNullOrEmpty(p.Name) && !string.Equals(p.Name, own, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)]);
    }

    // ------------------------------------------------------------------ construção

    private static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { AutoScroll = true, Padding = new Padding(12) };
        content.Location = new Point(12, 12);
        page.Controls.Add(content);
        return page;
    }

    private static FlowLayoutPanel Column() =>
        new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Label Heading(string text) =>
        new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(0, 8, 0, 2) };

    private static Label Note(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(500, 0), Margin = new Padding(0, 2, 0, 8) };

    private static CheckBox Check(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(0, 6, 0, 2) };
}
