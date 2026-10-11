using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Insomnia.Core.Store;

namespace Insomnia.Core.Tests.Fundacao;

using Insomnia.Core.Model;

/// Formatos JSON congelados do §4: ida e volta de cada arquivo, exemplos do desenho e datas.
public class JsonFormatTests
{
    private static readonly Guid Scheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static DateTimeOffset Utc(int h, int m = 0, int s = 0) => new(2026, 10, 11, h, m, s, TimeSpan.Zero);

    // ---------- session.json ----------

    [Fact]
    public void Sessao_ida_e_volta_preserva_tudo()
    {
        var s = new Session(Utc(18), Utc(22), [1800, 900]);
        var back = JsonCodec.ParseSession(JsonCodec.SerializeSession(s));
        Assert.Equal(s.StartedAt, back.StartedAt);
        Assert.Equal(s.EndsAt, back.EndsAt);
        Assert.Equal([1800, 900], back.Extensions.ToArray());
        Assert.Equal(1, back.Version);
    }

    [Fact]
    public void Sessao_grava_o_formato_do_desenho()
    {
        var json = JsonCodec.SerializeSession(new Session(Utc(18), Utc(22), [1800]));
        var node = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(1, (int)node["version"]!);
        Assert.Equal("2026-10-11T18:00:00Z", (string)node["startedAt"]!);
        Assert.Equal("2026-10-11T22:00:00Z", (string)node["endsAt"]!);
        Assert.Equal(1800, (int)node["extensions"]![0]!);
        Assert.Contains("\n", json); // indentado
    }

    [Fact]
    public void Sessao_le_o_exemplo_do_desenho()
    {
        var s = JsonCodec.ParseSession("""{ "version": 1, "startedAt": "2026-10-11T18:00:00Z", "endsAt": "2026-10-11T22:00:00Z", "extensions": [1800] }""");
        Assert.Equal(Utc(22), s.EndsAt);
        Assert.Equal([1800], s.Extensions.ToArray());
    }

