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
        using var http = new HttpClient(new StubHandler(request =>
        {
            requestedPath = request.RequestUri?.AbsolutePath;
            suppliedKey = request.Headers.GetValues("x-goog-api-key").Single();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"OK\"}]}}]}")
            };
        }));
        using var service = new GeminiService("test-only-key", "gemini-test-model", http);

        var reply = await service.AskAsync("Réponds OK");

        Assert.Equal("OK", reply);
        Assert.Equal("/v1beta/models/gemini-test-model:generateContent", requestedPath);
        Assert.Equal("test-only-key", suppliedKey);
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
