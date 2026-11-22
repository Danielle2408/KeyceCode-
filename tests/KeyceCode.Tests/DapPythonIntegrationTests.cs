using KeyceCode.Services.Dap;
using System.Collections.Concurrent;
using Xunit;

namespace KeyceCode.Tests;

public sealed class DapPythonIntegrationTests
{
    [Fact]
    public async Task InstalledDebugpy_StopsAtBreakpointContinuesAndTerminates()
    {
        var python = VsCodeLaunchConfigurationService.ResolveAdapter("debugpy");
        if (!python.IsAvailable || python.ExecutablePath is null) return; // Optional local integration dependency.
        var workspace = Path.Combine(Path.GetTempPath(), "KeyceCode-debugpy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var script = Path.Combine(workspace, "main.py");
        await File.WriteAllTextAsync(script, "value = 1\nvalue += 1\nprint('DAP_INTEGRATION_OK', value)\n");

        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new DapClient();
        var logs = new ConcurrentQueue<string>();
        client.OutputReceived += (_, line) => logs.Enqueue(line);
        client.TransportError += (_, error) => logs.Enqueue("TRANSPORT: " + error.Message);
        client.ProtocolStatusReceived += (_, message) => logs.Enqueue(message);
        client.EventReceived += (_, message) =>
        {
            if (message.EventName == "initialized") initialized.TrySetResult();
            if (message.EventName == "stopped" && message.Body.TryGetProperty("threadId", out var thread)) stopped.TrySetResult(thread.GetInt32());
            if (message.EventName == "terminated") terminated.TrySetResult();
        };
        try
        {
            await client.StartAsync(new DapAdapterOptions
            {
                ExecutablePath = python.ExecutablePath,
                Arguments = python.Arguments ?? [],
                Environment = python.Environment ?? new Dictionary<string, string?>(),
                WorkingDirectory = workspace,
                RequestTimeout = TimeSpan.FromSeconds(20)
            });
            await client.InitializeAsync("debugpy", "en-US");
            var launchRequest = client.LaunchAsync(new { name = "KeyceCode DAP integration", type = "debugpy", request = "launch", program = script, cwd = workspace, console = "internalConsole", justMyCode = true });
            await initialized.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var breakpoints = await client.RequestAsync("setBreakpoints", new
            {
                source = new { name = "main.py", path = script },
                breakpoints = new[] { new { line = 2 } },
                sourceModified = false
            });
            Assert.True(breakpoints.GetProperty("breakpoints")[0].GetProperty("verified").GetBoolean());
            await client.ConfigurationDoneAsync();
            await launchRequest.WaitAsync(TimeSpan.FromSeconds(20));
            var threadId = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var stack = await client.RequestAsync("stackTrace", new { threadId, startFrame = 0, levels = 10 });
            Assert.Equal(2, stack.GetProperty("stackFrames")[0].GetProperty("line").GetInt32());
            await client.RequestAsync("continue", new { threadId, singleThread = false });
            await terminated.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await client.DisconnectAsync(terminateDebuggee: true);
        }
        catch (Exception ex) { throw new Xunit.Sdk.XunitException(ex + Environment.NewLine + string.Join(Environment.NewLine, logs)); }
        finally
        {
            await client.DisposeAsync();
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }
}
