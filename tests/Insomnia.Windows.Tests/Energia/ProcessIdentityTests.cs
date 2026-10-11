using System.Buffers.Binary;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Model;
using Insomnia.Core.Store;
using Insomnia.Windows.Processes;

namespace Insomnia.Windows.Tests.Energia;

/// Identidade de processo (R8), BootId e a leitura de "parado" — tudo puro.
public class ProcessIdentityTests
{
    private static readonly DateTime Created = new(2026, 10, 11, 9, 12, 3, DateTimeKind.Utc);

    private static long FileTime(DateTime utc, long extraTicks = 0) => utc.ToFileTimeUtc() + extraTicks;

    // ---------- BootId ----------

    [Fact]
    public void ParseBootId_DWORD_do_registro_vira_decimal_sem_sinal()
    {
        Assert.Equal("43", ProcessIdentityRules.ParseBootId(0x2b));
        Assert.Equal("4294967295", ProcessIdentityRules.ParseBootId(-1));   // DWORD 0xFFFFFFFF chega como int -1
        Assert.Equal("17", ProcessIdentityRules.ParseBootId(17L));
        Assert.Equal("17", ProcessIdentityRules.ParseBootId(" 17 "));
        Assert.Equal("17", ProcessIdentityRules.ParseBootId(new byte[] { 17, 0, 0, 0 }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-3")]
    public void ParseBootId_ilegivel_e_vazio(object? value)
    {
        Assert.Equal("", ProcessIdentityRules.ParseBootId(value));
    }

    [Fact]
    public void ParseBootId_tipos_inesperados_sao_vazio()
    {
        Assert.Equal("", ProcessIdentityRules.ParseBootId(-5L));
        Assert.Equal("", ProcessIdentityRules.ParseBootId(new byte[] { 1, 2 }));
        Assert.Equal("", ProcessIdentityRules.ParseBootId(new[] { "17" }));
    }

    // ---------- identidade ----------

    [Fact]
    public void R8_sem_BootId_ou_sem_criacao_nao_ha_identidade()
    {
        Assert.Null(ProcessIdentityRules.FromFileTime(FileTime(Created), ""));
        Assert.Null(ProcessIdentityRules.FromFileTime(0, "17"));
        Assert.Null(ProcessIdentityRules.FromFileTime(-1, "17"));
        Assert.Null(ProcessIdentityRules.FromFileTime(long.MaxValue, "17"));
    }

    [Fact]
    public void Identidade_e_criacao_em_UTC_truncada_ao_segundo_mais_BootId()
    {
        var id = ProcessIdentityRules.FromFileTime(FileTime(Created, extraTicks: 9_999_999), "17");
        Assert.Equal(new ProcessIdentity(new DateTimeOffset(Created, TimeSpan.Zero), "17"), id);
        Assert.Equal(TimeSpan.Zero, id!.Value.CreationTime.Offset);
    }

    [Fact]
    public void Mesma_criacao_no_mesmo_boot_e_a_mesma_identidade()
    {
        var a = ProcessIdentityRules.FromFileTime(FileTime(Created, 1), "17");
        var b = ProcessIdentityRules.FromFileTime(FileTime(Created, 5_000_000), "17");
        Assert.Equal(a, b);
    }

    [Fact]
    public void R8_pid_reusado_outro_segundo_ou_outro_boot_e_outra_identidade()
    {
        var original = ProcessIdentityRules.FromFileTime(FileTime(Created), "17");
        Assert.NotEqual(original, ProcessIdentityRules.FromFileTime(FileTime(Created.AddSeconds(1)), "17"));
        Assert.NotEqual(original, ProcessIdentityRules.FromFileTime(FileTime(Created), "18"));
    }

    [Fact]
    public void R8_identidade_sobrevive_ida_e_volta_pelo_diario()
    {
        // O ponto da truncagem: a entrada relida do state.json tem de ser == à identidade
        // fresca que o Lookup devolve, senão o Core nunca retoma o processo congelado.
        var fresh = ProcessIdentityRules.FromFileTime(FileTime(Created, 1_234_567), "17")!.Value;
        var journal = Journal.Clean with
        {
            FrozenProcesses = [new FrozenProcess(4312, "slack.exe", fresh.CreationTime, fresh.BootId, "slack.exe")],
        };

        var reread = JsonCodec.ParseJournal(JsonCodec.SerializeJournal(journal)).FrozenProcesses.Single();

        Assert.True(reread.HasIdentity);
        Assert.Equal(fresh, new ProcessIdentity(reread.CreationTime!.Value, reread.BootId!));
    }

    [Fact]
    public void Normalize_converte_fuso_e_trunca()
    {
        var local = new DateTimeOffset(2026, 10, 11, 6, 12, 3, 999, TimeSpan.FromHours(-3));
        var normalized = ProcessIdentityRules.Normalize(local);
        Assert.Equal(new DateTimeOffset(Created, TimeSpan.Zero), normalized);
        Assert.Equal(TimeSpan.Zero, normalized.Offset);
    }

    // ---------- "parado" por NtQuerySystemInformation ----------

    private sealed record FakeProcess(long Pid, (int State, int Reason)[] Threads);

    /// Monta um buffer SystemProcessInformation sintético no layout pedido.
    private static byte[] BuildBuffer(SystemProcessTable.Layout layout, params FakeProcess[] processes)
    {
        var sizes = processes.Select(p => layout.ProcessSize + p.Threads.Length * layout.ThreadSize).ToArray();
        var buffer = new byte[sizes.Sum()];
        var offset = 0;
        for (var i = 0; i < processes.Length; i++)
        {
            var span = buffer.AsSpan(offset, sizes[i]);
            var next = i == processes.Length - 1 ? 0u : (uint)sizes[i];
            BinaryPrimitives.WriteUInt32LittleEndian(span, next);
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)processes[i].Threads.Length);
            if (layout.Is64Bit) BinaryPrimitives.WriteUInt64LittleEndian(span[layout.PidOffset..], (ulong)processes[i].Pid);
            else BinaryPrimitives.WriteUInt32LittleEndian(span[layout.PidOffset..], (uint)processes[i].Pid);
            for (var t = 0; t < processes[i].Threads.Length; t++)
            {
                var thread = span.Slice(layout.ProcessSize + t * layout.ThreadSize, layout.ThreadSize);
                BinaryPrimitives.WriteInt32LittleEndian(thread[layout.StateOffset..], processes[i].Threads[t].State);
                BinaryPrimitives.WriteInt32LittleEndian(thread[layout.WaitReasonOffset..], processes[i].Threads[t].Reason);
            }
            offset += sizes[i];
        }
        return buffer;
    }

    private static readonly (int, int) Suspended = (SystemProcessTable.ThreadStateWaiting, SystemProcessTable.WaitReasonSuspended);
    private static readonly (int, int) Running = (2, 0);
    private static readonly (int, int) WaitingOther = (SystemProcessTable.ThreadStateWaiting, 6);

    public static TheoryData<string> Layouts => ["x64", "x86"];

    private static SystemProcessTable.Layout LayoutFor(string name) => name == "x64" ? SystemProcessTable.Layout.X64 : SystemProcessTable.Layout.X86;

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Parado_so_quando_todas_as_threads_estao_suspensas(string layoutName)
    {
        var layout = LayoutFor(layoutName);
        var buffer = BuildBuffer(layout,
            new FakeProcess(0, []),                                  // Idle: sem threads ⇒ não parado
            new FakeProcess(4312, [Suspended, Suspended]),
            new FakeProcess(5000, [Suspended, Running]),
            new FakeProcess(6000, [WaitingOther]));

        var map = SystemProcessTable.ParseSuspended(buffer, layout);

        Assert.False(map[0]);
        Assert.True(map[4312]);
        Assert.False(map[5000]);
        Assert.False(map[6000]);
    }

    [Fact]
    public void Buffer_truncado_nao_lanca_e_devolve_o_que_leu()
    {
        var layout = SystemProcessTable.Layout.X64;
        var buffer = BuildBuffer(layout, new FakeProcess(10, [Suspended]), new FakeProcess(20, [Suspended, Suspended]));
        var cut = buffer.AsSpan(0, buffer.Length - 8).ToArray();   // corta a última thread do segundo

        var map = SystemProcessTable.ParseSuspended(cut, layout);

        Assert.True(map[10]);
        Assert.False(map.ContainsKey(20));
        Assert.Empty(SystemProcessTable.ParseSuspended([], layout));
        Assert.Empty(SystemProcessTable.ParseSuspended(new byte[10], layout));
    }
}
