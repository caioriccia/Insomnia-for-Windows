// View-model das Configurações da UI nova (desenho-ui §3.7/§5.4). Porta a lógica inteira do
// Settings/SettingsForm.cs sem nenhuma dependência de UI: toda mudança grava na hora via
// IConfigSource.Update (que normaliza os pisos); "Iniciar com o Windows" reflete o HKCU Run, não
// só o config.json; leituras lentas (processos, perfis Wi-Fi) rodam fora do thread da UI e voltam
// por `post`. Mudanças de config vindas de fora (IConfigSource.Changed) reaplicam via `post`.
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Insomnia.App.Ui.Theme;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Lid;
using Insomnia.Core.Model;
using Insomnia.Core.Time;

namespace Insomnia.App.Ui.Model;

public enum SettingsPage { Sessao, Tampa, Agentes, Bateria, Rede, App }

public sealed record SettingsNavItem(SettingsPage Page, string Glyph, string Title);

public sealed class SettingsModel : INotifyPropertyChanged, IDisposable
{
    public const string NoProfileText = "Sem hotspot configurado: quando o Wi-Fi cair, o Insomnia só registra a queda.";
    public const string SearchingProfileText = "Procurando o perfil…";
    public const string WouldFreezePending = "Congelaria agora: …";

    private readonly IConfigSource _config;
    private readonly IAutoStart _autoStart;
    private readonly IWifi _wifi;
    private readonly IProcessInspector _inspector;
    private readonly string _exePath;
    private readonly ILog _log;
    private readonly Action<Action> _post;
    private bool _disposed;
    private int _wifiTicket;
    private int _freezeTicket;
    // O SSID só é gravado no CommitSsid: enquanto a pessoa digita, um Changed de fora não o atropela.
    private bool _ssidDirty;

    private SettingsPage _current = SettingsPage.Sessao;
    private IReadOnlyList<(int Seconds, string Chip, bool IsDefault)> _presets = [];
    private string _newPresetText = "";
    private string _presetError = "";
    private string _maxDurationText = "";
    private bool _darken, _freezeAll, _docker, _mute, _energy;
    private IReadOnlyList<string> _freezeList = [];
    private IReadOnlyList<string> _agentList = [];
    private string _wouldFreezeText = "";
    private int _lowFloor, _endFloor;
    private string _ssid = "";
    private string _profileStateText = "";
    private bool _launchAtLogin;
    private string _launchError = "";

