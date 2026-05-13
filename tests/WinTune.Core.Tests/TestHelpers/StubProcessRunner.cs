using WinTune.Core.Services;

namespace WinTune.Core.Tests.TestHelpers;

public sealed class StubProcessRunner : IProcessRunner
{
    private readonly Dictionary<string, ProcessResult>     _scripted = new();
    public List<(string FileName, string[] Args)>          Calls     { get; } = new();

    /// <summary>
    /// Register a scripted response. <paramref name="argsContain"/> is the substring of
    /// the joined argument list to match — first match wins. Use the setting GUID
    /// when scripting per-setting powercfg /query responses.
    /// </summary>
    public void When(string argsContain, ProcessResult result)
        => _scripted[argsContain] = result;

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        var argString = string.Join(" ", arguments);
        Calls.Add((fileName, arguments.ToArray()));

        foreach (var (needle, response) in _scripted)
            if (argString.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(response);

        return Task.FromResult(new ProcessResult(0, "", ""));
    }
}
