using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Splitlab.Sdk.Events;

/// <summary>Sends events to <c>POST /v1/events</c> with an SDK key that has the <c>events:write</c> permission (ADR-0005).</summary>
public sealed class HttpEventTransport : IEventTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly Uri _url;
    private readonly string _sdkKey;

    /// <param name="http">Client to use; its lifetime stays with the caller.</param>
    /// <param name="baseUri">Base address of the Splitlab API.</param>
    /// <param name="sdkKey">The environment's SDK key, sent as a bearer token.</param>
    public HttpEventTransport(HttpClient http, Uri baseUri, string sdkKey)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(sdkKey);

        _http = http;
        _sdkKey = sdkKey;
        _url = new Uri(baseUri, "v1/events");
    }

    /// <inheritdoc />
    public async Task<EventSendResult> SendAsync(IReadOnlyList<TrackedEvent> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = JsonContent.Create(new { events = batch }, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _sdkKey);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return EventSendResult.Transient(ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The HttpClient's own timeout, not a request from the caller to stop.
            return EventSendResult.Transient("The request timed out.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return EventSendResult.Throttled(RetryAfterOf(response));
            }

            if (response.IsSuccessStatusCode)
            {
                return EventSendResult.Accepted(await RejectedCountAsync(response, cancellationToken).ConfigureAwait(false));
            }

            int status = (int)response.StatusCode;
            string detail = $"HTTP {status}";
            return status is 408 or >= 500
                ? EventSendResult.Transient(detail)
                : EventSendResult.Permanent(detail); // 400, 401, 403...: sending the same thing again will not help
        }
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }

    private static async Task<int> RejectedCountAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument body = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return body.RootElement.TryGetProperty("rejected", out JsonElement rejected) && rejected.ValueKind == JsonValueKind.Array
                ? rejected.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            // The batch was accepted; an unreadable receipt must not make us send it again.
            return 0;
        }
    }
}
