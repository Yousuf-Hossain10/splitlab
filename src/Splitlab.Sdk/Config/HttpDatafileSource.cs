using System.Net;
using System.Net.Http.Headers;

namespace Splitlab.Sdk.Config;

/// <summary>Fetches the datafile over HTTP with <c>If-None-Match</c> revalidation (ADR-0007).</summary>
public sealed class HttpDatafileSource : IDatafileSource
{
    private readonly HttpClient _http;
    private readonly Uri _url;
    private readonly string? _sdkKey;

    /// <param name="http">Client to use; its lifetime stays with the caller.</param>
    /// <param name="baseUri">Base address of the Splitlab API.</param>
    /// <param name="environmentKey">Environment whose datafile to fetch.</param>
    /// <param name="sdkKey">The environment's SDK key (needs <c>config:read</c>), sent as a bearer token.</param>
    public HttpDatafileSource(HttpClient http, Uri baseUri, string environmentKey, string? sdkKey = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentKey);

        _http = http;
        _sdkKey = sdkKey;
        _url = new Uri(baseUri, $"v1/config/{Uri.EscapeDataString(environmentKey)}");
    }

    /// <inheritdoc />
    public async Task<DatafileFetchResult> FetchAsync(string? etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        if (_sdkKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _sdkKey);
        }

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
