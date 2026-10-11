using System.Runtime.InteropServices;
using System.Windows.Forms;
using Insomnia.Core.Abstractions;
// System.Windows.Forms (using global do WinForms) também tem PowerStatus.
using PowerStatus = Insomnia.Core.Abstractions.PowerStatus;

namespace Insomnia.Windows.Power;

/// IPowerEvents real: janela nativa OCULTA (top-level sem WS_VISIBLE — não uma janela
/// message-only, que não recebe broadcast) assinando, via RegisterPowerSettingNotification,
/// os três GUIDs de §7: tampa, fonte AC/DC e percentual de bateria. Os eventos chegam em
/// WM_POWERBROADCAST/PBT_POWERSETTINGCHANGE. Ao registrar, o Windows manda na hora o valor
/// atual de cada GUID, então CurrentLid se preenche sozinho logo depois do Start.
///
/// THREAD: Start() e Stop() TÊM de ser chamados no thread da UI (o que roda o laço de
/// mensagens do WinForms): a janela pertence ao thread que a cria, e é nesse thread que
/// LidChanged e PowerChanged são disparados. Chamado fora dele, nada chega (sem laço de
/// mensagens) ou o DestroyWindow falha. Laptop sem sensor de tampa ⇒ CurrentLid fica Unknown.
///
/// Os eventos são crus (sem debounce — o debounce de 2 s é do LidWatcher, no Core).
public sealed class PowerEventsWindow : NativeWindow, IPowerEvents, IDisposable
{
    private readonly ILog? _log;
    private readonly PowerStatusWin _status = new();
    private readonly List<IntPtr> _registrations = [];
    private volatile LidState _lid = LidState.Unknown;

    public PowerEventsWindow() : this(null) { }

    /// Mesma janela, com log das falhas de registro e de assinantes que lançam.
    public PowerEventsWindow(ILog? log) { _log = log; }

    public event Action<LidState>? LidChanged;
    public event Action? PowerChanged;

    public LidState CurrentLid => _lid;

    public PowerStatus ReadPower() => _status.Read();

    /// Idempotente. Só no thread da UI (ver o comentário da classe).
    public void Start()
    {
        if (Handle != IntPtr.Zero) return;
        CreateHandle(new CreateParams { Caption = "Insomnia.PowerEvents" });
        foreach (var guid in PowerSettingIds.Notifications)
        {
            var registration = PowerNative.RegisterPowerSettingNotification(Handle, guid, PowerNative.DeviceNotifyWindowHandle);
            if (registration == IntPtr.Zero)
                _log?.Error($"RegisterPowerSettingNotification({guid}) falhou: erro Win32 {Marshal.GetLastWin32Error()}");
            else
                _registrations.Add(registration);
        }
    }

    /// Idempotente. Só no thread da UI.
    public void Stop()
    {
        foreach (var registration in _registrations)
            PowerNative.UnregisterPowerSettingNotification(registration);
        _registrations.Clear();
        if (Handle != IntPtr.Zero) DestroyHandle();
        _lid = LidState.Unknown;
    }

    public void Dispose() => Stop();

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == PowerNative.WmPowerBroadcast)
        {
            switch ((int)m.WParam)
            {
                case PowerNative.PbtPowerSettingChange when m.LParam != IntPtr.Zero:
                    Dispatch(PowerBroadcast.Decode(CopySetting(m.LParam)));
                    break;
                case PowerNative.PbtApmPowerStatusChange:
                case PowerNative.PbtApmResumeAutomatic:
                    Dispatch(new PowerNotification(null, true));
                    break;
            }
            m.Result = new IntPtr(1);   // TRUE: mensagem tratada
            return;
        }
        base.WndProc(ref m);
    }

    /// Copia POWERBROADCAST_SETTING (cabeçalho + DataLength bytes) para um array gerenciado.
    private static byte[] CopySetting(IntPtr ptr)
    {
        var length = Marshal.ReadInt32(ptr, 16);
        if (length < 0 || length > 4096) length = 0;   // valor absurdo ⇒ só o cabeçalho
        var raw = new byte[PowerBroadcast.HeaderSize + length];
        Marshal.Copy(ptr, raw, 0, raw.Length);
        if (length == 0) BitConverter.TryWriteBytes(raw.AsSpan(16, 4), 0u);
        return raw;
    }

    private void Dispatch(PowerNotification n)
    {
        try
        {
            if (n.Lid is { } lid)
            {
                _lid = lid;
                LidChanged?.Invoke(lid);
            }
            if (n.PowerChanged) PowerChanged?.Invoke();
        }
        catch (Exception e)
        {
            // Assinante que lança não pode derrubar o laço de mensagens da UI.
            _log?.Error($"evento de energia: assinante lançou: {e.Message}");
        }
    }
}
