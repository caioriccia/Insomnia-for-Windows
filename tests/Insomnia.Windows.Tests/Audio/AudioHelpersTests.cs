// Helpers puros de áudio e Docker (R12: nenhum teste chama WASAPI nem o CLI do Docker).
using Insomnia.Core.Abstractions;
using Insomnia.Windows.Audio;
using Insomnia.Windows.Docker;
using NAudio.CoreAudioApi;

namespace Insomnia.Windows.Tests.Audio;

public class AudioHelpersTests
{
    [Theory]
    [InlineData(0.62f, 0.62f)]
    [InlineData(0f, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(-0.1f, 0f)]
    [InlineData(1.5f, 1f)]
    [InlineData(float.NaN, 0f)]
    [InlineData(float.PositiveInfinity, 0f)]
    public void VolumeFicaEntreZeroEUmEInvalidoViraSilencio(float input, float expected) =>
        Assert.Equal(expected, AudioVolume.Clamp(input));

    [Theory]
    [InlineData(DeviceState.Active, true)]
    [InlineData(DeviceState.Unplugged, false)]
    [InlineData(DeviceState.NotPresent, false)]
    [InlineData(DeviceState.Disabled, false)]
    public void SoSaidaAtivaContaComoConectada(DeviceState state, bool connected) =>
        Assert.Equal(connected, AudioVolume.IsConnected(state));
}

public class DockerCliTests
{
    [Theory]
    [InlineData(0, "", false, DockerProbe.Idle)]
    [InlineData(0, "  \r\n", false, DockerProbe.Idle)]
    [InlineData(0, "3f2a1b9c0d11\r\n", false, DockerProbe.Busy)]
    [InlineData(1, "", false, DockerProbe.Unavailable)]
    [InlineData(1, "3f2a1b9c0d11", false, DockerProbe.Unavailable)]
    [InlineData(null, null, true, DockerProbe.Unavailable)]
    [InlineData(0, "", true, DockerProbe.Unavailable)]
    public void InterpretaSaidaDoDockerPs(int? exit, string? stdout, bool timedOut, DockerProbe expected) =>
        Assert.Equal(expected, DockerCli.Interpret(exit, stdout, timedOut));

    [Fact]
    public void EscolheOPipeDoEngineDoDockerDesktop()
    {
        Assert.Equal("npipe:////./pipe/dockerDesktopLinuxEngine",
            DockerCli.ChooseHost([@"\\.\pipe\docker_engine", @"\\.\pipe\dockerDesktopLinuxEngine", @"\\.\pipe\outro"]));
        Assert.Equal("npipe:////./pipe/docker_engine", DockerCli.ChooseHost([@"\\.\pipe\docker_engine"]));
        Assert.Null(DockerCli.ChooseHost([@"\\.\pipe\outro"]));
        Assert.Null(DockerCli.ChooseHost([]));
    }

    [Fact]
    public void ProcuraDockerNoPathEDepoisNaInstalacaoPadrao()
    {
        var paths = DockerCli.CandidatePaths(@"C:\a;""C:\b c"";;", @"C:\Program Files");

        Assert.Equal([@"C:\a\docker.exe", @"C:\b c\docker.exe", @"C:\Program Files\Docker\Docker\resources\bin\docker.exe"], paths);
        Assert.Empty(DockerCli.CandidatePaths(null, null));
    }

    [Fact]
    public void ArgumentosFixamOHostEListamSoIds() =>
        Assert.Equal(["--host", "npipe:////./pipe/docker_engine", "ps", "-q"], DockerCli.Arguments("npipe:////./pipe/docker_engine"));
}
