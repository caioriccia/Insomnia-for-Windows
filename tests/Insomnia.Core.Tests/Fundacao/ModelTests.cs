using Insomnia.Core.Store;
using Insomnia.Core.Tests.Fakes;

namespace Insomnia.Core.Tests.Fundacao;

using Insomnia.Core.Model;

/// Pisos do Config, IsDirty do Journal, Paths e ConfigService.
public class ModelTests
{
    // ---------- pisos ----------

    [Fact]
    public void Padrao_tem_os_valores_do_desenho()
    {
        var c = Config.Default;
        Assert.Equal([1800, 3600, 7200, 14400, 28800, 43200, 86400, 259200], c.Presets.ToArray());
        Assert.Equal(14400, c.DefaultPreset);
        Assert.Equal(2592000, c.MaxDuration);
        Assert.Equal(40, c.LowPowerFloor);
        Assert.Equal(10, c.EndFloor);
        Assert.True(c.FloorsAreOrdered);
        Assert.Contains("Docker Desktop.exe", c.AgentList);
        Assert.Equal(20, c.AgentList.Length);
        Assert.Same(Config.Default, Config.Default);
    }

    [Fact]
    public void WithEndFloor_sobe_a_Economia_para_um_passo_acima()
    {
        var c = Config.Default.WithEndFloor(40);
        Assert.Equal(40, c.EndFloor);
        Assert.Equal(45, c.LowPowerFloor);
        Assert.True(c.FloorsAreOrdered);
    }

    [Fact]
    public void WithEndFloor_respeita_teto_e_minimo()
    {
        var high = Config.Default.WithEndFloor(100);
        Assert.Equal(Config.MaxEndFloor, high.EndFloor);
        Assert.Equal(100, high.LowPowerFloor);
        var low = Config.Default.WithEndFloor(-5);
        Assert.Equal(0, low.EndFloor);
        Assert.Equal(40, low.LowPowerFloor);
    }

    [Fact]
    public void WithEndFloor_abaixo_da_Economia_nao_mexe_nela()
    {
        var c = Config.Default.WithEndFloor(25);
        Assert.Equal(25, c.EndFloor);
        Assert.Equal(40, c.LowPowerFloor);
    }

    [Fact]
    public void WithLowPowerFloor_desce_o_fim_para_um_passo_abaixo()
    {
        var c = Config.Default.WithLowPowerFloor(10);
        Assert.Equal(10, c.LowPowerFloor);
        Assert.Equal(5, c.EndFloor);
        var zero = Config.Default.WithLowPowerFloor(0);
        Assert.Equal(0, zero.LowPowerFloor);
        Assert.Equal(0, zero.EndFloor); // mínimo 0, e 0 desliga o fim por bateria
        Assert.True(zero.FloorsAreOrdered);
        Assert.Equal(100, Config.Default.WithLowPowerFloor(150).LowPowerFloor);
    }

    [Fact]
    public void Fim_zero_nao_e_afetado_pela_Economia()
    {
        var c = (Config.Default with { EndFloor = 0 }).WithLowPowerFloor(0);
        Assert.Equal(0, c.EndFloor);
        Assert.True(c.FloorsAreOrdered);
    }

    [Fact]
    public void Normalized_so_sobe_a_Economia()
    {
        var bad = Config.Default with { EndFloor = 50, LowPowerFloor = 30 };
        Assert.False(bad.FloorsAreOrdered);
        var n = bad.Normalized();
        Assert.Equal(50, n.EndFloor);
        Assert.Equal(55, n.LowPowerFloor);
        Assert.True(n.FloorsAreOrdered);
    }

    [Fact]
    public void Normalized_prende_valores_fora_da_faixa()
    {
        var n = (Config.Default with { EndFloor = 100, LowPowerFloor = 120 }).Normalized();
        Assert.Equal(Config.MaxEndFloor, n.EndFloor);
        Assert.Equal(100, n.LowPowerFloor);
        Assert.True(n.FloorsAreOrdered);
        var negative = (Config.Default with { EndFloor = -3 }).Normalized();
        Assert.Equal(0, negative.EndFloor);
    }

    [Fact]
    public void Normalized_de_config_em_ordem_devolve_a_mesma_instancia()
    {
        var ok = Config.Default with { EndFloor = 15 };
        Assert.Same(ok, ok.Normalized());
    }

    // ---------- diário ----------

    private static readonly SavedAudioOutput Speakers = new("spk", "Alto-falantes", 0.5f, false);

    [Fact]
    public void Diario_limpo_nao_e_sujo()
    {
        Assert.False(Journal.Clean.IsDirty());
        Assert.False(Journal.Clean.HasLidActions);
        Assert.Equal(1, Journal.Clean.Version);
    }

    [Theory]
    [InlineData("lid")]
    [InlineData("energy")]
    [InlineData("frozen")]
    [InlineData("docker")]
    [InlineData("audio")]
    public void Cada_entrada_suja_o_diario(string which)
    {
        var now = FakeClock.DefaultStart;
        var j = which switch
        {
            "lid" => Journal.Clean with { LidAction = new LidActionEntry(FakeSchemes.Balanced, 1, 1, now) },
            "energy" => Journal.Clean with { EnergySaver = new EnergySaverEntry(FakeSchemes.Balanced, 20, now, "battery") },
            "frozen" => Journal.Clean with { FrozenProcesses = [new FrozenProcess(1, "a.exe", null, null, "a.exe")] },
            "docker" => Journal.Clean with { DockerFrozen = true },
            _ => Journal.Clean with { SavedAudioOutputs = [Speakers] },
        };
        Assert.True(j.IsDirty());
    }

