using Insomnia.Core.Abstractions;
using Insomnia.Core.Store;
using Insomnia.Core.Tests.Fakes;

namespace Insomnia.Core.Tests.Fundacao;

using Insomnia.Core.Model;

/// FileStore em disco real, numa pasta temporária. Prova R5: evidência ilegível nunca é tocada.
public sealed class FileStoreTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 11, 18, 30, 5, TimeSpan.Zero));
    private readonly FakeLog _log = new();
    private readonly FileStore _store;

    public FileStoreTests() => _store = new FileStore(_home.Paths, _clock, _log);
    public void Dispose() => _home.Dispose();

    private static Session ASession() =>
        new(new DateTimeOffset(2026, 10, 11, 18, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 11, 22, 0, 0, TimeSpan.Zero), []);

    // ---------- sessão ----------

    [Fact]
    public void Sessao_ausente_e_null()
    {
        Assert.Null(_store.LoadSession());
        Assert.Empty(_home.Files());
    }

    [Fact]
    public void Sessao_gravada_e_lida_de_volta_sem_temporarios()
    {
        _store.SaveSession(ASession());
        Assert.Equal(ASession().EndsAt, _store.LoadSession()!.EndsAt);
        Assert.Equal(["session.json"], _home.Files());
    }

    [Fact]
    public void Apagar_sessao_e_idempotente()
    {
        _store.DeleteSession();
        _store.SaveSession(ASession());
        _store.DeleteSession();
        _store.DeleteSession();
        Assert.Null(_store.LoadSession());
    }

    [Fact]
    public void Apagar_sessao_com_pasta_inexistente_nao_lanca()
    {
        var store = new FileStore(Paths.UnderHome(Path.Combine(_home.Root, "nao-existe")), _clock);
        store.DeleteSession();
    }

    [Fact]
    public void R5_sessao_ilegivel_lanca_e_o_arquivo_nao_e_tocado()
    {
        File.WriteAllText(_home.Paths.SessionFile, "{ quebrado");
        var before = File.GetLastWriteTimeUtc(_home.Paths.SessionFile);
        var ex = Assert.Throws<StoreCorruptException>(() => _store.LoadSession());
        Assert.Equal(_home.Paths.SessionFile, ex.Path);
        Assert.Equal("{ quebrado", File.ReadAllText(_home.Paths.SessionFile));
        Assert.Equal(before, File.GetLastWriteTimeUtc(_home.Paths.SessionFile));
        Assert.Equal(["session.json"], _home.Files());
    }

    [Fact]
    public void R5_sessao_ilegivel_e_movida_para_o_lado_com_o_carimbo_UTC()
    {
        File.WriteAllText(_home.Paths.SessionFile, "lixo");
        var moved = _store.MoveAsideUnreadableSession();
        Assert.Equal(Path.Combine(_home.Root, "session.json.unreadable-20261011T183005Z"), moved);
        Assert.False(File.Exists(_home.Paths.SessionFile));
        Assert.Equal("lixo", File.ReadAllText(moved!));
        Assert.Null(_store.LoadSession());
    }

    [Fact]
    public void R5_mover_sessao_ilegivel_nunca_sobrescreve()
    {
        var taken = Path.Combine(_home.Root, "session.json.unreadable-20261011T183005Z");
        File.WriteAllText(taken, "primeira");
        File.WriteAllText(taken + "-1", "segunda");

        File.WriteAllText(_home.Paths.SessionFile, "terceira");
        var moved = _store.MoveAsideUnreadableSession();

        Assert.Equal(taken + "-2", moved);
        Assert.Equal("primeira", File.ReadAllText(taken));
        Assert.Equal("segunda", File.ReadAllText(taken + "-1"));
        Assert.Equal("terceira", File.ReadAllText(moved!));
    }

    [Fact]
    public void Mover_sessao_sem_arquivo_devolve_null() => Assert.Null(_store.MoveAsideUnreadableSession());

    // ---------- diário ----------

    [Fact]
    public void Diario_ausente_e_limpo_e_nao_cria_arquivo()
    {
        var j = _store.LoadJournal();
        Assert.False(j.IsDirty());
        Assert.Empty(_home.Files());
    }

    [Fact]
    public void Diario_gravado_e_lido_de_volta()
    {
        var j = Journal.Clean with { LidAction = new LidActionEntry(FakeSchemes.Balanced, 1, 3, _clock.Now) };
        _store.SaveJournal(j);
        Assert.Equal(j.LidAction, _store.LoadJournal().LidAction);
        Assert.Equal(["state.json"], _home.Files());
    }

    [Fact]
    public void R5_diario_corrompido_lanca_e_nao_e_tocado()
    {
        var bytes = "{ \"version\": 1, \"lidAction\": { \"schemeId\": 42 } }"u8.ToArray();
        File.WriteAllBytes(_home.Paths.JournalFile, bytes);
        var before = File.GetLastWriteTimeUtc(_home.Paths.JournalFile);

        var ex = Assert.Throws<StoreCorruptException>(() => _store.LoadJournal());
        Assert.Equal(_home.Paths.JournalFile, ex.Path);
        Assert.Throws<StoreCorruptException>(() => _store.LoadJournal()); // continua corrompido: nada "consertou"

        Assert.Equal(bytes, File.ReadAllBytes(_home.Paths.JournalFile));
        Assert.Equal(before, File.GetLastWriteTimeUtc(_home.Paths.JournalFile));
        Assert.Equal(["state.json"], _home.Files()); // nem cópia, nem temporário, nem renomeação
    }

    [Fact]
    public void Diario_com_bytes_invalidos_e_corrompido()
    {
        File.WriteAllBytes(_home.Paths.JournalFile, [0x7B, 0xFF, 0xFE, 0x7D]);
        Assert.Throws<StoreCorruptException>(() => _store.LoadJournal());
    }

    [Fact]
    public void Escrita_do_diario_e_atomica_e_substitui_o_anterior()
    {
        _store.SaveJournal(Journal.Clean with { DockerFrozen = true });
        _store.SaveJournal(Journal.Clean);
        Assert.False(_store.LoadJournal().DockerFrozen);
        Assert.Equal(["state.json"], _home.Files());
    }

    [Fact]
    public void Escrita_que_falha_vira_StoreWriteException_e_nao_deixa_temporario()
    {
        // Uma pasta no lugar do destino faz o File.Move falhar.
        Directory.CreateDirectory(_home.Paths.JournalFile);
        var ex = Assert.Throws<StoreWriteException>(() => _store.SaveJournal(Journal.Clean));
        Assert.Equal(_home.Paths.JournalFile, ex.Path);
        Assert.DoesNotContain(_home.Files(), f => f.Contains(".tmp-", StringComparison.Ordinal));
    }

    // ---------- config ----------

    [Fact]
    public void Config_ausente_vira_padrao_e_e_gravado()
    {
        var c = _store.LoadConfig();
        Assert.Equal(Config.Default.EndFloor, c.EndFloor);
        Assert.True(File.Exists(_home.Paths.ConfigFile));
        Assert.Equal(Config.Default.AgentList.ToArray(), JsonCodec.ParseConfig(File.ReadAllText(_home.Paths.ConfigFile)).AgentList.ToArray());
    }

    [Fact]
    public void Config_corrompido_lanca_e_nao_e_tocado()
    {
        File.WriteAllText(_home.Paths.ConfigFile, "{ \"endFloor\": \"dez\" }");
        Assert.Throws<StoreCorruptException>(() => _store.LoadConfig());
        Assert.Equal("{ \"endFloor\": \"dez\" }", File.ReadAllText(_home.Paths.ConfigFile));
    }

    [Fact]
    public void Config_com_BOM_do_Bloco_de_Notas_e_lido()
    {
        File.WriteAllBytes(_home.Paths.ConfigFile, [0xEF, 0xBB, 0xBF, .. "{ \"endFloor\": 5 }"u8.ToArray()]);
        Assert.Equal(5, _store.LoadConfig().EndFloor);
    }
}
