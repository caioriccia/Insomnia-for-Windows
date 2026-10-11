using Insomnia.Windows.Wifi;

namespace Insomnia.Windows.Tests.Wifi;

/// Só o puro do adaptador de Wi-Fi (R12): nenhum teste chama o WLAN.
public class WifiProfilesTests
{
    [Fact]
    public void Resolve_igual_exato_vence()
    {
        Assert.Equal("Caio", WifiProfiles.Resolve(["caio", "Caio", "Casa"], "Caio"));
    }

    [Fact]
    public void Resolve_sem_diferenciar_caixa_devolve_o_nome_do_perfil()
    {
        Assert.Equal("iPhone de Caio", WifiProfiles.Resolve(["Casa", "iPhone de Caio"], "  iphone de caio "));
    }

    [Fact]
    public void Resolve_ambiguo_vazio_ou_ausente_eh_null()
    {
        Assert.Null(WifiProfiles.Resolve(["CAIO", "caio"], "Caio"));
        Assert.Null(WifiProfiles.Resolve(["Casa"], ""));
        Assert.Null(WifiProfiles.Resolve(["Casa"], "   "));
        Assert.Null(WifiProfiles.Resolve([], "Casa"));
        Assert.Null(WifiProfiles.Resolve(["Casa"], "Trabalho"));
    }

    [Fact]
    public void ConnectivityEdge_so_a_mudanca_vira_evento()
    {
        var edge = new ConnectivityEdge(initial: true);
        Assert.Null(edge.Update(true));
        Assert.False(edge.Update(false));
        Assert.Null(edge.Update(false));   // segunda fonte relatando a mesma queda
        Assert.True(edge.Update(true));
        Assert.True(edge.Current);
    }
}
