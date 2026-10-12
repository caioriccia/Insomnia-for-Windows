using Insomnia.App.Ui;
using Insomnia.App.Ui.Model;
using Insomnia.App.Ui.Shell;
using Insomnia.App.Ui.Theme;

namespace Insomnia.App.Tests.Ui.Shell;

/// Contas puras da casca (desenho-ui §3.4, §3.5, §3.10, §4): clip do painel por progresso e por
/// arrasto, curvas, foco inicial, auto-recolher, alça, toggle e cores por tom. Sem WPF.
public class ShellMathTests
{
    private const double H = 420;

    // ------------------------------------------------------------------ clip por progresso

    [Fact]
    public void Clip_no_zero_e_a_pilula()
    {
        Assert.Equal(new ShellClip(148, 32, 16), ShellMath.ClipAt(0, H));
    }

    [Fact]
    public void Clip_no_um_e_o_painel()
    {
        Assert.Equal(new ShellClip(360, H, 8), ShellMath.ClipAt(1, H));
    }

    [Fact]
    public void Clip_no_meio_interpola_linear()
    {
        var clip = ShellMath.ClipAt(0.5, H);
        Assert.Equal(254, clip.Width, 6);
        Assert.Equal(226, clip.Height, 6);
        Assert.Equal(12, clip.Radius, 6);
    }

    [Theory]
    [InlineData(-0.5, 0)]
    [InlineData(1.7, 1)]
    [InlineData(double.NaN, 0)]
    public void Clip_prende_o_progresso_em_0_a_1(double progress, double equivalent)
    {
        Assert.Equal(ShellMath.ClipAt(equivalent, H), ShellMath.ClipAt(progress, H));
    }

    [Fact]
    public void Clip_parte_da_largura_atual_da_pilula()
    {
        Assert.Equal(180, ShellMath.ClipAt(0, H, startWidth: 180).Width);
        Assert.Equal(360, ShellMath.ClipAt(1, H, startWidth: 180).Width);
        // Nunca menor que a pílula mínima.
        Assert.Equal(148, ShellMath.ClipAt(0, H, startWidth: 90).Width);
    }

    [Fact]
    public void Clip_com_painel_mais_baixo_que_a_pilula_nao_encolhe()
    {
        Assert.Equal(32, ShellMath.ClipAt(1, 10).Height);
    }

    [Fact]
    public void Clip_cresce_monotonico_com_o_progresso()
    {
        var previous = ShellMath.ClipAt(0, H);
        for (var p = 0.05; p <= 1.0001; p += 0.05)
        {
            var clip = ShellMath.ClipAt(p, H);
            Assert.True(clip.Width >= previous.Width);
            Assert.True(clip.Height >= previous.Height);
            Assert.True(clip.Radius <= previous.Radius);
            previous = clip;
        }
    }

    // ------------------------------------------------------------------ clip pelo arrasto

    [Fact]
    public void Arrasto_altura_e_32_mais_o_offset()
    {
        Assert.Equal(32, ShellMath.ClipForPull(0, H).Height, 6);
        Assert.Equal(32 + 48, ShellMath.ClipForPull(48, H).Height, 6);
        Assert.Equal(32 + 100, ShellMath.ClipForPull(100, H).Height, 6);
    }

    [Fact]
    public void Arrasto_usa_o_offset_ja_com_resistencia_do_gesto()
    {
        // 148 epx brutos ⇒ 48 + 100 × 0,35 = 83 de offset ⇒ altura 115.
        var offset = PullDownGesture.Resist(148);
        Assert.Equal(83, offset, 6);
        Assert.Equal(115, ShellMath.ClipForPull(offset, H).Height, 6);
    }

    [Fact]
    public void Arrasto_alem_do_painel_para_no_painel()
    {
        Assert.Equal(1, ShellMath.PullProgress(10_000, H));
        Assert.Equal(new ShellClip(360, H, 8), ShellMath.ClipForPull(10_000, H));
        Assert.Equal(0, ShellMath.PullProgress(-20, H));
    }

    [Fact]
    public void Arrasto_largura_e_raio_acompanham_a_altura()
    {
        var offset = (H - 32) / 4;   // um quarto do caminho
        var clip = ShellMath.ClipForPull(offset, H);
        Assert.Equal(148 + (360 - 148) * 0.25, clip.Width, 6);
        Assert.Equal(16 - 8 * 0.25, clip.Radius, 6);
    }

    [Fact]
    public void Arrasto_conteudo_aparece_na_segunda_metade()
    {
        Assert.Equal(0, ShellMath.PullContentOpacity(0));
        Assert.Equal(0, ShellMath.PullContentOpacity(0.5));
        Assert.Equal(0.5, ShellMath.PullContentOpacity(0.75), 6);
        Assert.Equal(1, ShellMath.PullContentOpacity(1));
    }

