using System.IO;   // UseWPF tira System.IO dos usings implícitos
using Insomnia.App.Ui.Model;
using Insomnia.App.Ui.Theme;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Tests.Fakes;
using CoreConfig = Insomnia.Core.Model.Config;

// Namespace "Configuracoes" (e não "Config"): um namespace Insomnia.App.Tests.Ui.Config esconderia o
// tipo Config de todo teste em Insomnia.App.Tests.Ui.*. O filtro "~Insomnia.App.Tests.Ui.Config" pega igual.
namespace Insomnia.App.Tests.Ui.Configuracoes;

public class SettingsModelTests
{
    private const string Exe = @"C:\Apps\Insomnia\Insomnia.exe";

    private sealed class World
    {
        public FakeConfigSource Config { get; }
        public IAutoStart AutoStart { get; init; } = new FakeAutoStart();
        public IWifi Wifi { get; init; } = new FakeWifi();
        public FakeProcessWorld Processes { get; } = new();
        public FakeLog Log { get; } = new();
        /// null = post síncrono; senão as ações postadas esperam aqui até RunPosted.
        public Queue<Action>? Posted { get; init; }

        public World(CoreConfig? initial = null) => Config = new FakeConfigSource(initial);

        public SettingsModel Model() => new(Config, AutoStart, Wifi, Processes, "fake/config.json", Exe, simulationBuild: false, Log,
            a => { if (Posted is { } q) q.Enqueue(a); else a(); });

        public void RunPosted() { while (Posted is { Count: > 0 } q) q.Dequeue()(); }
    }

    private sealed class BrokenAutoStart(bool failRead = false) : IAutoStart
    {
        public bool IsRegistered() => failRead ? throw new UnauthorizedAccessException("acesso negado ao Run") : false;
        public void Register(string exePath) => throw new UnauthorizedAccessException("acesso negado ao Run");
        public void Unregister() => throw new UnauthorizedAccessException("acesso negado ao Run");
    }

    /// HasProfile trava no SSID "lento" até Release: prova que a leitura atrasada não sobrescreve a nova.
    private sealed class SlowWifi : IWifi
    {
        public ManualResetEventSlim Gate { get; } = new(false);
        public event Action<bool>? ConnectivityChanged { add { } remove { } }
        public bool IsConnected => true;
        public bool HasProfile(string ssid) { if (ssid == "lento") Gate.Wait(TimeSpan.FromSeconds(10)); return true; }
        public Task<bool> ConnectAsync(string profileName, CancellationToken ct) => Task.FromResult(true);
        public void Start() { }
        public void Stop() { }
    }

