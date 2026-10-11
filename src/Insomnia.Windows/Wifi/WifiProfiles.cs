namespace Insomnia.Windows.Wifi;

/// Decisões puras do adaptador de Wi-Fi (testáveis sem tocar no WLAN).
public static class WifiProfiles
{
    /// Nome do perfil salvo que corresponde ao SSID configurado. Igual exato vence; senão, um
    /// único perfil igual sem diferenciar caixa; ambíguo, vazio ou ausente ⇒ null.
    /// O WlanConnect precisa do nome exato, por isso o nome devolvido é o do perfil, não o digitado.
    public static string? Resolve(IEnumerable<string> profileNames, string ssid)
    {
        var wanted = (ssid ?? "").Trim();
        if (wanted.Length == 0) return null;
        var names = profileNames.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).ToList();
        var exact = names.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.Ordinal));
        if (exact is not null) return exact;
        var loose = names.Where(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return loose.Count == 1 ? loose[0] : null;
    }
}

/// Transforma leituras repetidas de "conectado?" em bordas: só a mudança vira evento. Várias
/// fontes (notificação WLAN, NetworkChange, sondagem) chamam Update; só a primeira de cada
/// mudança dispara. Thread-safe.
public sealed class ConnectivityEdge(bool initial)
{
    private readonly object _gate = new();
    private bool _last = initial;

    public bool Current { get { lock (_gate) return _last; } }

    /// Valor novo se mudou; null se é o mesmo de antes.
    public bool? Update(bool connected)
    {
        lock (_gate)
        {
            if (connected == _last) return null;
            _last = connected;
            return connected;
        }
    }

    public void Reset(bool connected) { lock (_gate) _last = connected; }
}
