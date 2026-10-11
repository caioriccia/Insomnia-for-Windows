using System.ComponentModel;
using System.Runtime.InteropServices;
using Insomnia.Core.Abstractions;

namespace Insomnia.Windows.Power;

/// ISleepGuard real: duas metades com vidas diferentes.
///
/// - Hold de execução: PowerCreateRequest + PowerSetRequest(SystemRequired e
///   ExecutionRequired). É do processo — se ele morrer, o Windows solta sozinho —, por isso
///   não é diarizado (R7). Aparece em `powercfg /requests`. Nunca lança: falha vira log e
///   IsHoldingSystemAwake fica false.
/// - LIDACTION (SUB_BUTTONS/LIDACTION) em AC e DC do esquema: persiste depois do processo,
///   por isso o Core diariza o valor anterior ANTES de chamar WriteLidAction (R1).
public sealed class SleepGuardWin : ISleepGuard, IDisposable
{
    private const string Reason = "Insomnia: sessão ativa mantém o computador acordado";

    private readonly ILog _log;
    private readonly object _gate = new();
    private IntPtr _request = IntPtr.Zero;
    private IntPtr _reasonText = IntPtr.Zero;

    public SleepGuardWin(ILog log) { _log = log; }

    public bool IsHoldingSystemAwake { get { lock (_gate) return _request != IntPtr.Zero; } }

    public void HoldSystemAwake(bool hold)
    {
        lock (_gate)
        {
            if (hold == (_request != IntPtr.Zero)) return;   // idempotente
            if (hold) Acquire(); else Release();
        }
    }

    private void Acquire()
    {
        // A string do motivo fica viva enquanto o pedido existir (liberada em Release).
        _reasonText = Marshal.StringToHGlobalUni(Reason);
        var context = new PowerNative.ReasonContext
        {
            Version = PowerNative.PowerRequestContextVersion,
            Flags = PowerNative.PowerRequestContextSimpleString,
            SimpleReasonString = _reasonText,
        };
        var handle = PowerNative.PowerCreateRequest(ref context);
        if (handle == IntPtr.Zero || handle == PowerNative.InvalidHandleValue)
        {
            _log.Error($"PowerCreateRequest falhou: {LastError()}; o computador não está seguro acordado");
            FreeReason();
            return;
        }
        if (!PowerNative.PowerSetRequest(handle, PowerNative.PowerRequestType.SystemRequired))
        {
            _log.Error($"PowerSetRequest(SystemRequired) falhou: {LastError()}");
            PowerNative.CloseHandle(handle);
            FreeReason();
            return;
        }
        // ExecutionRequired é o que segura o Modern Standby; em máquina sem ele o Windows o
        // trata como SystemRequired. Falhar aqui não desfaz o SystemRequired já obtido.
        if (!PowerNative.PowerSetRequest(handle, PowerNative.PowerRequestType.ExecutionRequired))
            _log.Error($"PowerSetRequest(ExecutionRequired) falhou: {LastError()}; segue só com SystemRequired");
        _request = handle;
        _log.Info("hold de energia ligado (SystemRequired + ExecutionRequired)");
    }

    private void Release()
    {
        PowerNative.PowerClearRequest(_request, PowerNative.PowerRequestType.ExecutionRequired);
        PowerNative.PowerClearRequest(_request, PowerNative.PowerRequestType.SystemRequired);
        PowerNative.CloseHandle(_request);
        _request = IntPtr.Zero;
        FreeReason();
        _log.Info("hold de energia desligado");
    }

    private void FreeReason()
    {
        if (_reasonText == IntPtr.Zero) return;
        Marshal.FreeHGlobal(_reasonText);
        _reasonText = IntPtr.Zero;
    }

    public LidActionValues ReadLidAction()
    {
        var scheme = PowerScheme.ActiveScheme();
        var ac = PowerScheme.ReadAc(scheme, PowerSettingIds.SubButtons, PowerSettingIds.LidAction);
        var dc = PowerScheme.ReadDc(scheme, PowerSettingIds.SubButtons, PowerSettingIds.LidAction);
        return new LidActionValues(scheme, ac, dc);
    }

    public void WriteLidAction(Guid schemeId, uint ac, uint dc)
    {
        try
        {
            PowerScheme.WriteAc(schemeId, PowerSettingIds.SubButtons, PowerSettingIds.LidAction, ac);
            PowerScheme.WriteDc(schemeId, PowerSettingIds.SubButtons, PowerSettingIds.LidAction, dc);
            PowerScheme.ReapplyActive();
            _log.Info($"LIDACTION do esquema {schemeId} = AC {ac}, DC {dc}");
        }
        catch (Exception e)
        {
            _log.Error($"falha ao escrever LIDACTION (esquema {schemeId}, AC {ac}, DC {dc}): {e.Message}");
            throw;
        }
    }

    public void Dispose() => HoldSystemAwake(false);

    private static string LastError()
    {
        var code = Marshal.GetLastWin32Error();
        return $"erro Win32 {code}: {new Win32Exception(code).Message}";
    }
}
