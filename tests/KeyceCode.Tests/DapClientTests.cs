using System.Diagnostics;
using KeyceCode.Services.Dap;
using Xunit;

namespace KeyceCode.Tests;

public sealed class DapClientTests
{
    [Fact]
    public async Task ClientCorrelatesRequestsReceivesEventsAndDisconnectsCleanly()
    {
        var client = new DapClient();
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.EventReceived += (_, args) => { if (args.EventName == "initialized") initialized.TrySetResult(); };
        await client.StartAsync(new DapAdapterOptions { ExecutablePath = FindDapEcho(), RequestTimeout = TimeSpan.FromSeconds(5) });
        try
        {
            var capabilities = await client.InitializeAsync("fake");
            Assert.True(capabilities.GetProperty("supportsConfigurationDoneRequest").GetBoolean());
            var launched = await client.LaunchAsync(new { program = "sample" });
            Assert.Equal(4242, launched.GetProperty("processId").GetInt32());
            await initialized.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await client.ConfigurationDoneAsync();
            await client.DisconnectAsync();
            Assert.False(client.IsRunning);
        }
        finally { await client.DisposeAsync(); }
    }

    private static string FindDapEcho()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "KeyceCode.Tests", "DapEcho", "bin", "Release", "net10.0-windows", "DapEcho.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Build output for the DAP echo adapter was not found.");
    }
}
