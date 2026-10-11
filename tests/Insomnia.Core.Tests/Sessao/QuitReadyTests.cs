using Insomnia.Core.Abstractions;
using Insomnia.Core.Tests.Fakes;

namespace Insomnia.Core.Tests.Sessao;

using Insomnia.Core.Model;
using Insomnia.Core.Session;

/// Sair adiado: quando a retentativa de 30 s conclui o fim pedido por Quit, o núcleo avisa
/// (QuitReady) para o app fechar sozinho, sem novo clique em Sair.
public class QuitReadyTests
{
    private static readonly TimeSpan FourHours = TimeSpan.FromHours(4);

    [Fact]
    public async Task Quit_com_lock_ocupado_adia_e_a_retentativa_libera_a_saida_uma_vez()
    {
        var rig = new SessionRig();
        await rig.Manager.StartAsync(FourHours);
        var fired = 0;
        rig.Manager.QuitReady += () => Interlocked.Increment(ref fired);
        rig.Lock.Held = true;

        var result = await rig.Manager.EndAsync(EndReason.Quit);

        Assert.Equal(EndOutcome.Locked, result.Outcome);
        Assert.False(result.LetsQuitGo);
        Assert.Equal(0, Volatile.Read(ref fired)); // adiado: ainda não libera

        rig.Lock.Held = false;
        rig.Scheduler.Advance(SessionManager.RetryDelay);
        await SessionRig.Until(() => Volatile.Read(ref fired) == 1);
        Assert.Equal(1u, rig.Sleep.Lid.Ac);
        Assert.Null(rig.Store.SessionOnDisk);

        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref fired)); // exatamente uma vez
        Assert.Equal(0, rig.Scheduler.PendingCount); // nenhuma retentativa sobrando
    }

    [Fact]
    public async Task Quit_incompleto_sem_backstop_adia_e_a_retentativa_libera_a_saida()
    {
        var rig = new SessionRig();
        await rig.Manager.StartAsync(FourHours);
        rig.Sleep.FailWriteIf = v => v.Ac == 1;
        rig.Backstop.State = BackstopState.Missing;
        rig.Backstop.FailInstall = true;
        var fired = 0;
        rig.Manager.QuitReady += () => Interlocked.Increment(ref fired);

        var result = await rig.Manager.EndAsync(EndReason.Quit);

        Assert.False(result.LetsQuitGo);
        Assert.Equal(0, Volatile.Read(ref fired));

        rig.Sleep.FailWriteIf = null;
        rig.Backstop.FailInstall = false;
        rig.Scheduler.Advance(SessionManager.RetryDelay);
        await SessionRig.Until(() => Volatile.Read(ref fired) == 1);
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref fired));
    }

    [Fact]
    public async Task Quit_que_restaura_de_primeira_ja_dispara_o_sinal()
    {
        // Comportamento documentado: o sinal também dispara no 1º Quit bem-sucedido (o AppHost
        // tolera, pois sai uma vez só). O resultado devolvido continua sendo LetsQuitGo.
        var rig = new SessionRig();
        await rig.Manager.StartAsync(FourHours);
        var fired = 0;
        rig.Manager.QuitReady += () => Interlocked.Increment(ref fired);

        var result = await rig.Manager.EndAsync(EndReason.Quit);

        Assert.True(result.LetsQuitGo);
        Assert.Equal(1, Volatile.Read(ref fired));
    }

    [Fact]
    public async Task Retentativa_de_fim_por_outra_razao_nao_dispara_o_sinal()
    {
        var rig = new SessionRig();
        await rig.Manager.StartAsync(FourHours);
        var fired = 0;
        rig.Manager.QuitReady += () => Interlocked.Increment(ref fired);
        rig.Lock.Held = true;

        var first = await rig.Manager.EndAsync(EndReason.Timer);
        Assert.Equal(EndOutcome.Locked, first.Outcome);

        rig.Lock.Held = false;
        rig.Scheduler.Advance(SessionManager.RetryDelay);
        await SessionRig.Until(() => !rig.Manager.IsActive && rig.Sleep.Lid.Ac == 1);
        await Task.Delay(100);

        Assert.Equal(0, Volatile.Read(ref fired));
    }

    [Fact]
    public async Task Quit_com_diario_ilegivel_nao_dispara_nem_agenda_retentativa()
    {
        var rig = new SessionRig();
        rig.Store.CorruptJournal = true;
        var fired = 0;
        rig.Manager.QuitReady += () => Interlocked.Increment(ref fired);

        var result = await rig.Manager.EndAsync(EndReason.Quit);

        Assert.Equal(EndOutcome.JournalUnreadable, result.Outcome);
        Assert.Equal(0, rig.Scheduler.PendingCount);
        Assert.Equal(0, Volatile.Read(ref fired));
    }

    [Fact]
    public async Task Assinante_que_lanca_nao_derruba_o_fim()
    {
        var rig = new SessionRig();
        await rig.Manager.StartAsync(FourHours);
        rig.Manager.QuitReady += () => throw new InvalidOperationException("boom");

        var result = await rig.Manager.EndAsync(EndReason.Quit);

        Assert.True(result.LetsQuitGo);
    }
}