    private static List<string> Changes(SettingsModel model)
    {
        var names = new List<string>();
        model.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? "");
        return names;
    }

    // ------------------------------------------------------------------ carregar

    [Fact]
    public void Carrega_tudo_do_config()
    {
        using var model = new World().Model();
        var d = CoreConfig.Default;

        Assert.Equal(d.Presets, model.Presets.Select(p => p.Seconds));
        Assert.Equal(["30m", "1h", "2h", "4h", "8h", "12h", "1d", "3d"], model.Presets.Select(p => p.Chip));
        Assert.Equal(14400, Assert.Single(model.Presets, p => p.IsDefault).Seconds);
        Assert.Equal("Sessão máxima: 30d (somente leitura)", model.MaxDurationText);
        Assert.Equal(d.DarkenDisplayOnLidClose, model.DarkenDisplay);
        Assert.Equal(d.FreezeAllApps, model.FreezeAll);
        Assert.Equal(d.DockerRule, model.DockerRule);
        Assert.Equal(d.MuteOnLidClose, model.Mute);
        Assert.Equal(d.EnergySaverOnLidClose, model.EnergySaver);
        Assert.Equal(d.FreezeList, model.FreezeList);
        Assert.Equal(d.AgentList, model.AgentList);
        Assert.Equal(40, model.LowPowerFloor);
        Assert.Equal(10, model.EndFloor);
        Assert.Equal("", model.HotspotSsid);
        Assert.False(model.LaunchAtLogin);
        Assert.Equal("", model.LaunchError);
        Assert.Equal("fake/config.json", model.ConfigPath);
        Assert.False(model.SimulationBuild);
        Assert.Equal(SettingsPage.Sessao, model.Current);
    }

    [Fact]
    public void Navegacao_tem_as_seis_paginas_na_ordem_com_glifos()
    {
        Assert.Equal(
            [SettingsPage.Sessao, SettingsPage.Tampa, SettingsPage.Agentes, SettingsPage.Bateria, SettingsPage.Rede, SettingsPage.App],
            SettingsModel.Nav.Select(n => n.Page));
        Assert.Equal(["Sessão", "Tampa", "Agentes", "Bateria", "Rede", "App"], SettingsModel.Nav.Select(n => n.Title));
        Assert.Equal(
            [Glyphs.Clock, Glyphs.Laptop, Glyphs.Robot, Glyphs.Battery, Glyphs.Wifi, Glyphs.Settings],
            SettingsModel.Nav.Select(n => n.Glyph));
    }

    [Fact]
    public void Iniciar_com_o_Windows_vem_do_registro_e_nao_do_config()
    {
        var world = new World(CoreConfig.Default with { LaunchAtLogin = false })
        {
            AutoStart = new FakeAutoStart { RegisteredPath = Exe },
        };
        using var model = world.Model();

        Assert.True(model.LaunchAtLogin);
    }

    [Fact]
    public void Registro_ilegivel_usa_o_config_e_explica()
    {
        var world = new World(CoreConfig.Default with { LaunchAtLogin = true }) { AutoStart = new BrokenAutoStart(failRead: true) };
        using var model = world.Model();

        Assert.True(model.LaunchAtLogin);
        Assert.Equal("Inicialização com o Windows ilegível: acesso negado ao Run", model.LaunchError);
    }

    // ------------------------------------------------------------------ presets

    [Fact]
    public void AddPreset_acima_do_maximo_da_erro_e_nao_grava()
    {
        var world = new World();
        using var model = world.Model();

        model.NewPresetText = "31d";
        Assert.True(model.CanAddPreset);
        model.AddPreset();

        Assert.Equal("Presets não podem passar de 30d.", model.PresetError);
        Assert.Equal(0, world.Config.Updates);
        Assert.Equal(CoreConfig.Default.Presets.AsEnumerable(), world.Config.Current.Presets.AsEnumerable());
        Assert.Equal("31d", model.NewPresetText);   // o texto fica para a pessoa corrigir
    }

    [Fact]
    public void AddPreset_valido_grava_em_ordem_limpa_o_campo_e_o_erro()
    {
        var world = new World();
        using var model = world.Model();
        model.NewPresetText = "31d";
        model.AddPreset();

        model.NewPresetText = "45";
        model.AddPreset();

        Assert.Equal("", model.PresetError);
        Assert.Equal("", model.NewPresetText);
        Assert.Equal([1800, 2700, 3600, 7200, 14400, 28800, 43200, 86400, 259200], world.Config.Current.Presets.AsEnumerable());
        Assert.Contains(model.Presets, p => p.Seconds == 2700 && p.Chip == "45m" && !p.IsDefault);
    }

    [Fact]
    public void AddPreset_repetido_ou_invalido_nao_grava()
    {
        var world = new World();
        using var model = world.Model();

        model.NewPresetText = "abc";
        Assert.False(model.CanAddPreset);
        model.AddPreset();
        model.NewPresetText = "2h";
        model.AddPreset();

        Assert.Equal(0, world.Config.Updates);
        Assert.Equal("", model.NewPresetText);
    }

    [Fact]
    public void RemovePreset_do_padrao_troca_o_padrao_pelo_primeiro_que_sobrou()
    {
        var world = new World();
        using var model = world.Model();

        model.RemovePreset(14400);

        Assert.DoesNotContain(14400, world.Config.Current.Presets);
        Assert.Equal(1800, world.Config.Current.DefaultPreset);
        Assert.Equal(1800, Assert.Single(model.Presets, p => p.IsDefault).Seconds);
    }

    [Fact]
    public void RemovePreset_nunca_esvazia_a_lista()
    {
        var world = new World(CoreConfig.Default with { Presets = [3600], DefaultPreset = 3600 });
        using var model = world.Model();

        model.RemovePreset(3600);
        model.RemovePreset(999);

        Assert.Equal(0, world.Config.Updates);
        Assert.Equal([3600], world.Config.Current.Presets.AsEnumerable());
    }

    [Fact]
    public void SetDefault_grava_so_preset_existente()
    {
        var world = new World();
        using var model = world.Model();

        model.SetDefault(999);
        Assert.Equal(0, world.Config.Updates);
        model.SetDefault(7200);

        Assert.Equal(7200, world.Config.Current.DefaultPreset);
        Assert.Equal(7200, Assert.Single(model.Presets, p => p.IsDefault).Seconds);
    }

    // ------------------------------------------------------------------ tampa e listas

    [Fact]
    public void Chaves_da_tampa_gravam_na_hora()
    {
        var world = new World();
        using var model = world.Model();

        model.DarkenDisplay = false;
        model.DockerRule = true;
        model.Mute = false;
        model.EnergySaver = false;
        model.FreezeAll = true;

        var c = world.Config.Current;
        Assert.False(c.DarkenDisplayOnLidClose);
        Assert.True(c.DockerRule);
        Assert.False(c.MuteOnLidClose);
        Assert.False(c.EnergySaverOnLidClose);
        Assert.True(c.FreezeAllApps);
        Assert.Equal(5, world.Config.Updates);
    }

    [Fact]
    public void Mesmo_valor_nao_grava()
    {
        var world = new World();
        using var model = world.Model();

        model.DarkenDisplay = CoreConfig.Default.DarkenDisplayOnLidClose;
        model.LowPowerFloor = 40;
        model.EndFloor = 10;
        model.LaunchAtLogin = false;

        Assert.Equal(0, world.Config.Updates);
    }

    [Fact]
    public void Listas_normalizam_nome_e_nao_repetem()
    {
        var world = new World();
        using var model = world.Model();

        model.AddFrozen("Spotify");
        model.AddFrozen("SLACK.EXE");       // já está (sem diferenciar caixa)
        model.AddFrozen(@"C:\x\y.exe");     // caminho não vale
        model.RemoveFrozen("WhatsApp.exe");
        model.AddAgent("\"Figma.exe\" ");
        model.RemoveAgent("node.exe");

        Assert.Equal(["slack.exe", "Discord.exe", "Spotify.exe"], world.Config.Current.FreezeList.AsEnumerable());
        Assert.Equal(world.Config.Current.FreezeList, model.FreezeList);
        Assert.Contains("Figma.exe", world.Config.Current.AgentList);
        Assert.DoesNotContain("node.exe", world.Config.Current.AgentList);
        Assert.Equal(world.Config.Current.AgentList, model.AgentList);
        Assert.Equal(4, world.Config.Updates);
    }

    [Fact]
    public async Task Congelaria_agora_lista_os_candidatos_automaticos()
    {
        var world = new World();
        var t0 = DateTimeOffset.UnixEpoch;
        world.Processes.Add(90001, "Spotify.exe", 1, t0, hasVisibleWindow: true);
        world.Processes.Add(90002, "slack.exe", 1, t0, hasVisibleWindow: true);    // na lista explícita
        world.Processes.Add(90003, "chrome.exe", 1, t0, hasVisibleWindow: true);   // protegido
        world.Processes.Add(90004, "worker.exe", 1, t0, hasVisibleWindow: false);  // sem janela
        using var model = world.Model();

        await model.RefreshWouldFreezeAsync();
        Assert.Equal("", model.WouldFreezeText);   // "congelar todos" desligado

        model.FreezeAll = true;
        await model.RefreshWouldFreezeAsync();
        Assert.Equal("Congelaria agora: Spotify.exe", model.WouldFreezeText);

        world.Processes.Kill(90001);
        await model.RefreshWouldFreezeAsync();
        Assert.Equal("Congelaria agora: nenhum outro app", model.WouldFreezeText);
    }

    [Fact]
    public async Task Apps_em_execucao_vem_sem_repeticao_em_ordem_e_so_com_janela()
    {
        var world = new World();
        var t0 = DateTimeOffset.UnixEpoch;
        world.Processes.Add(90001, "zoom.exe", 1, t0, hasVisibleWindow: true);
        world.Processes.Add(90002, "Arc.exe", 1, t0, hasVisibleWindow: true);
        world.Processes.Add(90003, "ARC.exe", 1, t0, hasVisibleWindow: true);
        world.Processes.Add(90004, "svc.exe", 1, t0, hasVisibleWindow: false);
        world.Processes.Add(90005, PlannerEnvironmentOwnExe(), 1, t0, hasVisibleWindow: true);
        using var model = world.Model();

        var apps = await model.RunningAppsAsync();

        Assert.Equal(["Arc.exe", "zoom.exe"], apps);
    }

    private static string PlannerEnvironmentOwnExe() => Insomnia.Core.Lid.PlannerEnvironment.Current.OwnExeName;

    // ------------------------------------------------------------------ bateria

    [Fact]
    public void EndFloor_acima_da_economia_sobe_o_LowPowerFloor()
    {
        var world = new World();
        using var model = world.Model();
        var changes = Changes(model);

        model.EndFloor = 40;

        Assert.Equal(40, world.Config.Current.EndFloor);
        Assert.Equal(45, world.Config.Current.LowPowerFloor);
        Assert.Equal(40, model.EndFloor);
        Assert.Equal(45, model.LowPowerFloor);
        Assert.Contains(nameof(SettingsModel.LowPowerFloor), changes);
    }

    [Fact]
    public void LowPowerFloor_abaixo_do_fim_desce_o_EndFloor_e_valores_andam_de_5_em_5()
    {
        var world = new World();
        using var model = world.Model();

        model.LowPowerFloor = 9;   // vira 10 (passo de 5) e empurra o fim para 5

        Assert.Equal(10, model.LowPowerFloor);
        Assert.Equal(5, model.EndFloor);
        Assert.Equal(10, world.Config.Current.LowPowerFloor);
        Assert.Equal(5, world.Config.Current.EndFloor);
    }

    // ------------------------------------------------------------------ rede

    [Fact]
    public void CommitSsid_so_grava_se_mudou()
    {
        var world = new World();
        using var model = world.Model();

        model.CommitSsid();
        Assert.Equal(0, world.Config.Updates);

        model.HotspotSsid = "iPhone";
        Assert.Equal(0, world.Config.Updates);   // digitar não grava
        model.CommitSsid();
        model.CommitSsid();

        Assert.Equal(1, world.Config.Updates);
        Assert.Equal("iPhone", world.Config.Current.HotspotSsid);
    }

    [Fact]
    public void Rascunho_do_ssid_nao_e_atropelado_por_mudanca_de_fora()
    {
        var world = new World();
        using var model = world.Model();

        model.HotspotSsid = "digitando";
        world.Config.Set(world.Config.Current with { MuteOnLidClose = false });

        Assert.Equal("digitando", model.HotspotSsid);
        Assert.False(model.Mute);
    }

    [Fact]
    public async Task Perfil_do_hotspot_le_o_Windows()
    {
        var wifi = new FakeWifi();
        wifi.Profiles.Add("iPhone");
        var world = new World { Wifi = wifi };
        using var model = world.Model();

        await model.RefreshProfileStateAsync();
        Assert.Equal(SettingsModel.NoProfileText, model.ProfileStateText);

        world.Config.Set(world.Config.Current with { HotspotSsid = "iPhone" });
        await model.RefreshProfileStateAsync();
        Assert.Equal("Perfil \"iPhone\" salvo no Windows.", model.ProfileStateText);

        world.Config.Set(world.Config.Current with { HotspotSsid = "Outro" });
        await model.RefreshProfileStateAsync();
        Assert.Equal("Perfil do hotspot não encontrado: conecte-se a ele uma vez pelo Windows.", model.ProfileStateText);
    }

    [Fact]
    public async Task Leitura_de_perfil_atrasada_nao_sobrescreve_a_mais_nova()
    {
        var wifi = new SlowWifi();
        var world = new World(CoreConfig.Default with { HotspotSsid = "lento" }) { Wifi = wifi };
        using var model = world.Model();

        var slow = model.RefreshProfileStateAsync();
        Assert.Equal(SettingsModel.SearchingProfileText, model.ProfileStateText);
        world.Config.Set(world.Config.Current with { HotspotSsid = "rapido" });
        await model.RefreshProfileStateAsync();
        Assert.Equal("Perfil \"rapido\" salvo no Windows.", model.ProfileStateText);

        wifi.Gate.Set();
        await slow;

        Assert.Equal("Perfil \"rapido\" salvo no Windows.", model.ProfileStateText);
    }

    [Fact]
    public void Abrir_a_pagina_Rede_le_o_perfil()
    {
        var world = new World();
        using var model = world.Model();

        model.Current = SettingsPage.Rede;

        Assert.Equal(SettingsModel.NoProfileText, model.ProfileStateText);
    }

    // ------------------------------------------------------------------ iniciar com o Windows

    [Fact]
    public void LaunchAtLogin_registra_e_grava()
    {
        var auto = new FakeAutoStart();
        var world = new World { AutoStart = auto };
        using var model = world.Model();

        model.LaunchAtLogin = true;

        Assert.Equal(Exe, auto.RegisteredPath);
        Assert.True(world.Config.Current.LaunchAtLogin);
        Assert.True(model.LaunchAtLogin);

        model.LaunchAtLogin = false;
        Assert.Null(auto.RegisteredPath);
        Assert.False(world.Config.Current.LaunchAtLogin);
        Assert.Equal(["register", "unregister"], auto.Calls);
    }

    [Fact]
    public void LaunchAtLogin_com_falha_reverte_e_expoe_LaunchError()
    {
        var world = new World { AutoStart = new BrokenAutoStart() };
        using var model = world.Model();
        var changes = Changes(model);

        model.LaunchAtLogin = true;

        Assert.False(model.LaunchAtLogin);
        Assert.Equal("Inicialização com o Windows: acesso negado ao Run", model.LaunchError);
        Assert.Equal(0, world.Config.Updates);
        Assert.False(world.Config.Current.LaunchAtLogin);
        Assert.True(world.Log.Contains("acesso negado ao Run"));
        Assert.Contains(nameof(SettingsModel.LaunchAtLogin), changes);   // o controle relê o valor que ficou
        Assert.Contains(nameof(SettingsModel.LaunchError), changes);
    }

    // ------------------------------------------------------------------ mudanças de fora e eventos

    [Fact]
    public void Changed_externo_reflete_nas_propriedades_pelo_post()
    {
        var world = new World { Posted = new Queue<Action>() };
        using var model = world.Model();
        var changes = Changes(model);

        world.Config.Set(CoreConfig.Default with
        {
            MuteOnLidClose = false,
            FreezeList = ["Spotify.exe"],
            LowPowerFloor = 60,
            EndFloor = 20,
            HotspotSsid = "casa",
            DefaultPreset = 3600,
        });
        Assert.True(model.Mute);   // ainda não: o Changed foi postado ao thread da UI
        Assert.Empty(changes);

        world.RunPosted();

        Assert.False(model.Mute);
        Assert.Equal(["Spotify.exe"], model.FreezeList);
        Assert.Equal(60, model.LowPowerFloor);
        Assert.Equal(20, model.EndFloor);
        Assert.Equal("casa", model.HotspotSsid);
        Assert.Equal(3600, Assert.Single(model.Presets, p => p.IsDefault).Seconds);
        Assert.Contains(nameof(SettingsModel.Mute), changes);
        Assert.Contains(nameof(SettingsModel.FreezeList), changes);
        Assert.Contains(nameof(SettingsModel.Presets), changes);
        Assert.Contains(nameof(SettingsModel.HotspotSsid), changes);
    }

    [Fact]
    public void PropertyChanged_dispara_para_o_que_mudou()
    {
        var world = new World();
        using var model = world.Model();
        var changes = Changes(model);

        model.DarkenDisplay = false;
        model.NewPresetText = "2h";
        model.Current = SettingsPage.Bateria;

        Assert.Contains(nameof(SettingsModel.DarkenDisplay), changes);
        Assert.Contains(nameof(SettingsModel.NewPresetText), changes);
        Assert.Contains(nameof(SettingsModel.CanAddPreset), changes);
        Assert.Contains(nameof(SettingsModel.Current), changes);
        Assert.Single(changes, nameof(SettingsModel.DarkenDisplay));   // o Changed do config não repete o aviso
    }

    [Fact]
    public void Dispose_para_de_ouvir_o_config()
    {
        var world = new World();
        var model = world.Model();
        var changes = Changes(model);

        model.Dispose();
        world.Config.Set(world.Config.Current with { MuteOnLidClose = false });

        Assert.Empty(changes);
        Assert.True(model.Mute);
    }

    [Fact]
    public void Falha_ao_gravar_volta_ao_que_o_config_tem()
    {
        var world = new World();
        var failing = new FailingConfigSource(world.Config);
        using var model = new SettingsModel(failing, new FakeAutoStart(), new FakeWifi(), world.Processes, "c", Exe, false, world.Log, a => a());

        model.Mute = false;

        Assert.True(model.Mute);
        Assert.True(world.Log.Contains("não deu para aplicar"));
    }

    private sealed class FailingConfigSource(IConfigSource inner) : IConfigSource
    {
        public CoreConfig Current => inner.Current;
        public event Action? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
        public void Update(Func<CoreConfig, CoreConfig> change) => throw new IOException("disco cheio");
    }
}
