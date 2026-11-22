using KeyceCode.Services;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Xunit;

namespace KeyceCode.Tests;

public sealed class ConPtySessionTests
{
    [Fact]
    public async Task InteractiveConsoleReadsInputReturnsOutputExitCodeAndCanResize()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        var echoProgram = FindEchoProgram();
        var session = await ConPtySession.StartAsync(echoProgram, workingDirectory: Path.GetTempPath(), columns: 100, rows: 30);
        var output = new ConcurrentQueue<string>();
        try
        {
            var ready = await ReadUntilAsync(session, "KEYCE_ECHO_READY>");
            output.Enqueue(ready);
            session.Resize(112, 36);
            await session.SendInputAsync("hello through ConPTY\r");
            output.Enqueue(await ReadUntilAsync(session, "KEYCE_ECHO:hello through ConPTY"));
            await session.SendInputAsync("exit\r");
            output.Enqueue(await ReadUntilAsync(session, "KEYCE_ECHO_EXIT_OK"));
            var exitCode = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(23, exitCode);
            Assert.Contains("KEYCE_ECHO:hello through ConPTY", string.Concat(output));
        }
        finally { await session.DisposeAsync(); }
    }

    [Fact]
    public async Task DisposingAnInteractiveShellDoesNotLeaveTheProcessRunning()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        var session = await ConPtySession.StartAsync(FindEchoProgram(), workingDirectory: Path.GetTempPath());
        var processId = session.ProcessId;
        await session.DisposeAsync();

        try
        {
            using var process = Process.GetProcessById(processId);
            Assert.True(process.HasExited, $"ConPTY child process {processId} remained alive after disposal.");
        }
        catch (ArgumentException)
        {
            // Process has already been reaped by Windows.
        }
    }

    [Fact]
    public async Task InteractiveCmdAcceptsACommandAndReturnsItsExitCode()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        var shell = Environment.GetEnvironmentVariable("COMSPEC") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var session = await ConPtySession.StartAsync(shell, ["/D", "/Q", "/K"], Environment.CurrentDirectory);
        try
        {
            await ReadUntilAsync(session, ">");
            await session.SendInputAsync("echo KEYCE_CONPTY_CMD_OK\r");
            var commandOutput = await ReadUntilAsync(session, "KEYCE_CONPTY_CMD_OK");
            Assert.Contains("KEYCE_CONPTY_CMD_OK", commandOutput);
            await session.SendInputAsync("exit /b 19\r");
            Assert.Equal(19, await session.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { await session.DisposeAsync(); }
    }

    private static string FindEchoProgram()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "KeyceCode.Tests", "ConPtyEcho", "bin", "Release", "net10.0-windows", "ConPtyEcho.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Build output for the ConPTY echo helper was not found.");
    }

    private static async Task<string> ReadUntilAsync(ConPtySession session, string marker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var text = new StringBuilder();
        while (await session.Output.WaitToReadAsync(timeout.Token))
        {
            while (session.Output.TryRead(out var chunk))
            {
                text.Append(chunk);
                if (text.ToString().Contains(marker, StringComparison.Ordinal)) return text.ToString();
            }
        }
        var status = session.Completion.IsCompleted ? $" Exit code: {await session.Completion}." : " Process still active.";
        throw new Xunit.Sdk.XunitException($"ConPTY output ended before marker '{marker}'. Received: {text}.{status}");
    }
}