    [Theory]
    [InlineData("""{ "version": 1, "startedAt": "2026-10-11T18:00:00Z" }""")]                       // sem endsAt
    [InlineData("""{ "version": 1, "endsAt": "2026-10-11T22:00:00Z" }""")]                           // sem startedAt
    [InlineData("""{ "startedAt": "2026-10-11T18:00:00Z", "endsAt": null }""")]                      // endsAt nulo
    [InlineData("""{ "version": 2, "startedAt": "2026-10-11T18:00:00Z", "endsAt": "2026-10-11T22:00:00Z" }""")] // versão desconhecida
    [InlineData("""{ "startedAt": "2026-10-11T18:00:00Z", "endsAt": "2026-10-11T22:00:00Z", "extensions": ["x"] }""")]
    [InlineData("""{ "startedAt": "2026-10-11T18:00:00Z", "endsAt": "2026-10-11T22:00:00Z" """)]  // truncado
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("")]
    public void Sessao_com_forma_errada_e_ilegivel(string json) =>
        Assert.Throws<FormatException>(() => JsonCodec.ParseSession(json));

    [Fact]
    public void Membros_desconhecidos_sao_ignorados_e_extensions_ausente_vira_vazio()
    {
        var s = JsonCodec.ParseSession("""{ "startedAt": "2026-10-11T18:00:00Z", "endsAt": "2026-10-11T22:00:00Z", "futuro": { "a": 1 } }""");
        Assert.Empty(s.Extensions);
        Assert.Equal(1, s.Version);
    }

    // ---------- state.json ----------

    private static Journal FullJournal() => new(
        1,
        new LidActionEntry(Scheme, 1, 1, Utc(18)),
        new EnergySaverEntry(Scheme, 20, Utc(18, 5), "lid"),
        [
            new FrozenProcess(4312, "slack.exe", new DateTimeOffset(2026, 10, 11, 9, 12, 3, TimeSpan.Zero), "17", "slack.exe"),
            new FrozenProcess(4400, "slack.exe", null, null, "slack.exe"), // provisória
        ],
        true,
        [
            new SavedAudioOutput("{0.0.0.00000000}.{a1b2}", "Alto-falantes", 0.62f, false),
            new SavedAudioOutput("{0.0.0.00000000}.{c3d4}", null, 1f, true),
        ]);

    [Fact]
    public void Diario_ida_e_volta_preserva_tudo()
    {
        var j = FullJournal();
        var back = JsonCodec.ParseJournal(JsonCodec.SerializeJournal(j));
        Assert.Equal(j.Version, back.Version);
        Assert.Equal(j.LidAction, back.LidAction);
        Assert.Equal(j.EnergySaver, back.EnergySaver);
        Assert.Equal(j.FrozenProcesses.ToArray(), back.FrozenProcesses.ToArray());
        Assert.Equal(j.DockerFrozen, back.DockerFrozen);
        Assert.Equal(j.SavedAudioOutputs.ToArray(), back.SavedAudioOutputs.ToArray());
        Assert.False(back.FrozenProcesses[1].HasIdentity);
        Assert.True(back.FrozenProcesses[0].HasIdentity);
    }

    [Fact]
    public void Diario_grava_os_nomes_do_desenho_e_omite_nulos()
    {
        var node = JsonNode.Parse(JsonCodec.SerializeJournal(FullJournal()))!.AsObject();
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", (string)node["lidAction"]!["schemeId"]!);
        Assert.Equal(1u, (uint)node["lidAction"]!["acPrevious"]!);
        Assert.Equal(1u, (uint)node["lidAction"]!["dcPrevious"]!);
        Assert.Equal("2026-10-11T18:00:00Z", (string)node["lidAction"]!["setAt"]!);
        Assert.Equal(20u, (uint)node["energySaver"]!["dcThresholdPrevious"]!);
        Assert.Equal("lid", (string)node["energySaver"]!["cause"]!);
        var provisional = node["frozenProcesses"]![1]!.AsObject();
        Assert.False(provisional.ContainsKey("creationTime"));
        Assert.False(provisional.ContainsKey("bootId"));
        Assert.Equal("2026-10-11T09:12:03Z", (string)node["frozenProcesses"]![0]!["creationTime"]!);
        Assert.True((bool)node["dockerFrozen"]!);
        Assert.Equal(0.62f, (float)node["savedAudioOutputs"]![0]!["volume"]!);
        Assert.False(node["savedAudioOutputs"]![1]!.AsObject().ContainsKey("name"));

        var clean = JsonNode.Parse(JsonCodec.SerializeJournal(Journal.Clean))!.AsObject();
        Assert.False(clean.ContainsKey("lidAction"));
        Assert.False(clean.ContainsKey("energySaver"));
    }

    [Fact]
    public void Diario_le_o_exemplo_do_desenho()
    {
        var j = JsonCodec.ParseJournal("""
            {
              "version": 1,
              "lidAction":   { "schemeId": "381b4222-f694-41f0-9685-ff5bb260df2e", "acPrevious": 1, "dcPrevious": 1, "setAt": "2026-10-11T18:00:00Z" },
              "energySaver": { "schemeId": "381b4222-f694-41f0-9685-ff5bb260df2e", "dcThresholdPrevious": 20, "setAt": "2026-10-11T18:00:00Z", "cause": "lid" },
              "frozenProcesses": [ { "pid": 4312, "name": "slack.exe", "creationTime": "2026-10-11T09:12:03Z", "bootId": "17", "group": "slack.exe" } ],
              "dockerFrozen": false,
              "savedAudioOutputs": [ { "deviceId": "{0.0.0.00000000}.{a1b2}", "name": "Alto-falantes", "volume": 0.62, "muted": false } ]
            }
            """);
        Assert.Equal(new LidActionEntry(Scheme, 1, 1, Utc(18)), j.LidAction);
        Assert.Equal("lid", j.EnergySaver!.Cause);
        Assert.Equal(4312, j.FrozenProcesses.Single().Pid);
        Assert.Equal("17", j.FrozenProcesses.Single().BootId);
        Assert.Equal(0.62f, j.SavedAudioOutputs.Single().Volume);
    }

    [Fact]
    public void Diario_com_campos_ausentes_vale_o_padrao()
    {
        var j = JsonCodec.ParseJournal("{}");
        Assert.Equal(1, j.Version);
        Assert.Null(j.LidAction);
        Assert.Null(j.EnergySaver);
        Assert.Empty(j.FrozenProcesses);
        Assert.False(j.DockerFrozen);
        Assert.Empty(j.SavedAudioOutputs);
        Assert.False(j.IsDirty());
    }

    [Theory]
    [InlineData("""{ "lidAction": { "schemeId": "381b4222-f694-41f0-9685-ff5bb260df2e", "dcPrevious": 1, "setAt": "2026-10-11T18:00:00Z" } }""")] // sem acPrevious
    [InlineData("""{ "lidAction": { "schemeId": "nao-e-guid", "acPrevious": 1, "dcPrevious": 1, "setAt": "2026-10-11T18:00:00Z" } }""")]
    [InlineData("""{ "lidAction": { "schemeId": "381b4222-f694-41f0-9685-ff5bb260df2e", "acPrevious": -1, "dcPrevious": 1, "setAt": "2026-10-11T18:00:00Z" } }""")]
    [InlineData("""{ "energySaver": { "schemeId": "381b4222-f694-41f0-9685-ff5bb260df2e", "dcThresholdPrevious": 20, "setAt": "2026-10-11T18:00:00Z", "cause": "outra" } }""")]
    [InlineData("""{ "frozenProcesses": [ { "pid": "4312", "name": "a.exe", "group": "a.exe" } ] }""")]          // pid como texto
    [InlineData("""{ "frozenProcesses": [ { "pid": 4312, "name": null, "group": "a.exe" } ] }""")]              // nome nulo
    [InlineData("""{ "frozenProcesses": [ null ] }""")]
    [InlineData("""{ "frozenProcesses": [ { "pid": 1, "name": "a.exe", "group": "a.exe", "creationTime": "2026-10-11 09:12:03" } ] }""")]
    [InlineData("""{ "savedAudioOutputs": [ { "deviceId": "x", "volume": 0.5 } ] }""")]                        // sem muted
    [InlineData("""{ "dockerFrozen": "false" }""")]
    [InlineData("""{ "version": 2 }""")]
    [InlineData("""{ "version": 1, "version": 1 }""")]                                                          // chave duplicada
    [InlineData("""{ "version": 1, }""")]                                                                       // vírgula sobrando
    [InlineData("""{ "version": 1 /* comentário */ }""")]
    [InlineData("null")]
    [InlineData("lixo")]
    public void Diario_com_forma_errada_e_corrompido(string json) =>
        Assert.Throws<FormatException>(() => JsonCodec.ParseJournal(json));

    // ---------- config.json ----------

    [Fact]
    public void Config_ida_e_volta_preserva_tudo()
    {
        var c = Config.Default with
        {
            Presets = [600, 7200], DefaultPreset = 600, MaxDuration = 86400, FreezeList = ["Teams.exe"],
            FreezeAllApps = true, DockerRule = true, MuteOnLidClose = false, DarkenDisplayOnLidClose = false,
            EnergySaverOnLidClose = false, AgentList = ["Code.exe"], LowPowerFloor = 50, EndFloor = 15,
            HotspotSsid = "Celular do Caio", LaunchAtLogin = true,
        };
        var back = JsonCodec.ParseConfig(JsonCodec.SerializeConfig(c));
        AssertSameConfig(c, back);
    }

    [Fact]
    public void Config_le_o_exemplo_do_desenho_igual_ao_padrao()
    {
        var c = JsonCodec.ParseConfig("""
            {
              "version": 1,
              "presets": [1800, 3600, 7200, 14400, 28800, 43200, 86400, 259200],
              "defaultPreset": 14400,
              "maxDuration": 2592000,
              "freezeList": ["slack.exe", "WhatsApp.exe", "Discord.exe"],
              "freezeAllApps": false,
              "dockerRule": false,
              "muteOnLidClose": true,
              "darkenDisplayOnLidClose": true,
              "energySaverOnLidClose": true,
              "agentList": ["Code.exe", "Cursor.exe", "WindowsTerminal.exe", "wt.exe", "pwsh.exe", "powershell.exe", "cmd.exe", "claude.exe", "Claude.exe", "ChatGPT.exe", "chrome.exe", "msedge.exe", "brave.exe", "Docker Desktop.exe", "ollama.exe", "ollama app.exe", "wsl.exe", "node.exe", "python.exe", "devenv.exe"],
              "lowPowerFloor": 40,
              "endFloor": 10,
              "hotspotSsid": "",
              "launchAtLogin": false
            }
            """);
        AssertSameConfig(Config.Default, c);
    }

    [Fact]
    public void Config_com_campos_ausentes_completa_com_o_padrao()
    {
        var c = JsonCodec.ParseConfig("""{ "endFloor": 20, "hotspotSsid": "Hotspot" }""");
        Assert.Equal(20, c.EndFloor);
        Assert.Equal("Hotspot", c.HotspotSsid);
        AssertSameConfig(Config.Default with { EndFloor = 20, HotspotSsid = "Hotspot" }, c);
    }

    [Theory]
    [InlineData("""{ "presets": "1800" }""")]
    [InlineData("""{ "freezeList": ["a.exe", null] }""")]
    [InlineData("""{ "endFloor": 10.5 }""")]
    [InlineData("""{ "version": 3 }""")]
    [InlineData("""{ "launchAtLogin": 1 }""")]
    [InlineData("{")]
    public void Config_com_forma_errada_e_corrompido(string json) =>
        Assert.Throws<FormatException>(() => JsonCodec.ParseConfig(json));

    private static void AssertSameConfig(Config expected, Config actual)
    {
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Presets.ToArray(), actual.Presets.ToArray());
        Assert.Equal(expected.DefaultPreset, actual.DefaultPreset);
        Assert.Equal(expected.MaxDuration, actual.MaxDuration);
        Assert.Equal(expected.FreezeList.ToArray(), actual.FreezeList.ToArray());
        Assert.Equal(expected.FreezeAllApps, actual.FreezeAllApps);
        Assert.Equal(expected.DockerRule, actual.DockerRule);
        Assert.Equal(expected.MuteOnLidClose, actual.MuteOnLidClose);
        Assert.Equal(expected.DarkenDisplayOnLidClose, actual.DarkenDisplayOnLidClose);
        Assert.Equal(expected.EnergySaverOnLidClose, actual.EnergySaverOnLidClose);
        Assert.Equal(expected.AgentList.ToArray(), actual.AgentList.ToArray());
        Assert.Equal(expected.LowPowerFloor, actual.LowPowerFloor);
        Assert.Equal(expected.EndFloor, actual.EndFloor);
        Assert.Equal(expected.HotspotSsid, actual.HotspotSsid);
        Assert.Equal(expected.LaunchAtLogin, actual.LaunchAtLogin);
    }

    // ---------- datas ----------

    [Theory]
    [InlineData("2026-10-11T18:00:00Z", 18, 0)]
    [InlineData("2026-10-11T15:00:00-03:00", 18, 0)]
    [InlineData("2026-10-11T20:00:00+02:00", 18, 0)]
    [InlineData("2026-10-11T18:00:00.5Z", 18, 500)]
    [InlineData("2026-10-11T18:00:00.1234567+00:00", 18, 123)]
    public void Datas_aceitas(string text, int utcHour, int millis)
    {
        var d = JsonCodec.TryParseDate(text);
        Assert.NotNull(d);
        Assert.Equal(utcHour, d!.Value.UtcDateTime.Hour);
        Assert.Equal(millis, d.Value.UtcDateTime.Millisecond);
    }

    [Theory]
    [InlineData("2026-10-11T18:00:00")]          // sem fuso
    [InlineData("2026-10-11T18:00:00+0300")]     // fuso sem dois-pontos
    [InlineData("2026-10-11T18:00:00+03")]
    [InlineData("2026-10-11 18:00:00Z")]         // espaço no lugar do T
    [InlineData("2026-10-11")]
    [InlineData("2026-10-11T18:00Z")]            // sem segundos
    [InlineData("2026-13-11T18:00:00Z")]         // mês 13
    [InlineData("2026-10-11T25:00:00Z")]
    [InlineData("2026-10-11T18:00:00z")]         // z minúsculo
    [InlineData("11/10/2026 18:00:00")]
    [InlineData(" 2026-10-11T18:00:00Z")]
    [InlineData("2026-10-11T18:00:00+15:00")]    // fora da faixa de fuso
    [InlineData("")]
    public void Datas_recusadas(string text)
    {
        Assert.Null(JsonCodec.TryParseDate(text));
        var json = $$"""{ "startedAt": "{{text}}", "endsAt": "2026-10-11T22:00:00Z" }""";
        Assert.Throws<FormatException>(() => JsonCodec.ParseSession(json));
    }

    [Fact]
    public void Data_como_numero_e_recusada() =>
        Assert.Throws<FormatException>(() => JsonCodec.ParseSession("""{ "startedAt": 1760205600, "endsAt": "2026-10-11T22:00:00Z" }"""));

    [Fact]
    public void Datas_sao_gravadas_em_UTC_sem_fracao()
    {
        var local = new DateTimeOffset(2026, 10, 11, 15, 0, 0, 999, TimeSpan.FromHours(-3));
        Assert.Equal("2026-10-11T18:00:00Z", JsonCodec.FormatDate(local));
        var json = JsonCodec.SerializeSession(new Session(local, local.AddHours(4), ImmutableArray<int>.Empty));
        Assert.Contains("\"startedAt\": \"2026-10-11T18:00:00Z\"", json);
        Assert.Contains("\"endsAt\": \"2026-10-11T22:00:00Z\"", json);
    }
}
