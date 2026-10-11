using System.Buffers.Binary;

namespace Insomnia.Windows.Processes;

/// Leitura pura do buffer de NtQuerySystemInformation(SystemProcessInformation): uma cadeia de
/// SYSTEM_PROCESS_INFORMATION (ligada por NextEntryOffset), cada uma seguida do seu vetor de
/// SYSTEM_THREAD_INFORMATION. Daqui sai só "o processo está parado?": parado ⇔ tem threads e
/// TODAS estão em Waiting (5) com WaitReason Suspended (5).
///
/// Offsets das estruturas (estáveis desde o Vista), em x64 / x86:
///   processo: NumberOfThreads 4/4 · UniqueProcessId 80/68 (ponteiro) · tamanho 256/184
///   thread:   ThreadState 68/52 · WaitReason 72/56 · tamanho 80/64
/// Buffer malformado nunca lança: devolve o que leu até ali.
public static class SystemProcessTable
{
    public const int ThreadStateWaiting = 5;
    public const int WaitReasonSuspended = 5;

    public readonly record struct Layout(int ProcessSize, int PidOffset, int ThreadSize, int StateOffset, int WaitReasonOffset, bool Is64Bit)
    {
        public static Layout X64 { get; } = new(256, 80, 80, 68, 72, true);
        public static Layout X86 { get; } = new(184, 68, 64, 52, 56, false);
        public static Layout Current => IntPtr.Size == 8 ? X64 : X86;
    }

    /// pid ⇒ está parado. Processos ausentes do mapa: tratar como "não parado".
    public static Dictionary<int, bool> ParseSuspended(ReadOnlySpan<byte> buffer, Layout layout)
    {
        var result = new Dictionary<int, bool>();
        var offset = 0;
        while (offset >= 0 && offset + layout.ProcessSize <= buffer.Length)
        {
            var entry = buffer[offset..];
            var next = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var threads = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            var pid = layout.Is64Bit
                ? (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[layout.PidOffset..])
                : BinaryPrimitives.ReadUInt32LittleEndian(entry[layout.PidOffset..]);

            var threadsEnd = (long)layout.ProcessSize + (long)threads * layout.ThreadSize;
            if (pid is >= 0 and <= int.MaxValue && threadsEnd <= entry.Length)
            {
                var suspended = threads > 0;
                for (var t = 0; t < threads && suspended; t++)
                {
                    var thread = entry.Slice(layout.ProcessSize + t * layout.ThreadSize, layout.ThreadSize);
                    var state = BinaryPrimitives.ReadInt32LittleEndian(thread[layout.StateOffset..]);
                    var reason = BinaryPrimitives.ReadInt32LittleEndian(thread[layout.WaitReasonOffset..]);
                    suspended = state == ThreadStateWaiting && reason == WaitReasonSuspended;
                }
                result[(int)pid] = suspended;
            }

            if (next == 0 || next > int.MaxValue - offset) break;
            offset += (int)next;
        }
        return result;
    }
}
