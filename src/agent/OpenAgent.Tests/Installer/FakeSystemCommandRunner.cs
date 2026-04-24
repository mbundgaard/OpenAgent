using OpenAgent.Installer;

namespace OpenAgent.Tests.Installer;

public sealed class FakeSystemCommandRunner : ISystemCommandRunner
{
    public List<(string Executable, string Arguments)> Calls { get; } = new();
    public Queue<CommandResult> Responses { get; } = new();

    public CommandResult Run(string executable, string arguments, TimeSpan? timeout = null)
    {
        Calls.Add((executable, arguments));
        return Responses.Count > 0
            ? Responses.Dequeue()
            : new CommandResult(0, "");
    }
}
