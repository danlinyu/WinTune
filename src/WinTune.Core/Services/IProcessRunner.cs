namespace WinTune.Core.Services;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken ct);
}
