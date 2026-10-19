using System.Net;
using System.Net.Http.Headers;

namespace Splitlab.Sdk.Config;

/// <summary>Fetches the datafile over HTTP with <c>If-None-Match</c> revalidation (ADR-0007).</summary>
public sealed class HttpDatafileSource : IDatafileSource
{
    private readonly HttpClient _http;
    private readonly Uri _url;

    /// <param name="http">Client to use; its lifetime stays with the caller.</param>
    /// <param name="baseUri">Base address of the Splitlab API.</param>
    /// <param name="environmentKey">Environment whose datafile to fetch.</param>
    public HttpDatafileSource(HttpClient http, Uri baseUri, string environmentKey)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentKey);

        _http = http;
        _url = new Uri(baseUri, $"v1/config/{Uri.EscapeDataString(environmentKey)}");
    }

    /// <inheritdoc />
    public async Task<DatafileFetchResult> FetchAsync(string? etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        if (etag is not null && EntityTagHeaderValue.TryParse(etag, out EntityTagHeaderValue? tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }

        using HttpResponseMessage response = await _http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new DatafileFetchResult(true, null, etag);
        }

        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new DatafileFetchResult(false, json, response.Headers.ETag?.ToString());
    }
}
