using Insomnia.Core.Abstractions;
using Insomnia.Core.Model;

namespace Insomnia.Core.Store;

/// Fonte única das preferências em memória, gravando em config.json a cada mudança.
///
/// Ao abrir: arquivo ausente vira Config.Default (o store grava); pisos fora de ordem num
/// config.json editado à mão são corrigidos por Normalized() e gravados de volta. Arquivo
/// corrompido: usa o padrão em memória, registra em LoadError e NÃO sobrescreve o arquivo
/// (o usuário pode querer consertar o que digitou); só uma mudança feita pelo app grava.
public sealed class ConfigService : IConfigSource
{
    private readonly IStore _store;
    private readonly ILog _log;
    private readonly object _gate = new();
    private Config _current;

    public ConfigService(IStore store, ILog log)
    {
        _store = store;
        _log = log;
        Config loaded;
        try { loaded = store.LoadConfig(); }
        catch (StoreCorruptException ex)
        {
            LoadError = $"config.json ilegível, usando o padrão: {ex.Message}";
            log.Error(LoadError);
            _current = Config.Default;
            return;
        }
        _current = loaded.Normalized();
        if (!ReferenceEquals(_current, loaded))
        {
            var change = $"pisos de bateria corrigidos: lowPowerFloor {loaded.LowPowerFloor} -> {_current.LowPowerFloor}, endFloor {loaded.EndFloor} -> {_current.EndFloor}";
            try
            {
                store.SaveConfig(_current);
                log.Info($"config.json: {change}; gravado");
            }
            catch (StoreWriteException ex)
            {
                log.Error($"config.json: {change}; não deu para gravar a correção: {ex.Message}");
            }
        }
    }

    /// Mensagem de quando o config.json existia mas não pôde ser lido (null se leu bem).
    public string? LoadError { get; private set; }

    public Config Current { get { lock (_gate) return _current; } }

    public event Action? Changed;

    /// Aplica, normaliza pisos, grava e dispara Changed. Falha ao gravar mantém a mudança em
    /// memória (vale para esta execução) e vai para o log.
    public void Update(Func<Config, Config> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Config next;
        lock (_gate)
        {
            next = change(_current).Normalized();
            _current = next;
            try
            {
                _store.SaveConfig(next);
                LoadError = null;
            }
            catch (StoreWriteException ex)
            {
                _log.Error($"config.json: não deu para gravar: {ex.Message}");
            }
        }
        Changed?.Invoke();
    }
}
