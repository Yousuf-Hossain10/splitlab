using System.Net;
using System.Text;
using System.Text.Json;
using Splitlab.Sdk.Events;

namespace Splitlab.Sdk.Tests.Events;

public class HttpEventTransportTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 20, 12, 0, 0, 123, TimeSpan.Zero);

    private static readonly TrackedEvent Exposure =
        new("e1", TrackedEvent.Exposure, "checkout", "treatment", null, null, "user-1", Noon);

    private static readonly TrackedEvent Conversion =
        new("e2", TrackedEvent.Conversion, null, null, "purchase", 19.99, "user-1", Noon);

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return await respond(request);
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string? body = null) => new(status)
    {
        Content = new StringContent(body ?? "", Encoding.UTF8, "application/json"),
    };

    private static (HttpEventTransport Transport, StubHandler Handler) Create(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new StubHandler(respond);
        return (new HttpEventTransport(new HttpClient(handler), new Uri("https://splitlab.example/api/"), "sl_abc_secret"), handler);
    }

    private static Task<EventSendResult> Send(HttpEventTransport transport, params TrackedEvent[] events) =>
        transport.SendAsync(events, CancellationToken.None);

    // ---- the request ----

    [Fact]
    public async Task PostsToTheEventsEndpointWithTheKeyAsABearerToken()
    {
        (HttpEventTransport transport, StubHandler handler) = Create(_ => Task.FromResult(Reply(HttpStatusCode.Accepted, """{"accepted":1,"rejected":[]}""")));

        await Send(transport, Exposure);

        (HttpRequestMessage request, _) = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://splitlab.example/api/v1/events", request.RequestUri!.ToString());
        Assert.Equal(("Bearer", "sl_abc_secret"), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task SendsOnlyTheFieldsEachKindOfEventHas()
    {
        (HttpEventTransport transport, StubHandler handler) = Create(_ => Task.FromResult(Reply(HttpStatusCode.Accepted, """{"accepted":2,"rejected":[]}""")));

        await Send(transport, Exposure, Conversion);

        using JsonDocument body = JsonDocument.Parse(handler.Seen[0].Body);
        JsonElement[] events = body.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(["eventId", "type", "experimentKey", "variationKey", "userId", "timestamp"], events[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["eventId", "type", "name", "value", "userId", "timestamp"], events[1].EnumerateObject().Select(p => p.Name));
        Assert.Equal("exposure", events[0].GetProperty("type").GetString());
        Assert.Equal(19.99, events[1].GetProperty("value").GetDouble());
        Assert.Equal(Noon, events[0].GetProperty("timestamp").GetDateTimeOffset());
    }

    [Fact]
    public async Task ASecondSendOfTheSameBatchIsByteForByteIdentical()
    {
        // The server recognises a retry by event id and timestamp, so both must serialise identically every time.
        (HttpEventTransport transport, StubHandler handler) = Create(_ => Task.FromResult(Reply(HttpStatusCode.ServiceUnavailable)));

        await Send(transport, Exposure, Conversion);
        await Send(transport, Exposure, Conversion);

        Assert.Equal(handler.Seen[0].Body, handler.Seen[1].Body);
        Assert.Contains("2026-10-20T12:00:00.123", handler.Seen[0].Body, StringComparison.Ordinal);
    }

    // ---- reading the answer ----

    [Fact]
    public async Task AnAcceptedBatchReportsHowManyEventsTheServerRefused()
    {
        (HttpEventTransport transport, _) = Create(_ => Task.FromResult(Reply(
            HttpStatusCode.Accepted, """{"accepted":1,"rejected":[{"index":1,"reason":"a conversion needs a valid name."}]}""")));

        EventSendResult result = await Send(transport, Exposure, Conversion);

        Assert.Equal(new EventSendResult(EventSendStatus.Accepted, 1), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"rejected":"nope"}""")]
    public async Task AnUnreadableReceiptStillCountsAsAcceptedSoNothingIsSentTwice(string body)
    {
        (HttpEventTransport transport, _) = Create(_ => Task.FromResult(Reply(HttpStatusCode.Accepted, body)));

        EventSendResult result = await Send(transport, Exposure);

        Assert.Equal(new EventSendResult(EventSendStatus.Accepted, 0), result);
    }

    [Fact]
    public async Task ThrottlingCarriesTheServersRetryAfterInSeconds()
    {
        (HttpEventTransport transport, _) = Create(_ =>
        {
            HttpResponseMessage response = Reply(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return Task.FromResult(response);
        });

        EventSendResult result = await Send(transport, Exposure);

        Assert.Equal(EventSendStatus.Throttled, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(7), result.RetryAfter);
    }

    [Fact]
    public async Task ThrottlingCanCarryARetryAfterDate()
    {
        (HttpEventTransport transport, _) = Create(_ =>
        {
            HttpResponseMessage response = Reply(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(30));
            return Task.FromResult(response);
        });

        EventSendResult result = await Send(transport, Exposure);

        Assert.Equal(EventSendStatus.Throttled, result.Status);
        Assert.InRange(result.RetryAfter!.Value.TotalSeconds, 20, 31);
    }

    [Fact]
    public async Task ThrottlingWithoutARetryAfterStillBacksOff()
    {
        (HttpEventTransport transport, _) = Create(_ => Task.FromResult(Reply(HttpStatusCode.TooManyRequests)));

        EventSendResult result = await Send(transport, Exposure);

        Assert.Equal(new EventSendResult(EventSendStatus.Throttled), result);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task ServerErrorsAreWorthRetrying(HttpStatusCode status)
    {
        (HttpEventTransport transport, _) = Create(_ => Task.FromResult(Reply(status)));

        Assert.Equal(EventSendStatus.Transient, (await Send(transport, Exposure)).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    public async Task RefusalsThatRetryingCannotFixAreFinal(HttpStatusCode status)
    {
        (HttpEventTransport transport, _) = Create(_ => Task.FromResult(Reply(status)));

        EventSendResult result = await Send(transport, Exposure);

        Assert.Equal(EventSendStatus.Permanent, result.Status);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), result.Detail, StringComparison.Ordinal);
    }

    // ---- failures that are not HTTP answers ----

    [Fact]
    public async Task ANetworkFailureIsTransient()
    {
        (HttpEventTransport transport, _) = Create(_ => throw new HttpRequestException("connection refused"));

        EventSendResult result = await Send(transport, Exposure);

        Assert.Equal(EventSendStatus.Transient, result.Status);
        Assert.Contains("connection refused", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHttpClientTimeoutIsTransient()
    {
        // HttpClient reports its own timeout as a cancellation that the caller did not ask for.
        (HttpEventTransport transport, _) = Create(_ => throw new TaskCanceledException("timed out"));

        Assert.Equal(EventSendStatus.Transient, (await Send(transport, Exposure)).Status);
    }

    [Fact]
    public async Task WhenTheCallerCancelsTheCancellationIsPassedOn()
    {
        using var cts = new CancellationTokenSource();
        (HttpEventTransport transport, _) = Create(async request =>
        {
            await cts.CancelAsync();
            cts.Token.ThrowIfCancellationRequested();
            return Reply(HttpStatusCode.Accepted);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync([Exposure], cts.Token));
    }

    // ---- construction ----

    [Fact]
    public void ArgumentsAreChecked()
    {
        using var http = new HttpClient();
        var baseUri = new Uri("https://splitlab.example/");

        Assert.Throws<ArgumentNullException>(() => new HttpEventTransport(null!, baseUri, "key"));
        Assert.Throws<ArgumentNullException>(() => new HttpEventTransport(http, null!, "key"));
        Assert.Throws<ArgumentException>(() => new HttpEventTransport(http, baseUri, " "));
    }
}
