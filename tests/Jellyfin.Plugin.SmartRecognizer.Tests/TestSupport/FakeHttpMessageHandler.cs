using System.Net;
using System.Text;

namespace Jellyfin.Plugin.SmartRecognizer.Tests.TestSupport;

/// <summary>
/// Answers HTTP requests from a routing function and records every request.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string?> RequestBodies { get; } = [];

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent(string.Empty) };

    /// <summary>Serves TestData/{folder}/{file}.</summary>
    public static HttpResponseMessage Fixture(string folder, string file) =>
        Json(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", folder, file)));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return _respond(request);
    }
}
