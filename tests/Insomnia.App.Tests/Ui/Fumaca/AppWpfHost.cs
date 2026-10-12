using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace Insomnia.App.Tests.Ui.Fumaca;

/// Tudo que cria objetos WPF reais compartilha esta coleção: o WPF só admite UMA Application por
/// processo e todo objeto de UI precisa viver no thread dela, então os testes rodam um de cada vez.
[CollectionDefinition(Nome)]
public sealed class WpfCollection
{
    public const string Nome = "Wpf";
}

/// Sobe o App REAL (App.xaml: ThemeMode=System + Tokens.xaml + Controls.xaml, mais o
/// SyncAccentAliases do construtor) UMA vez, num thread STA com Dispatcher próprio, sem Run e
/// sem mostrar janela nenhuma. Cada teste manda sua ação para esse thread. É o mais perto do
/// início do app que um teste consegue chegar sem tocar o sistema (sem Mutex, bandeja, energia
/// nem arquivo). A Application não pode ser recriada no mesmo processo (o WPF guarda uma flag
/// estática que nunca volta), por isso o thread vive até o fim da execução dos testes.
/// Se o App não sobe, a exceção fica guardada e TODO teste que o usa a recebe.
internal static class AppWpfHost
{
    private static readonly Lazy<(Dispatcher? Dispatcher, ExceptionDispatchInfo? Failure)> Started = new(Start);

    public static void Run(Action<Application> action)
    {
        var (dispatcher, failure) = Started.Value;
        failure?.Throw();
        // Invoke devolve ao chamador a exceção lançada pela ação, com o stack original.
        dispatcher!.Invoke(() => action(Application.Current));
    }

    private static (Dispatcher?, ExceptionDispatchInfo?) Start()
    {
        Dispatcher? dispatcher = null;
        ExceptionDispatchInfo? failure = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                _ = new global::Insomnia.App.App();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            if (failure is null) Dispatcher.Run();
        })
        { IsBackground = true, Name = "Insomnia.App.Tests WPF" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("o App não subiu em 60 s");
        return (dispatcher, failure);
    }
}
