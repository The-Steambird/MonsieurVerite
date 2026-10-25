using System.IO;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite.Tests;

public abstract class ViewModelTest : IDisposable
{
    private protected ScratchFolder Scratch { get; } = new();

    public void Dispose()
    {
        Scratch.Dispose();
        GC.SuppressFinalize(this);
    }

    // Pointing the engine path into the scratch folder puts recovered_keys.json there too.
    protected MainViewModel NewViewModel(string? engine) =>
        new(engine, new Settings { EnginePath = Scratch.File("charlotte-cli.exe") });

    protected MainViewModel NewViewModel() =>
        NewViewModel(Scratch.File("charlotte-cli.exe"));

    protected QueueItem Add(MainViewModel viewModel, string name)
    {
        var item = new QueueItem(Scratch.File(Path.Combine("usm", name)));
        viewModel.Items.Add(item);
        return item;
    }

    // Every message is declined, because accepting a restart would relaunch the test host.
    protected static List<Message> DeclineMessages(MainViewModel viewModel)
    {
        var shown = new List<Message>();
        viewModel.ShowMessage = message =>
        {
            shown.Add(message);
            return false;
        };
        return shown;
    }

    protected string FakeEngine(params string[] lines)
    {
        var script = Scratch.File(Path.GetRandomFileName() + ".cmd");
        File.WriteAllLines(script, lines);
        return script;
    }
}
