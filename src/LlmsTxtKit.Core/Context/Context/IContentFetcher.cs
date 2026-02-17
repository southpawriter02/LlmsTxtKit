namespace LlmsTxtKit.Core.Context;

/// <summary>
/// Abstraction for fetching the Markdown content of a linked URL during
/// context generation. This enables unit testing without network access and
/// allows alternative content retrieval strategies (local files, mocks, etc.).
/// </summary>
/// <remarks>
/// <para>
/// The default implementation (<see cref="HttpContentFetcher"/>) uses an
/// <see cref="System.Net.Http.HttpClient"/> to fetch content. When the
/// <see cref="ContextGenerator"/> is constructed with a
/// <see cref="Fetching.LlmsTxtFetcher"/> and/or <see cref="Caching.LlmsTxtCache"/>,
/// it creates an internal adapter that delegates to those services.
/// </para>
/// <para>
/// For testing, a simple in-memory implementation can be provided that maps
/// URLs to pre-defined content strings. See the test project's mock implementation.
/// </para>
/// </remarks>
public interface IContentFetcher
{
    /// <summary>
    /// Fetches the raw Markdown content at the specified URL.
    /// </summary>
    /// <param name="url">The URL to fetch content from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The raw content as a string, or <c>null</c> if the fetch failed.
    /// When <c>null</c> is returned, the caller should include an error
    /// placeholder in the generated context.
    /// </returns>
    Task<ContentFetchResult> FetchContentAsync(Uri url, CancellationToken cancellationToken = default);
}

/// <summary>
/// The result of fetching linked content during context generation.
/// </summary>
/// <param name="Content">The fetched content, or <c>null</c> if the fetch failed.</param>
/// <param name="ErrorMessage">An error message if the fetch failed, or <c>null</c> on success.</param>
public sealed record ContentFetchResult(string? Content, string? ErrorMessage)
{
    /// <summary>Whether the fetch was successful (content is available).</summary>
    public bool IsSuccess => Content != null;
}

/// <summary>
/// Default <see cref="IContentFetcher"/> implementation that uses an
/// <see cref="HttpClient"/> to fetch linked Markdown content. Handles common
/// HTTP errors gracefully and returns structured error information.
/// </summary>
/// <remarks>
/// This is used when the <see cref="ContextGenerator"/> is constructed without
/// a specific fetcher, or when linked URLs need direct HTTP retrieval.
/// </remarks>
public sealed class HttpContentFetcher : IContentFetcher
{
    /// <summary>The HTTP client used for fetching.</summary>
    private readonly HttpClient _httpClient;

    /// <summary>Whether this instance owns the HTTP client (for disposal).</summary>
    private readonly bool _ownsClient;

    /// <summary>
    /// Creates a new HTTP content fetcher with the specified client.
    /// </summary>
    /// <param name="httpClient">
    /// Optional HTTP client. If <c>null</c>, a new client is created.
    /// </param>
    public HttpContentFetcher(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <inheritdoc/>
    public async Task<ContentFetchResult> FetchContentAsync(
        Uri url, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new ContentFetchResult(
                    null,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new ContentFetchResult(content, null);
        }
        catch (HttpRequestException ex)
        {
            return new ContentFetchResult(null, $"Network error: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ContentFetchResult(null, "Request timed out");
        }
        catch (Exception ex)
        {
            return new ContentFetchResult(null, $"Fetch error: {ex.Message}");
        }
    }
}
