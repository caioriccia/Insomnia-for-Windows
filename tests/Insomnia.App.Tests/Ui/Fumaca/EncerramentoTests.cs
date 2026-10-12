using Insomnia.App.Composition;

namespace Insomnia.App.Tests.Ui.Fumaca;

/// A saída do app (Sair) não pode lançar: ShutdownSignal troca o CancellationTokenSource cru do
/// AppHost, que lançava ObjectDisposedException ao ler .Token ou chamar Cancel depois do Dispose.
public class EncerramentoTests
{
    [Fact]
    public void Token_continua_legivel_depois_do_Dispose_e_vem_cancelado()
    {
        var signal = new ShutdownSignal();
        var antes = signal.Token;
        Assert.False(antes.IsCancellationRequested);

        signal.Dispose();

        Assert.True(signal.Token.IsCancellationRequested);   // era ObjectDisposedException
        Assert.True(signal.IsCancelled);
        Assert.True(antes.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_depois_do_Dispose_e_Dispose_duplo_nao_lancam()
    {
        var signal = new ShutdownSignal();
        signal.Dispose();
        signal.Cancel();    // era ObjectDisposedException
        signal.Dispose();
        signal.Cancel();
        Assert.True(signal.IsCancelled);
    }

    [Fact]
    public void Cancel_repetido_avisa_quem_espera_uma_vez_so()
    {
        var signal = new ShutdownSignal();
        var avisos = 0;
        using var _ = signal.Token.Register(() => avisos++);

        signal.Cancel();
        signal.Cancel();
        signal.Dispose();

        Assert.Equal(1, avisos);
    }

    [Fact]
    public void Callback_que_falha_nao_impede_o_fechamento()
    {
        var signal = new ShutdownSignal();
        using var _ = signal.Token.Register(() => throw new InvalidOperationException("callback ruim"));

        signal.Cancel();          // o AggregateException do callback fica de fora
        signal.Dispose();

        Assert.True(signal.IsCancelled);
    }

    [Fact]
    public async Task Task_que_so_le_o_token_depois_do_fechamento_nao_estoura()
    {
        // O caso do AppHost: Task.Run(() => Manager.StartAsync(d, _shutdown.Token)) cuja lambda só roda
        // depois que o app já fechou.
        var signal = new ShutdownSignal();
        var liberar = new ManualResetEventSlim();
        var tarefa = Task.Run(() =>
        {
            liberar.Wait();
            return signal.Token.IsCancellationRequested;
        });

        signal.Dispose();
        liberar.Set();

        Assert.True(await tarefa);
    }

    [Fact]
    public void Falha_fatal_vai_para_o_log_com_o_stack_trace()
    {
        Exception capturada;
        try { ThrowDeep(); throw new InvalidOperationException("inalcançável"); }
        catch (Exception ex) { capturada = ex; }

        var linha = Program.FatalMessage(capturada);

        Assert.StartsWith("falha fatal (InvalidOperationException): 'X' não é válido.", linha);
        Assert.Contains(nameof(ThrowDeep), linha);          // o stack trace, não só a mensagem
        Assert.Contains("EncerramentoTests", linha);
    }

    [Fact]
    public void Falha_fatal_inclui_a_excecao_interna()
    {
        var ex = new InvalidOperationException("externa", new ArgumentException("interna-causa"));
        Assert.Contains("interna-causa", Program.FatalMessage(ex));
        Assert.Contains("interna-causa", Program.UiErrorMessage(ex));
        Assert.StartsWith("erro na UI: InvalidOperationException: externa", Program.UiErrorMessage(ex));
    }

    private static void ThrowDeep() => throw new InvalidOperationException("'X' não é válido.");
}
