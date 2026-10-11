using Insomnia.Core.Abstractions;
using Insomnia.Core.Tests.Fakes;
using Insomnia.Core.Time;

namespace Insomnia.Core.Tests.Fundacao;

using Insomnia.Core.Model;

/// Os fakes são a base dos testes das outras frentes: aqui se prova que fazem o que dizem.
public class FakesTests
{
    [Fact]
    public void FakeScheduler_executa_vencidos_em_ordem_e_acompanha_o_relogio()
    {
        var clock = new FakeClock();
        var s = new FakeScheduler(clock);
        var ran = new List<string>();
        s.Schedule(TimeSpan.FromSeconds(30), () => ran.Add($"30@{(clock.Now - FakeClock.DefaultStart).TotalSeconds}"));
        s.Schedule(TimeSpan.FromSeconds(10), () =>
        {
            ran.Add("10");
            s.Schedule(TimeSpan.FromSeconds(5), () => ran.Add("10+5")); // agendado durante a execução
        });
        var cancelled = s.Schedule(TimeSpan.FromSeconds(20), () => ran.Add("20"));
        cancelled.Dispose();

        s.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(["10", "10+5"], ran);
        Assert.Equal(1, s.PendingCount);

        s.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(["10", "10+5", "30@30"], ran);
        Assert.Equal(FakeClock.DefaultStart.AddSeconds(30), clock.Now);
    }

    [Fact]
    public void FakeStore_botoes_de_falha_e_corrupcao()
    {
        var store = new FakeStore();
        store.FailNextJournalWrite = true;
        Assert.Throws<StoreWriteException>(() => store.SaveJournal(Journal.Clean));
        store.SaveJournal(Journal.Clean with { DockerFrozen = true });
        Assert.Single(store.JournalWrites);

        store.CorruptJournal = true;
        Assert.Throws<StoreCorruptException>(() => store.LoadJournal());

        store.CorruptSession = true;
        Assert.Throws<StoreCorruptException>(() => store.LoadSession());
        Assert.NotNull(store.MoveAsideUnreadableSession());
        Assert.Null(store.LoadSession());

        store.FailAllJournalWrites = true;
        Assert.Throws<StoreWriteException>(() => store.SaveJournal(Journal.Clean));
        Assert.Throws<StoreWriteException>(() => store.SaveJournal(Journal.Clean));
    }

    [Fact]
    public void JournalTransaction_nao_atualiza_quando_a_escrita_falha()
    {
        var store = new FakeStore { FailNextJournalWrite = true };
        var tx = new JournalTransaction(store, Journal.Clean);
        Assert.Throws<StoreWriteException>(() => tx.Write(Journal.Clean with { DockerFrozen = true }));
        Assert.False(tx.Journal.DockerFrozen);
        tx.Write(Journal.Clean with { DockerFrozen = true });
        Assert.True(tx.Journal.DockerFrozen);
        Assert.True(store.LoadJournal().DockerFrozen);
    }

    [Fact]
    public async Task FakeRecoveryLock_Held_forca_ocupado()
    {
        var lk = new FakeRecoveryLock { Held = true };
        Assert.Null(lk.TryAcquire());
        await Assert.ThrowsAsync<RecoveryLockBusyException>(() => lk.AcquireAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
        lk.Held = false;
        using (var h = lk.TryAcquire()) { Assert.NotNull(h); Assert.Null(lk.TryAcquire()); }
        Assert.NotNull(lk.TryAcquire());
    }

    [Fact]
    public void Timeline_prova_ordem_entre_fakes()
    {
        var t = new Timeline();
        var store = new FakeStore(t);
        var sleep = new FakeSleepGuard(t);
        store.SaveJournal(Journal.Clean);
        sleep.WriteLidAction(FakeSchemes.Balanced, 0, 0);
        Assert.True(t.IndexOf("store.SaveJournal") < t.IndexOf("sleep.WriteLidAction(0,0)"));
    }

    [Fact]
    public void FakeProcessWorld_identidade_suspensao_e_reuso_de_pid()
    {
        var w = new FakeProcessWorld();
        var created = FakeClock.DefaultStart.AddHours(-2);
        w.Add(100, "slack.exe", 1, created);
        Assert.Equal(new ProcessIdentity(created, "17"), w.Lookup(100)!.Identity);
        Assert.True(w.Suspend(100));
        Assert.True(w.Lookup(100)!.IsSuspended);
        w.Kill(100);
        Assert.Null(w.Lookup(100));
        Assert.False(w.Resume(100));
        w.Add(100, "outro.exe", 1, created.AddHours(1)); // pid reusado
        Assert.NotEqual(new ProcessIdentity(created, "17"), w.Lookup(100)!.Identity);
        Assert.Equal([("suspend", 100), ("resume", 100)], w.Signals);
        w.BootId = "";
        Assert.Null(w.Lookup(100)!.Identity);
    }

    [Fact]
    public void FakeAudio_desconectar_e_mutar()
    {
        var a = new FakeAudio();
        var changes = 0;
        a.DevicesChanged += () => changes++;
        a.SetMute(FakeAudio.SpeakersId, true);
        Assert.True(a.Read(FakeAudio.SpeakersId).Muted);
        a.Disconnect(FakeAudio.SpeakersId);
        Assert.Throws<AudioDeviceMissingException>(() => a.Read(FakeAudio.SpeakersId));
        Assert.Throws<AudioUnavailableException>(() => a.ReadDefaultOutput());
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task FakeLidUndo_padrao_limpa_e_deixa_saida_desconectada()
    {
        var store = new FakeStore();
        var j = Journal.Clean with
        {
            FrozenProcesses = [new FrozenProcess(1, "a.exe", null, null, "a.exe")],
            SavedAudioOutputs = [new SavedAudioOutput("x", null, 1, false), new SavedAudioOutput("y", null, 1, false)],
        };
        var undo = new FakeLidUndo();
        undo.NotConnected.Add("y");
        var tx = new JournalTransaction(store, j);
        var result = await undo.UndoAsync(tx, CancellationToken.None);
        Assert.Empty(tx.Journal.FrozenProcesses);
        Assert.Equal("y", tx.Journal.SavedAudioOutputs.Single().DeviceId);
        Assert.False(tx.Journal.IsDirty(result.AudioDevicesNotConnected));
    }

    [Fact]
    public void ThreadPoolScheduler_roda_a_acao_e_cancela()
    {
        var log = new FakeLog();
        var s = new ThreadPoolScheduler(log);
        using var ran = new ManualResetEventSlim();
        s.Schedule(TimeSpan.FromMilliseconds(10), ran.Set);
        Assert.True(ran.Wait(TimeSpan.FromSeconds(5)));

        var cancelledRan = false;
        s.Schedule(TimeSpan.FromMilliseconds(200), () => cancelledRan = true).Dispose();
        using var boom = new ManualResetEventSlim();
        s.Schedule(TimeSpan.Zero, () => { boom.Set(); throw new InvalidOperationException("x"); });
        Assert.True(boom.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(400);
        Assert.False(cancelledRan);
        Assert.True(log.Contains("tarefa agendada falhou"));
        Assert.Equal(0, s.PendingCount);
    }
}
