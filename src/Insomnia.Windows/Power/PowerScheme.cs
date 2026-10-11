using System.Runtime.InteropServices;

namespace Insomnia.Windows.Power;

/// Leitura e escrita de índices do esquema de energia via powrprof, sem administrador.
/// Toda falha vira InvalidOperationException com o código Win32 (o contrato pede "lança").
internal static class PowerScheme
{
    public static Guid ActiveScheme()
    {
        var code = PowerNative.PowerGetActiveScheme(IntPtr.Zero, out var ptr);
        if (code != PowerNative.ErrorSuccess || ptr == IntPtr.Zero)
            throw Fail("PowerGetActiveScheme", code);
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { PowerNative.LocalFree(ptr); }
    }

    public static uint ReadAc(Guid scheme, Guid subGroup, Guid setting)
    {
        var code = PowerNative.PowerReadACValueIndex(IntPtr.Zero, scheme, subGroup, setting, out var value);
        return code == PowerNative.ErrorSuccess ? value : throw Fail("PowerReadACValueIndex", code);
    }

    public static uint ReadDc(Guid scheme, Guid subGroup, Guid setting)
    {
        var code = PowerNative.PowerReadDCValueIndex(IntPtr.Zero, scheme, subGroup, setting, out var value);
        return code == PowerNative.ErrorSuccess ? value : throw Fail("PowerReadDCValueIndex", code);
    }

    public static void WriteAc(Guid scheme, Guid subGroup, Guid setting, uint value)
    {
        var code = PowerNative.PowerWriteACValueIndex(IntPtr.Zero, scheme, subGroup, setting, value);
        if (code != PowerNative.ErrorSuccess) throw Fail("PowerWriteACValueIndex", code);
    }

    public static void WriteDc(Guid scheme, Guid subGroup, Guid setting, uint value)
    {
        var code = PowerNative.PowerWriteDCValueIndex(IntPtr.Zero, scheme, subGroup, setting, value);
        if (code != PowerNative.ErrorSuccess) throw Fail("PowerWriteDCValueIndex", code);
    }

    /// Escrever um índice não muda o comportamento até o esquema ser (re)aplicado.
    /// Reaplica o esquema ATIVO, e não o escrito: se o usuário trocou de plano depois que o
    /// diário foi gravado, restaurar o plano antigo não pode arrancá-lo do plano novo — o
    /// valor escrito no plano inativo vale quando ele voltar a ser ativo.
    public static void ReapplyActive()
    {
        var active = ActiveScheme();
        var code = PowerNative.PowerSetActiveScheme(IntPtr.Zero, active);
        if (code != PowerNative.ErrorSuccess) throw Fail("PowerSetActiveScheme", code);
    }

    private static InvalidOperationException Fail(string api, uint code) =>
        new($"{api} falhou (erro Win32 {code}: {new System.ComponentModel.Win32Exception((int)code).Message})");
}