    [Fact]
    public void Soltar_completa_ou_volta_em_no_maximo_200_ms()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(200), ShellMath.SettleDuration(0, 1, Motion.Normal));
        Assert.Equal(TimeSpan.FromMilliseconds(167), ShellMath.SettleDuration(0.5, 1, Motion.Normal));   // 0,5 × 333
        Assert.Equal(TimeSpan.FromMilliseconds(60), ShellMath.SettleDuration(0.3, 0, ShellMath.CloseDuration));
        Assert.Equal(TimeSpan.Zero, ShellMath.SettleDuration(1, 1, Motion.Normal));
    }

    // ------------------------------------------------------------------ opacidade e curvas

    [Fact]
    public void Conteudo_aparece_nos_ultimos_167_ms_dos_333()
    {
        Assert.Equal(0, ShellMath.FadeInTail(0, Motion.Normal, Motion.Short));
        Assert.Equal(0, ShellMath.FadeInTail(166.0 / 333, Motion.Normal, Motion.Short), 6);
        Assert.Equal(0.5, ShellMath.FadeInTail((166 + 83.5) / 333, Motion.Normal, Motion.Short), 6);
        Assert.Equal(1, ShellMath.FadeInTail(1, Motion.Normal, Motion.Short));
    }

    [Fact]
    public void Sem_animacao_o_conteudo_ja_esta_visivel_ou_ja_sumiu()
    {
        Assert.Equal(1, ShellMath.FadeInTail(1, TimeSpan.Zero, Motion.Short));
        Assert.Equal(0, ShellMath.FadeOutHead(1, TimeSpan.Zero, Motion.Fast));
    }

    [Fact]
    public void Ao_fechar_o_conteudo_some_nos_primeiros_83_ms()
    {
        var total = ShellMath.CloseDuration;
        Assert.Equal(1, ShellMath.FadeOutHead(0, total, Motion.Fast));
        Assert.Equal(0.5, ShellMath.FadeOutHead(41.5 / 200, total, Motion.Fast), 6);
        Assert.Equal(0, ShellMath.FadeOutHead(0.5, total, Motion.Fast));
    }

    [Fact]
    public void Curvas_vao_de_0_a_1_e_sao_monotonicas()
    {
        foreach (var curve in new[] { Motion.Decelerate, Motion.Accelerate, Motion.Standard })
        {
            Assert.Equal(0, ShellMath.Ease(curve, 0));
            Assert.Equal(1, ShellMath.Ease(curve, 1));
            var previous = 0.0;
            for (var t = 0.01; t < 1; t += 0.01)
            {
                var y = ShellMath.Ease(curve, t);
                Assert.True(y >= previous - 1e-9, $"{curve} recuou em t={t}");
                previous = y;
            }
        }
    }

    [Fact]
    public void Decelerate_entra_rapido_e_Accelerate_sai_devagar()
    {
        Assert.True(ShellMath.Ease(Motion.Decelerate, 0.25) > 0.6);
        Assert.True(ShellMath.Ease(Motion.Accelerate, 0.25) < 0.1);
    }

    [Fact]
    public void Curva_linear_devolve_o_proprio_tempo()
    {
        var linear = (1.0 / 3, 1.0 / 3, 2.0 / 3, 2.0 / 3);
        foreach (var t in new[] { 0.1, 0.37, 0.5, 0.9 })
            Assert.Equal(t, ShellMath.Ease(linear, t), 5);
    }

    // ------------------------------------------------------------------ foco, auto-recolher, toggle, alça

    [Fact]
    public void Foco_inicial_por_modo()
    {
        Assert.Equal(PanelFocus.DurationField, ShellMath.FocusFor(PanelMode.Start));
        Assert.Equal(PanelFocus.FirstExtendChip, ShellMath.FocusFor(PanelMode.Session));
    }

    [Fact]
    public void Auto_recolher_so_depois_de_inicio_bem_sucedido()
    {
        Assert.True(ShellMath.ArmsAutoCollapse(null));
        Assert.False(ShellMath.ArmsAutoCollapse("Já existe uma sessão ativa."));
    }

    [Theory]
    [InlineData(null, false, true)]                 // encerrou de fato: "Sessão encerrada" e recolhe
    [InlineData(null, true, false)]                 // EndAsync Locked/JournalUnreadable: sessão de pé ⇒ rearma
    [InlineData("Falhou: diário", false, false)]    // exceção ⇒ rearma
    [InlineData("Falhou: diário", true, false)]
    public void Segurar_para_encerrar_so_conclui_se_a_sessao_sumiu(string? problem, bool hasSessionAfter, bool succeeded)
    {
        Assert.Equal(succeeded, ShellMath.EndSucceeded(problem, hasSessionAfter));
    }

    [Fact]
    public void Auto_recolher_espera_1_5_s()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1500), ShellMath.AutoCollapseDelay);
        Assert.False(ShellMath.ShouldAutoCollapse(true, TimeSpan.FromMilliseconds(1499), false, true, true));
        Assert.True(ShellMath.ShouldAutoCollapse(true, TimeSpan.FromMilliseconds(1500), false, true, true));
    }

    [Theory]
    [InlineData(false, false, true, true)]    // não armado
    [InlineData(true, true, true, true)]      // a pessoa mexeu no painel
    [InlineData(true, false, false, true)]    // a sessão já acabou
    [InlineData(true, false, true, false)]    // o painel já fechou
    public void Auto_recolher_nao_acontece(bool armed, bool interacted, bool hasSession, bool panelOpen)
    {
        Assert.False(ShellMath.ShouldAutoCollapse(armed, TimeSpan.FromSeconds(5), interacted, hasSession, panelOpen));
    }

    [Fact]
    public void Toggle_abre_fechado_e_nao_reabre_logo_apos_perder_o_foco()
    {
        Assert.True(ShellMath.ToggleOpens(panelOpen: false, msSinceDismissedByFocus: 10_000));
        Assert.False(ShellMath.ToggleOpens(panelOpen: true, msSinceDismissedByFocus: 10_000));
        Assert.False(ShellMath.ToggleOpens(panelOpen: false, msSinceDismissedByFocus: 120));
        Assert.True(ShellMath.ToggleOpens(panelOpen: false, msSinceDismissedByFocus: ShellMath.ReopenGuardMs));
    }

    [Theory]
    [InlineData(48, 48, true)]     // arrastou 48 para cima
    [InlineData(80, 80, true)]
    [InlineData(0, 3, true)]       // clique (mexeu até 4)
    [InlineData(4, 4, true)]
    [InlineData(30, 30, false)]    // arrasto curto
    [InlineData(-60, 60, false)]   // arrastou para baixo
    [InlineData(0, 20, false)]     // foi e voltou
    public void Alca_fecha_com_arrasto_para_cima_ou_clique(double upward, double travel, bool closes)
    {
        Assert.Equal(closes, ShellMath.HandleCloses(upward, travel));
    }

    // ------------------------------------------------------------------ pílula

    [Theory]
    [InlineData(0, 148)]
    [InlineData(100, 148)]      // 100 + 28 < 148
    [InlineData(120, 148)]
    [InlineData(130.2, 159)]    // 130,2 + 28 = 158,2 ⇒ 159
    public void Largura_da_pilula_tem_minimo_148_e_padding_14(double content, double expected)
    {
        Assert.Equal(expected, ShellMath.PillWidth(content));
    }

    [Fact]
    public void Cores_da_pilula_por_tom()
    {
        Assert.Equal(("Insomnia.Text.Primary", "Insomnia.Text.Primary"), ShellMath.PillBrushes(PillTone.Normal));
        Assert.Equal(("Insomnia.Text.Secondary", "Insomnia.Text.Secondary"), ShellMath.PillBrushes(PillTone.Paused));
        Assert.Equal(("Insomnia.Caution", "Insomnia.Text.Primary"), ShellMath.PillBrushes(PillTone.Warning));
        Assert.Equal(("Insomnia.Caution", "Insomnia.Caution"), ShellMath.PillBrushes(PillTone.EndingSoon));
        Assert.Equal(("Insomnia.Critical", "Insomnia.Critical"), ShellMath.PillBrushes(PillTone.Critical));
    }

    [Fact]
    public void Ponto_e_sublinha_por_tom()
    {
        Assert.Equal("Insomnia.Accent", ShellMath.DotBrush(PillTone.Normal));
        Assert.Equal("Insomnia.Accent", ShellMath.DotBrush(PillTone.Paused));
        Assert.Equal("Insomnia.Caution", ShellMath.DotBrush(PillTone.Warning));
        Assert.Equal("Insomnia.Caution", ShellMath.DotBrush(PillTone.EndingSoon));
        Assert.Equal("Insomnia.Critical", ShellMath.DotBrush(PillTone.Critical));

        Assert.Equal("Insomnia.Text.Secondary", ShellMath.SublineBrush(PillTone.Paused));
        Assert.Equal("Insomnia.Caution", ShellMath.SublineBrush(PillTone.EndingSoon));
        Assert.Equal("Insomnia.Critical", ShellMath.SublineBrush(PillTone.Critical));
    }

    [Fact]
    public void Chaves_de_cor_existem_no_Tokens()
    {
        var tokens = File.ReadAllText(TokensPath());
        var keys = new[] { PillTone.Normal, PillTone.Paused, PillTone.Warning, PillTone.EndingSoon, PillTone.Critical }
            .SelectMany(t => new[] { ShellMath.PillBrushes(t).Glyph, ShellMath.PillBrushes(t).Text, ShellMath.DotBrush(t), ShellMath.SublineBrush(t) })
            .Distinct();
        foreach (var key in keys)
            Assert.Contains($"x:Key=\"{key}\"", tokens);
    }

    // A saída de build fica fora do repositório (ArtifactsPath): o caminho sai do fonte deste teste.
    private static string TokensPath([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "..", "src", "Insomnia.App", "Ui", "Theme", "Tokens.xaml"));
}
