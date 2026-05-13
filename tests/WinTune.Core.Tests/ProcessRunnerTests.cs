using FluentAssertions;
using WinTune.Core.Services;

namespace WinTune.Core.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_executes_real_process_and_captures_stdout()
    {
        IProcessRunner sut = new ProcessRunner();

        var r = await sut.RunAsync("cmd.exe", new[] { "/c", "echo hello" }, CancellationToken.None);

        r.ExitCode.Should().Be(0);
        r.Stdout.Trim().Should().Be("hello");
        r.Stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_captures_non_zero_exit_code()
    {
        IProcessRunner sut = new ProcessRunner();

        var r = await sut.RunAsync("cmd.exe", new[] { "/c", "exit 7" }, CancellationToken.None);

        r.ExitCode.Should().Be(7);
    }
}
