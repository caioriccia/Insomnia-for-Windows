using Insomnia.Core.Abstractions;
using Insomnia.Core.Store;

namespace Insomnia.Core.Tests.Fundacao;

/// Lock de recuperação (FileShare.None, retentativa de 50 ms) e log com rotação.
public sealed class RecoveryLockAndLogTests : IDisposable
{
    private readonly TempHome _home = new();
    public void Dispose() => _home.Dispose();

    // ---------- lock ----------

    [Fact]
    public void R6_lock_segurado_impede_outro_FileStream_e_outra_instancia()
    {
        var a = new FileRecoveryLock(_home.Paths.LockFile);
        var b = new FileRecoveryLock(_home.Paths.LockFile);

        using (var held = a.TryAcquire())
        {
            Assert.NotNull(held);
            Assert.Null(b.TryAcquire());
            Assert.Null(a.TryAcquire());
            // Um FileStream qualquer (como o de outro processo) também é barrado.
            Assert.ThrowsAny<IOException>(() =>
                new FileStream(_home.Paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite).Dispose());
        }

        using var again = b.TryAcquire();
        Assert.NotNull(again);
        Assert.True(File.Exists(_home.Paths.LockFile)); // nunca apagado
    }

    [Fact]
    public void Lock_respeita_um_FileStream_externo_sem_compartilhamento()
    {
        Directory.CreateDirectory(_home.Root);
        var lk = new FileRecoveryLock(_home.Paths.LockFile);
        using (new FileStream(_home.Paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(lk.TryAcquire());
        using var held = lk.TryAcquire();
        Assert.NotNull(held);
    }

    [Fact]
    public void Lock_cria_a_pasta_se_faltar()
    {
        var path = Path.Combine(_home.Root, "sub", "dir", ".recovery.lock");
        using var held = new FileRecoveryLock(path).TryAcquire();
        Assert.NotNull(held);
    }

    [Fact]
    public async Task R6_AcquireAsync_estoura_com_RecoveryLockBusyException()
    {
        var lk = new FileRecoveryLock(_home.Paths.LockFile);
        using var held = lk.TryAcquire();
        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<RecoveryLockBusyException>(() => lk.AcquireAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None));
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public async Task AcquireAsync_pega_o_lock_quando_o_dono_solta_durante_a_espera()
    {
        var lk = new FileRecoveryLock(_home.Paths.LockFile);
        var held = lk.TryAcquire()!;
        _ = Task.Run(async () => { await Task.Delay(150); held.Dispose(); });
        using var got = await lk.AcquireAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.NotNull(got);
    }

    [Fact]
    public async Task AcquireAsync_respeita_cancelamento()
    {
        var lk = new FileRecoveryLock(_home.Paths.LockFile);
        using var held = lk.TryAcquire();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lk.AcquireAsync(TimeSpan.FromSeconds(10), cts.Token));
    }

    // ---------- log ----------

    [Fact]
    public void Log_escreve_linha_com_data_nivel_e_mensagem()
    {
        var now = new DateTimeOffset(2026, 10, 11, 15, 0, 0, TimeSpan.FromHours(-3));
        var log = new FileLog(_home.Paths.LogFile, now: () => now);
        log.Info("sessão iniciada");
        log.Error("linha 1\nlinha 2");
        var lines = File.ReadAllLines(_home.Paths.LogFile);
        Assert.Equal(["2026-10-11T15:00:00-03:00 [info] insomnia: sessão iniciada", "2026-10-11T15:00:00-03:00 [error] insomnia: linha 1 linha 2"], lines);
    }

    [Fact]
    public void Log_passa_do_teto_e_gira_para_ponto_1_substituindo_o_anterior()
    {
        var log = new FileLog(_home.Paths.LogFile, maxBytes: 200);
        Assert.Equal(1L << 20, FileLog.DefaultMaxBytes);

        Directory.CreateDirectory(_home.Paths.LogsDirectory);
        File.WriteAllText(log.RotatedPath, "ponto-1 antigo");
        for (var i = 0; i < 4; i++) log.Info($"antiga {i} " + new string('x', 60)); // > 200 bytes
        Assert.True(new FileInfo(log.Path).Length > 200);

        log.Info("nova");

        Assert.DoesNotContain("ponto-1 antigo", File.ReadAllText(log.RotatedPath));
        Assert.Contains("antiga 3", File.ReadAllText(log.RotatedPath));
        var current = File.ReadAllLines(log.Path);
        Assert.Single(current);
        Assert.EndsWith("nova", current[0]);
    }

    [Fact]
    public void Log_nao_gira_abaixo_do_teto()
    {
        var log = new FileLog(_home.Paths.LogFile, maxBytes: 10_000);
        log.Info("a");
        log.Info("b");
        Assert.False(File.Exists(log.RotatedPath));
        Assert.Equal(2, File.ReadAllLines(log.Path).Length);
    }

    [Fact]
    public void Log_nunca_lanca_mesmo_sem_poder_escrever()
    {
        Directory.CreateDirectory(_home.Paths.LogFile); // pasta no lugar do arquivo
        var log = new FileLog(_home.Paths.LogFile);
        log.Info("não lança");
        log.Error("não lança");
    }

    [Fact]
    public void Log_aceita_leitor_com_o_arquivo_aberto()
    {
        var log = new FileLog(_home.Paths.LogFile);
        log.Info("um");
        using var reader = new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        log.Info("dois");
        Assert.Equal(2, File.ReadAllLines(log.Path).Length);
    }
}
