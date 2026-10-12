// Aplicação WPF do Insomnia (desenho-ui §2). Só o laço e os recursos: quem compõe o app é o
// AppHost, e quem cria os dois é Program.RunUi. O Main gerado pelo App.xaml não é usado
// (StartupObject = Program).
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Insomnia.App;

public partial class App : Application
{
    /// Tokens cujo pincel Fluent não tem cor equivalente (o tom muda com o tema): o App põe o
    /// próprio pincel Fluent por cima da reserva do Tokens.xaml e o troca quando o tema muda.
    public static readonly IReadOnlyList<(string Token, string FluentBrush)> AccentAliases =
    [
        ("Insomnia.Accent", "AccentFillColorDefaultBrush"),
        ("Insomnia.Accent.Text", "AccentTextFillColorPrimaryBrush"),
    ];

    /// Depois de uma troca de tema, o WPF troca o dicionário Fluent ao tratar o WM_SETTINGCHANGE;
    /// ressincroniza de novo depois deste atraso, caso o nosso aviso chegue antes da troca.
    private static readonly TimeSpan ResyncDelay = TimeSpan.FromSeconds(1);

    public App()
    {
        InitializeComponent();
        SyncAccentAliases();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// Aponta cada token de acento para o pincel Fluent do tema atual. Sem o pincel (tema
    /// Fluent ausente), fica a reserva do Tokens.xaml.
    private void SyncAccentAliases()
    {
        foreach (var (token, fluent) in AccentAliases)
        {
            if (TryFindResource(fluent) is System.Windows.Media.Brush brush && !ReferenceEquals(Resources[token], brush))
                Resources[token] = brush;
        }
    }

    /// Pode chegar de outro thread (SystemEvents): tudo acontece no Dispatcher.
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            SyncAccentAliases();
            var later = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = ResyncDelay };
            later.Tick += (_, _) => { later.Stop(); SyncAccentAliases(); };
            later.Start();
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;   // evento estático: não vazar
        base.OnExit(e);
    }
}
