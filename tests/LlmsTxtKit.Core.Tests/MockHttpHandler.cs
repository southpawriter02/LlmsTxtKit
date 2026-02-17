namespace LlmsTxtKit.Core.Tests;

/// <summary>
/// A mock <see cref="HttpMessageHandler"/> for unit testing HTTP-dependent code
/// without making live network calls.
/// </summary>
/// <remarks>
/// <para>
/// This handler captures every request sent through it (via <see cref="Requests"/>)
/// and returns the response configured by the <see cref="Handler"/> delegate.
/// This allows tests to:
/// </para>
/// <list type="bullet">
/// <item>Verify that the correct URLs, headers, and methods are sent.</item>
/// <item>Return specific HTTP status codes and response bodies.</item>
/// <item>Simulate failures (timeouts, DNS errors) by throwing exceptions.</item>
/// <item>Track the number of requests for retry verification.</item>
/// </list>
/// <para>
/// Usage pattern:
/// <code>
/// var handler = new MockHttpHandler(request =>
///     new HttpResponseMessage(HttpStatusCode.OK)
///     {
///         Content = new StringContent("# My Site")
///     });
/// var httpClient = new HttpClient(handler);
/// var fetcher = new LlmsTxtFetcher(httpClient: httpClient);
/// </code>
/// </para>
/// </remarks>
public sealed class MockHttpHandler : HttpMessageHandler
{
    /// <summary>
    /// The delegate that produces a response for each incoming request.
    /// </summary>
    /// <remarks>
    /// The delegate receives the <see cref="HttpRequestMessage"/> and a
    /// <see cref="CancellationToken"/>, and returns a <see cref="Task{HttpResponseMessage}"/>.
    /// The delegate may throw exceptions to simulate transport-level failures.
    /// </remarks>
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    /// <summary>
    /// All requests that have been sent through this handler, in order.
    /// Useful for verifying retry counts, request headers, and URLs.
    /// </summary>
    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>
    /// Creates a mock handler with a synchronous response factory.
    /// </summary>
    /// <param name="handler">
    /// A function that takes an <see cref="HttpRequestMessage"/> and returns
    /// an <see cref="HttpResponseMessage"/>. The function is called once per
    /// request.
    /// </param>
    public MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = (request, _) => Task.FromResult(handler(request));
    }

    /// <summary>
    /// Creates a mock handler with an async response factory that receives
    /// the cancellation token. Useful for simulating timeouts and cancellation.
    /// </summary>
    /// <param name="handler">
    /// An async function that takes an <see cref="HttpRequestMessage"/> and
    /// <see cref="CancellationToken"/>, and returns an <see cref="HttpResponseMessage"/>.
    /// </param>
    public MockHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Record the request for later inspection
        Requests.Add(request);

        // Delegate to the configured handler
        return await _handler(request, cancellationToken).ConfigureAwait(false);
    }
}
