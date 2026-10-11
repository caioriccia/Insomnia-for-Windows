using Insomnia.App.Commands;
using Insomnia.Core.Abstractions;
using Insomnia.Core.Model;
using Insomnia.Core.Tests.Fakes;

namespace Insomnia.App.Tests.Bandeja;

/// Composição do --backstop (R9): o LidUndo que ele monta nunca acorda a tela. Run não é
/// chamado (montaria os adaptadores reais); só a fábrica, com fakes.
public class BackstopCommandTests
{
    [Fact]
    public async Task R9_o_LidUndo_do_backstop_nao_acorda_a_tela_mesmo_com_residuo_no_diario()
    {
        var world = new FakeProcessWorld();
        var audio = new FakeAudio();
        var display = new FakeDisplay();
        var journal = Journal.Clean with
        {
            SavedAudioOutputs = [new SavedAudioOutput("{0.0.0.00000000}.{fone-bluetooth}", "Fone", 0.4f, false)],
        };
        var store = new FakeStore { JournalOnDisk = journal };

        var lidUndo = BackstopCommand.CreateLidUndo(world, world, audio, display, new FakeLog());
        await lidUndo.UndoAsync(new JournalTransaction(store, journal), default);

        Assert.False(lidUndo.WakesDisplay);
        Assert.Equal(0, display.WakeCount);
    }
}
