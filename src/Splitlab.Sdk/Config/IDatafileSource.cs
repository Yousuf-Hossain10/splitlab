namespace Splitlab.Sdk.Config;

/// <summary>Outcome of one conditional datafile fetch.</summary>
/// <param name="NotModified"><c>true</c> when the server answered 304: the caller's copy is current.</param>
/// <param name="Json">The datafile body; <c>null</c> when <paramref name="NotModified"/>.</param>
/// <param name="ETag">The ETag of the body, if the server sent one.</param>
public readonly record struct DatafileFetchResult(bool NotModified, string? Json, string? ETag);

/// <summary>Where the SDK gets its datafile from. Abstracted so tests and alternative transports can substitute it.</summary>
public interface IDatafileSource
{
    /// <summary>Fetches the datafile, sending <paramref name="etag"/> as <c>If-None-Match</c> when not null.</summary>
    Task<DatafileFetchResult> FetchAsync(string? etag, CancellationToken cancellationToken);
}
