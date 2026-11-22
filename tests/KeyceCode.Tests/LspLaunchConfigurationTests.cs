using KeyceCode.Services.Lsp;
using System.Text.Json;
using Xunit;

namespace KeyceCode.Tests;

public sealed class LspLaunchConfigurationTests
{
    [Fact]
    public void Resolve_PrefersSavedSettingsAndParsesArgumentsWithoutShellSplitting()
    {
        var result = LspLaunchConfiguration.Resolve(" C:\\tools\\server.exe ", "[\"--stdio\",\"a b\"]", "env-server", "[\"--env\"]");

        Assert.Equal("C:\\tools\\server.exe", result.ExecutablePath);
        Assert.Equal(new[] { "--stdio", "a b" }, result.Arguments);
    }

    [Fact]
    public void Resolve_UsesEnvironmentFallbackWhenSavedValuesAreEmpty()
    {
        var result = LspLaunchConfiguration.Resolve(null, null, "dotnet", "[\"server.dll\"]");

        Assert.Equal("dotnet", result.ExecutablePath);
        Assert.Equal(new[] { "server.dll" }, result.Arguments);
    }

    [Theory]
    [InlineData("{\"argument\":\"--stdio\"}")]
    [InlineData("[\"--stdio\",4]")]
    [InlineData("[")]
    public void Resolve_RejectsInvalidArgumentJson(string json) =>
        Assert.ThrowsAny<JsonException>(() => LspLaunchConfiguration.Resolve("server.exe", json));
}
