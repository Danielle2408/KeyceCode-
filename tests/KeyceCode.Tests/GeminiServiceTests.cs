using KeyceCode.Services;
using System.Net;
using Xunit;

namespace KeyceCode.Tests;

public sealed class GeminiServiceTests
{
    [Fact]
    public async Task AskAsync_UsesConfiguredModelAndHeaderKey()
    {
        string? requestedPath = null;
        string? suppliedKey = null;
        string? requestBody = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            requestedPath = request.RequestUri?.AbsolutePath;
            suppliedKey = request.Headers.GetValues("x-goog-api-key").Single();
            requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"completed\",\"steps\":[{\"type\":\"model_output\",\"content\":[{\"type\":\"text\",\"text\":\"OK\"}]}]}")
            };
        }));
        using var service = new GeminiService("test-only-key", "gemini-test-model", http);

        var reply = await service.AskAsync("Réponds OK");

        Assert.Equal("OK", reply);
        Assert.Equal("/v1beta/interactions", requestedPath);
        Assert.Equal("test-only-key", suppliedKey);
        using var payload = System.Text.Json.JsonDocument.Parse(requestBody!);
        Assert.Equal("gemini-test-model", payload.RootElement.GetProperty("model").GetString());
        Assert.False(payload.RootElement.GetProperty("store").GetBoolean());
        Assert.StartsWith("Tu es KeyceIA,", payload.RootElement.GetProperty("input").GetString());
        Assert.Equal(2048, payload.RootElement.GetProperty("generation_config").GetProperty("max_output_tokens").GetInt32());
    }

    [Fact]
    public async Task CompleteAsync_PreservesWhitespaceFromInteractionOutput()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"completed\",\"steps\":[{\"type\":\"model_output\",\"content\":[{\"type\":\"text\",\"text\":\"\\n    return 42;\\n\"}]}]}")
        }));
        using var service = new GeminiService("test-only-key", "gemini-test-model", http);

        var result = await service.CompleteAsync("int Answer()\n{", "csharp");

        Assert.Equal("\n    return 42;\n", result);
    }

    [Fact]
    public void Model_RejectsBlankIdentifier()
    {
        using var service = new GeminiService("test-only-key", "gemini-test-model", new HttpClient(new StubHandler(_ => new HttpResponseMessage())));
        Assert.Throws<ArgumentException>(() => service.Model = " ");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