    [Fact]
    public void Saida_de_audio_desconectada_nao_suja()
    {
        var j = Journal.Clean with { SavedAudioOutputs = [Speakers] };
        Assert.True(j.HasLidActions);
        Assert.False(j.IsDirty(new HashSet<string> { "spk" }));
        Assert.True(j.IsDirty(new HashSet<string> { "outra" }));
    }

    [Fact]
    public void Identidade_exige_hora_de_criacao_e_boot()
    {
        Assert.True(new FrozenProcess(1, "a.exe", FakeClock.DefaultStart, "17", "a.exe").HasIdentity);
        Assert.False(new FrozenProcess(1, "a.exe", FakeClock.DefaultStart, "", "a.exe").HasIdentity);
        Assert.False(new FrozenProcess(1, "a.exe", null, "17", "a.exe").HasIdentity);
    }

    // ---------- sessão ----------

    [Fact]
    public void Sessao_expira_no_instante_do_fim()
    {
        var s = new Session(FakeClock.DefaultStart, FakeClock.DefaultStart.AddHours(1), []);
        Assert.False(s.IsExpired(FakeClock.DefaultStart.AddMinutes(59)));
        Assert.True(s.IsExpired(s.EndsAt));
        Assert.Equal(TimeSpan.FromMinutes(1), s.Remaining(FakeClock.DefaultStart.AddMinutes(59)));
        Assert.Equal(TimeSpan.Zero, s.Remaining(s.EndsAt.AddHours(1)));
    }

    // ---------- caminhos ----------

    [Fact]
    public void INSOMNIA_HOME_relocaliza_tudo()
    {
        var home = Path.Combine(Path.GetTempPath(), "insomnia-home-teste");
        var p = Paths.FromEnvironment(name => name == Paths.HomeVariable ? home : null);
        Assert.Equal(Path.Combine(home, "config.json"), p.ConfigFile);
        Assert.Equal(Path.Combine(home, "state.json"), p.JournalFile);
        Assert.Equal(Path.Combine(home, "session.json"), p.SessionFile);
        Assert.Equal(Path.Combine(home, ".recovery.lock"), p.LockFile);
        Assert.Equal(Path.Combine(home, "Logs", "insomnia.log"), p.LogFile);
        Assert.Equal(Path.Combine(home, "Logs", "handoffs.log"), p.HandoffsLogFile);
        Assert.Equal(Path.Combine(home, "simulate-lid"), p.SimulateLidFile);
    }

    [Fact]
    public void Sem_INSOMNIA_HOME_config_roameia_e_o_resto_e_local()
    {
        var p = Paths.FromEnvironment(_ => "  ");
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(roaming, "Insomnia", "config.json"), p.ConfigFile);
        Assert.Equal(Path.Combine(local, "Insomnia", "state.json"), p.JournalFile);
        Assert.Equal(Path.Combine(local, "Insomnia", "Logs", "insomnia.log"), p.LogFile);
        Assert.Equal(Path.Combine(local, "Programs", "Insomnia", "Insomnia.exe"), Paths.InstalledExecutablePath);
    }

    // ---------- ConfigService ----------

    [Fact]
    public void ConfigService_normaliza_grava_e_avisa()
    {
        var store = new FakeStore();
        var log = new FakeLog();
        var svc = new ConfigService(store, log);
        var changed = 0;
        svc.Changed += () => changed++;

        svc.Update(c => c with { EndFloor = 60 }); // fora de ordem: Normalized sobe a Economia

        Assert.Equal(60, svc.Current.EndFloor);
        Assert.Equal(65, svc.Current.LowPowerFloor);
        Assert.Equal(65, store.ConfigOnDisk!.LowPowerFloor);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void ConfigService_corrige_pisos_de_um_config_editado_a_mao()
    {
        var store = new FakeStore { ConfigOnDisk = Config.Default with { EndFloor = 50, LowPowerFloor = 20 } };
        var log = new FakeLog();
        var svc = new ConfigService(store, log);
        Assert.Equal(55, svc.Current.LowPowerFloor);
        Assert.Equal(55, store.ConfigOnDisk!.LowPowerFloor);
        Assert.True(log.Contains("pisos de bateria corrigidos"));
    }

    [Fact]
    public void ConfigService_com_config_corrompido_usa_o_padrao_e_nao_sobrescreve()
    {
        var store = new FakeStore { CorruptConfig = true };
        var log = new FakeLog();
        var svc = new ConfigService(store, log);
        Assert.Equal(Config.Default.EndFloor, svc.Current.EndFloor);
        Assert.NotNull(svc.LoadError);
        Assert.Empty(store.ConfigWrites);
        Assert.Single(log.Errors);
    }

    [Fact]
    public void ConfigService_falha_ao_gravar_mantem_em_memoria_e_loga()
    {
        var store = new FakeStore();
        var log = new FakeLog();
        var svc = new ConfigService(store, log);
        store.FailConfigWrites = true;
        svc.Update(c => c with { HotspotSsid = "Celular" });
        Assert.Equal("Celular", svc.Current.HotspotSsid);
        Assert.True(log.Contains("não deu para gravar"));
    }

    [Fact]
    public void ConfigService_em_disco_ida_e_volta()
    {
        using var home = new TempHome();
        var store = new FileStore(home.Paths, new FakeClock());
        var svc = new ConfigService(store, new FakeLog());
        Assert.True(File.Exists(home.Paths.ConfigFile)); // ausente ⇒ padrão gravado
        svc.Update(c => c.WithLowPowerFloor(30));
        var reopened = new ConfigService(new FileStore(home.Paths, new FakeClock()), new FakeLog());
        Assert.Equal(30, reopened.Current.LowPowerFloor);
    }
}
