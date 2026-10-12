// Sinal de "o app está fechando" que o AppHost passa aos comandos longos (iniciar, estender,
// reconciliar, relançar navegador). Existe porque um CancellationTokenSource cru lança
// ObjectDisposedException em duas situações que o fechamento do app torna prováveis: ler .Token
// depois do Dispose (a lambda de um Task.Run que só roda depois que o app já fechou) e chamar
// Cancel depois do Dispose. Aqui as duas são impossíveis: o token é copiado uma vez no
// construtor (CancellationToken é só um valor: continua utilizável depois de o dono ser
// descartado) e Cancel/Dispose são idempotentes e nunca lançam. Sem WPF, sem sistema: testável.
namespace Insomnia.App.Composition;

public sealed class ShutdownSignal : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly object _gate = new();
    private bool _disposed;

    public ShutdownSignal() => Token = _source.Token;

    /// Sempre seguro de ler, antes ou depois do Dispose. Depois do Cancel, já vem cancelado.
    public CancellationToken Token { get; }

    public bool IsCancelled => Token.IsCancellationRequested;

    /// Cancela uma vez; chamadas seguintes (inclusive depois do Dispose) não fazem nada.
    /// Uma falha num callback registrado no token não impede o fechamento: não sobe.
    public void Cancel()
    {
        lock (_gate)
        {
            if (_disposed || _source.IsCancellationRequested) return;
            try { _source.Cancel(); }
            catch (Exception ex) when (ex is AggregateException or ObjectDisposedException) { }
        }
    }

    /// Cancela (se ainda não foi) e libera, no máximo uma vez.
    public void Dispose()
    {
        Cancel();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _source.Dispose();
        }
    }
}