    public SettingsModel(IConfigSource config, IAutoStart autoStart, IWifi wifi, IProcessInspector inspector,
        string configPath, string exePath, bool simulationBuild, ILog log, Action<Action> post)
    {
        _config = config;
        _autoStart = autoStart;
        _wifi = wifi;
        _inspector = inspector;
        _exePath = exePath;
        _log = log;
        _post = post;
        ConfigPath = configPath;
        SimulationBuild = simulationBuild;
        LoadFromConfig();
        _config.Changed += OnConfigChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// As 6 páginas, nesta ordem.
    public static IReadOnlyList<SettingsNavItem> Nav { get; } =
    [
        new(SettingsPage.Sessao, Glyphs.Clock, "Sessão"),
        new(SettingsPage.Tampa, Glyphs.Laptop, "Tampa"),
        new(SettingsPage.Agentes, Glyphs.Robot, "Agentes"),
        new(SettingsPage.Bateria, Glyphs.Battery, "Bateria"),
        new(SettingsPage.Rede, Glyphs.Wifi, "Rede"),
        new(SettingsPage.App, Glyphs.Settings, "App"),
    ];

    /// Página aberta. Abrir Tampa recalcula "Congelaria agora"; abrir Rede relê o perfil do hotspot.
    public SettingsPage Current
    {
        get => _current;
        set
        {
            if (!Set(ref _current, value)) return;
            if (value == SettingsPage.Tampa) _ = RefreshWouldFreezeAsync();
            if (value == SettingsPage.Rede) _ = RefreshProfileStateAsync();
        }
    }

    public string ConfigPath { get; }
    public bool SimulationBuild { get; }

    /// Relê tudo do config e do HKCU Run (a janela chama ao ser mostrada, como o ShowAndActivate velho).
    public void Reload() => LoadFromConfig();

    // ------------------------------------------------------------------ Sessão

    public IReadOnlyList<(int Seconds, string Chip, bool IsDefault)> Presets => _presets;

    public string NewPresetText
    {
        get => _newPresetText;
        set
        {
            if (Set(ref _newPresetText, value ?? "")) Raise(nameof(CanAddPreset));
        }
    }

    public bool CanAddPreset => DurationParser.Parse(_newPresetText) is not null;
    public string PresetError { get => _presetError; private set => Set(ref _presetError, value); }
    public string MaxDurationText { get => _maxDurationText; private set => Set(ref _maxDurationText, value); }

    public void AddPreset()
    {
        if (DurationParser.Parse(_newPresetText) is not { } duration) return;
        var cfg = _config.Current;
        var seconds = (int)Math.Min(int.MaxValue, (long)duration.TotalSeconds);
        if (seconds > cfg.MaxDuration)
        {
            PresetError = $"Presets não podem passar de {DurationFormat.Chip(TimeSpan.FromSeconds(cfg.MaxDuration))}.";
            return;
        }
        PresetError = "";
        NewPresetText = "";
        if (cfg.Presets.Contains(seconds)) return;
        Apply(c => c with { Presets = [.. c.Presets.Add(seconds).Sort()] });
    }

    /// Nunca deixa a lista vazia; tirar o padrão passa o padrão ao primeiro que sobrou.
    public void RemovePreset(int seconds)
    {
        var cfg = _config.Current;
        if (!cfg.Presets.Contains(seconds) || cfg.Presets.Length <= 1) return;
        Apply(c =>
        {
            var rest = c.Presets.Remove(seconds);
            return c with { Presets = rest, DefaultPreset = c.DefaultPreset == seconds ? rest[0] : c.DefaultPreset };
        });
    }

    public void SetDefault(int seconds)
    {
        var cfg = _config.Current;
        if (!cfg.Presets.Contains(seconds) || cfg.DefaultPreset == seconds) return;
        Apply(c => c with { DefaultPreset = seconds });
    }

    // ------------------------------------------------------------------ Tampa / Agentes

    public bool DarkenDisplay
    {
        get => _darken;
        set { if (Set(ref _darken, value)) Apply(c => c with { DarkenDisplayOnLidClose = value }); }
    }

    public bool FreezeAll
    {
        get => _freezeAll;
        set
        {
            if (!Set(ref _freezeAll, value)) return;
            Apply(c => c with { FreezeAllApps = value });
            _ = RefreshWouldFreezeAsync();
        }
    }

    public bool DockerRule
    {
        get => _docker;
        set { if (Set(ref _docker, value)) Apply(c => c with { DockerRule = value }); }
    }

    public bool Mute
    {
        get => _mute;
        set { if (Set(ref _mute, value)) Apply(c => c with { MuteOnLidClose = value }); }
    }

    public bool EnergySaver
    {
        get => _energy;
        set { if (Set(ref _energy, value)) Apply(c => c with { EnergySaverOnLidClose = value }); }
    }

    public IReadOnlyList<string> FreezeList => _freezeList;
    public IReadOnlyList<string> AgentList => _agentList;

    public void AddFrozen(string exe)
    {
        if (NormalizeExe(exe) is not { } name || _config.Current.FreezeList.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        Apply(c => c with { FreezeList = c.FreezeList.Add(name) });
        _ = RefreshWouldFreezeAsync();
    }

    public void RemoveFrozen(string exe)
    {
        if (!_config.Current.FreezeList.Contains(exe, StringComparer.OrdinalIgnoreCase)) return;
        Apply(c => c with { FreezeList = c.FreezeList.RemoveAll(i => string.Equals(i, exe, StringComparison.OrdinalIgnoreCase)) });
        _ = RefreshWouldFreezeAsync();
    }

    public void AddAgent(string exe)
    {
        if (NormalizeExe(exe) is not { } name || _config.Current.AgentList.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        Apply(c => c with { AgentList = c.AgentList.Add(name) });
        _ = RefreshWouldFreezeAsync();
    }

    public void RemoveAgent(string exe)
    {
        if (!_config.Current.AgentList.Contains(exe, StringComparer.OrdinalIgnoreCase)) return;
        Apply(c => c with { AgentList = c.AgentList.RemoveAll(i => string.Equals(i, exe, StringComparison.OrdinalIgnoreCase)) });
        _ = RefreshWouldFreezeAsync();
    }

    /// "slack" ⇒ "slack.exe"; espaços e aspas nas pontas somem; vazio ou com separador de pasta ⇒ null
    /// (mesma regra do NameListEditor velho; identificador = nome do executável, sem caixa).
    public static string? NormalizeExe(string? text)
    {
        var name = text?.Trim().Trim('"') ?? "";
        if (name.Length == 0 || name.IndexOfAny(['\\', '/', ':']) >= 0) return null;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    /// Apps com janela aberta agora (menos o próprio Insomnia), sem repetição, em ordem alfabética.
    public Task<IReadOnlyList<string>> RunningAppsAsync()
    {
        var own = PlannerEnvironment.Current.OwnExeName;
        return Task.Run(() => (IReadOnlyList<string>)[.. _inspector.Snapshot()
            .Where(p => p.HasVisibleWindow && !string.IsNullOrEmpty(p.Name) && !string.Equals(p.Name, own, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)]);
    }

    public string WouldFreezeText { get => _wouldFreezeText; private set => Set(ref _wouldFreezeText, value); }

    /// "" com "congelar todos" desligado; senão "Congelaria agora: …" até a leitura voltar. Só a
    /// leitura mais recente (ticket) escreve o resultado.
    public async Task RefreshWouldFreezeAsync()
    {
        var cfg = _config.Current;
        var ticket = ++_freezeTicket;
        if (!cfg.FreezeAllApps) { WouldFreezeText = ""; return; }
        WouldFreezeText = WouldFreezePending;
        string text;
        try
        {
            var names = await Task.Run(() => FreezePlanner.AutomaticCandidates(cfg, _inspector.Snapshot(), PlannerEnvironment.Current)).ConfigureAwait(false);
            text = names.Count == 0 ? "Congelaria agora: nenhum outro app" : "Congelaria agora: " + string.Join(", ", names);
        }
        catch (Exception ex) { text = $"Não deu para listar os apps: {ex.Message}"; }
        _post(() => { if (!_disposed && ticket == _freezeTicket) WouldFreezeText = text; });
    }

    // ------------------------------------------------------------------ Bateria

    /// Passo de 5 %. Subir a Economia acima do fim desce o fim (WithLowPowerFloor).
    public int LowPowerFloor
    {
        get => _lowFloor;
        set
        {
            var snapped = Snap(Math.Clamp(value, 0, 100));
            if (snapped == _lowFloor) return;
            Apply(c => c.WithLowPowerFloor(snapped));
        }
    }

    /// Passo de 5 %; 0 desliga o fim por bateria. Subir o fim até a Economia sobe a Economia (WithEndFloor).
    public int EndFloor
    {
        get => _endFloor;
        set
        {
            var snapped = Snap(Math.Clamp(value, 0, Config.MaxEndFloor));
            if (snapped == _endFloor) return;
            Apply(c => c.WithEndFloor(snapped));
        }
    }

    private static int Snap(int value) => (int)Math.Round(value / (double)Config.FloorStep, MidpointRounding.ToEven) * Config.FloorStep;

    // ------------------------------------------------------------------ Rede

    /// Rascunho do campo; só vai ao config no CommitSsid (Enter ou sair do campo).
    public string HotspotSsid
    {
        get => _ssid;
        set
        {
            value ??= "";
            if (Set(ref _ssid, value)) _ssidDirty = value != _config.Current.HotspotSsid;
        }
    }

    public void CommitSsid()
    {
        var typed = _ssid;
        _ssidDirty = false;
        if (typed == _config.Current.HotspotSsid) return;
        Apply(c => c with { HotspotSsid = typed });
        _ = RefreshProfileStateAsync();
    }

    public string ProfileStateText { get => _profileStateText; private set => Set(ref _profileStateText, value); }

    /// Lê se o Windows tem o perfil do hotspot gravado; só a leitura mais recente (ticket) escreve.
    public async Task RefreshProfileStateAsync()
    {
        var ssid = _config.Current.HotspotSsid.Trim();
        var ticket = ++_wifiTicket;
        if (ssid.Length == 0) { ProfileStateText = NoProfileText; return; }
        ProfileStateText = SearchingProfileText;
        string text;
        try
        {
            var found = await Task.Run(() => _wifi.HasProfile(ssid)).ConfigureAwait(false);
            text = found ? $"Perfil \"{ssid}\" salvo no Windows." : "Perfil do hotspot não encontrado: conecte-se a ele uma vez pelo Windows.";
        }
        catch (Exception ex) { text = $"Não deu para ler os perfis Wi-Fi: {ex.Message}"; }
        _post(() => { if (!_disposed && ticket == _wifiTicket) ProfileStateText = text; });
    }

    // ------------------------------------------------------------------ App

    /// Registra/desfaz no HKCU Run e só então grava no config. Falha ⇒ volta ao valor anterior e LaunchError explica.
    public bool LaunchAtLogin
    {
        get => _launchAtLogin;
        set
        {
            if (value == _launchAtLogin) return;
            try
            {
                if (value) _autoStart.Register(_exePath);
                else _autoStart.Unregister();
                LaunchError = "";
                Set(ref _launchAtLogin, value);
                Apply(c => c with { LaunchAtLogin = value });
            }
            catch (Exception ex)
            {
                LaunchError = $"Inicialização com o Windows: {ex.Message}";
                _log.Error(LaunchError);
                // O controle ligado ao valor já virou: avisa para ele reler o valor que ficou.
                Raise(nameof(LaunchAtLogin));
            }
        }
    }

    public string LaunchError { get => _launchError; private set => Set(ref _launchError, value); }

    // ------------------------------------------------------------------ config

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _config.Changed -= OnConfigChanged;
    }

    /// Grava e relê na hora (sem esperar o Changed passar pelo post): o controle que mudou vê já o
    /// valor normalizado (pisos), e se a gravação falhar as propriedades voltam ao que o config tem.
    private void Apply(Func<Config, Config> change)
    {
        try { _config.Update(change); }
        catch (Exception ex) { _log.Error($"configurações: não deu para aplicar: {ex.Message}"); }
        if (!_disposed) LoadFromConfig();
    }

    private void OnConfigChanged() => _post(() => { if (!_disposed) LoadFromConfig(); });

    private void LoadFromConfig()
    {
        var cfg = _config.Current;

        var presets = cfg.Presets.Select(s => (s, DurationFormat.Chip(TimeSpan.FromSeconds(s)), s == cfg.DefaultPreset)).ToList();
        if (!presets.SequenceEqual(_presets)) { _presets = presets; Raise(nameof(Presets)); }
        MaxDurationText = $"Sessão máxima: {DurationFormat.Chip(TimeSpan.FromSeconds(cfg.MaxDuration))} (somente leitura)";

        Set(ref _darken, cfg.DarkenDisplayOnLidClose, nameof(DarkenDisplay));
        Set(ref _freezeAll, cfg.FreezeAllApps, nameof(FreezeAll));
        Set(ref _docker, cfg.DockerRule, nameof(DockerRule));
        Set(ref _mute, cfg.MuteOnLidClose, nameof(Mute));
        Set(ref _energy, cfg.EnergySaverOnLidClose, nameof(EnergySaver));
        SetList(ref _freezeList, cfg.FreezeList, nameof(FreezeList));
        SetList(ref _agentList, cfg.AgentList, nameof(AgentList));

        Set(ref _lowFloor, Math.Clamp(cfg.LowPowerFloor, 0, 100), nameof(LowPowerFloor));
        Set(ref _endFloor, Math.Clamp(cfg.EndFloor, 0, Config.MaxEndFloor), nameof(EndFloor));

        if (!_ssidDirty) Set(ref _ssid, cfg.HotspotSsid, nameof(HotspotSsid));

        bool registered;
        try { registered = _autoStart.IsRegistered(); LaunchError = ""; }
        catch (Exception ex) { registered = cfg.LaunchAtLogin; LaunchError = $"Inicialização com o Windows ilegível: {ex.Message}"; }
        Set(ref _launchAtLogin, registered, nameof(LaunchAtLogin));
    }

    private void SetList(ref IReadOnlyList<string> field, ImmutableArray<string> value, string name)
    {
        if (field.SequenceEqual(value, StringComparer.Ordinal)) return;
        field = [.. value];
        Raise(name);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
