using System.Net;
using Mahjong.Plugin.Dalamud.Telemetry;
using Mahjong.Plugin.Dalamud.Tests.Stubs;

namespace Mahjong.Plugin.Dalamud.Tests;

public class HttpTelemetryClientTests
{
    [Fact]
    public async Task Too_many_requests_is_reported_as_rate_limited_without_warning_spam()
    {
        using var tmp = new TempDir();
        var payloadPath = Path.Combine(tmp.Path, "memdumps-test.ndjson");
        await File.WriteAllTextAsync(payloadPath, "{\"v\":2}\n");

        using var http = new HttpClient(new StatusHandler(HttpStatusCode.TooManyRequests));
        var log = new RecordingPluginLog();
        var envelope = new TelemetryEnvelope(
            Guid.NewGuid(), "1.0.0", "hash", "game", "English", "Win32NT", 1);
        var client = new HttpTelemetryClient(http, envelope, log);

        var result = await client.UploadAsync(
            "https://example.test/v1/upload", "memdumps", payloadPath, CancellationToken.None);

        Assert.Equal(TelemetryUploadResult.RateLimited, result);
        Assert.DoesNotContain(log.Entries, e => e.Level == Serilog.Events.LogEventLevel.Warning);
    }

    private sealed class StatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }
}
