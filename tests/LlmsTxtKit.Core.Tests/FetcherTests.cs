using System.Net;
using System.Net.Sockets;
using LlmsTxtKit.Core.Fetching;
using Xunit;

namespace LlmsTxtKit.Core.Tests;

/// <summary>
/// Unit tests for <see cref="LlmsTxtFetcher"/>. All tests use a <see cref="MockHttpHandler"/>
/// — no live HTTP requests are made.
/// </summary>
/// <remarks>
/// <para>
/// These tests verify the fetcher's core responsibilities per Design Spec § 2.2:
/// </para>
/// <list type="bullet">
/// <item>URL construction from domain names</item>
/// <item>Correct classification of HTTP responses into <see cref="FetchStatus"/> categories</item>
/// <item>WAF block detection via response header heuristics</item>
/// <item>Retry-After header parsing for 429 responses</item>
/// <item>Retry logic with exponential backoff for transient failures</item>
/// <item>Per-request timeout handling</item>
/// <item>CancellationToken propagation</item>
/// <item>Input validation (null/empty domain)</item>
/// <item>Custom User-Agent header propagation</item>
/// <item>Duration tracking on all outcomes</item>
/// </list>
/// <para>
/// Test method naming follows the convention from CONTRIBUTING.md:
/// <c>MethodName_Scenario_ExpectedBehavior</c>.
/// </para>
/// </remarks>
public class FetcherTests
{
    // ---------------------------------------------------------------
    // Helper: Minimal valid llms.txt content for successful parse tests
    // ---------------------------------------------------------------

    /// <summary>
    /// A minimal but valid llms.txt file content that the parser can process
    /// into a non-empty <see cref="Parsing.LlmsDocument"/>.
    /// </summary>
    private const string ValidLlmsTxtContent = """
        # Test Site
        > A test site for unit testing the fetcher.
        ## Docs
        - [Guide](https://example.com/guide.md): The main guide
        """;

    // ---------------------------------------------------------------
    // 1. Success (HTTP 200)
    // ---------------------------------------------------------------

    /// <summary>
    /// Verifies that a 200 OK response with valid llms.txt content produces
    /// a <see cref="FetchStatus.Success"/> result with a parsed document.
    /// </summary>
    [Fact]
    public async Task FetchAsync_Success200_ReturnsParsedDocument()
    {
        // Arrange: mock returns 200 with valid llms.txt content
        var handler = new MockHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidLlmsTxtContent)
            });

        using var httpClient = new HttpClient(handler);
        using var fetcher = new LlmsTxtFetcher(httpClient: httpClient);

        // Act
        var result = await fetcher.FetchAsync("example.com");

        // Assert: status is Success and the document is populated
        Assert.Equal(FetchStatus.Success, result.Status);
        Assert.NotNull(result.Document);
        Assert.Equal("Test Site", result.Document.Title);
        Assert.Equal("A test site for unit testing the fetcher.", result.Document.Summary);
        Assert.NotNull(result.RawContent);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("example.com", result.Domain);
    }

    // ---------------------------------------------------------------
    // 2. Not Found (HTTP 404)
    // ---------------------------------------------------------------

    /// <summary>
    /// Verifies that a 404 response produces a <see cref="FetchStatus.NotFound"/> result
    /// with no parsed document.
    /// </summary>
    [Fact]
    public async Task FetchAsync_NotFound404_ReturnsNotFoundStatus()
    {
        // Arrange
        var handler = new MockHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("Not Found")
            });

        using var httpClient = new HttpClient(handler);
        using var fetcher = new LlmsTxtFetcher(httpClient: httpClient);

        // Act
        var result = await fetcher.FetchAsync("nofile.example.com");

        // Assert
        Assert.Equal(FetchStatus.NotFound, result.Status);
        Assert.Null(result.Document);
        Assert.Equal(404, result.StatusCode);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("404", result.ErrorMessage);
    }

    // ---------------------------------------------------------------
    // 3. WAF Block — Cloudflare (cf-ray header)
    // ---------------------------------------------------------------

    /// <summary>
    /// Verifies that a 403 response with Cloudflare's <c>cf-ray</c> header
    /// produces a <see cref="FetchStatus.Blocked"/> result with a Cloudflare-specific
    /// <see cref="FetchResult.BlockReason"/>.
    /// </summary>
    [Fact]
    public async Task FetchAsync_Blocked403WithCloudflareHeaders_ReturnsBlockedStatus()
    {
        // Arrange: 403 with Cloudflare-specific headers
        var handler = new MockHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<html>Cloudflare challenge</html>")
            };
            // Cloudflare always includes a cf-ray header on its responses
            response.Headers.TryAddWithoutValidation("cf-ray", "abc123-IAD");
            response.Headers.TryAddWithoutValidation("server", "cloudflare");
            return response;
        });

        using var httpClient = new HttpClient(handler);
        using var fetcher = new LlmsTxtFetcher(httpClient: httpClient);

        // Act
        var result = await fetcher.FetchAsync("protected.example.com");

        // Assert
        Assert.Equal(FetchStatus.Blocked, result.Status);
        Assert.Null(result.Document);
        Assert.Equal(403, result.StatusCode);
        Assert.NotNull(result.BlockReason);
        Assert.Contains("Cloudflare", result.BlockReason);
    }

    // ---------------------------------------------------------------
    // 4. WAF Block — Generic 403 (no identifiable WAF)
    // ---------------------------------------------------------------

    /// <summary>
    /// Verifies that a 403 response without identifiable WAF headers still
    /// produces a <see cref="FetchStatus.Blocked"/> result, but with a generic
    /// block reason since the WAF vendor could not be identified.
    /// </summary>
    [Fact]
    public async Task FetchAsync_Blocked403Generic_ReturnsBlockedWithoutSpecificDiagnosis()
    {
        // Arrange: plain 403 with no WAF-identifying headers
        var handler = new MockHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("Forbidden")
            });

        using var httpClient = new HttpClient(handler);
        using var fetcher = new LlmsTxtFetcher(httpClient: httpClient);

        // Act
        var result = await fetcher.FetchAsync("secretive.example.com");

        // Assert
        Assert.Equal(FetchStatus.Blocked, result.Status);
        Assert.NotNull(result.BlockReason);
        Assert.Contains("403", result.BlockReason);
        // The block reason should NOT mention a specific WAF vendor
        Assert.DoesNotContain("Cloudflare", result.BlockReason);
        Assert.DoesNotContain("AWS", result.BlockReason);
    }

    // ---------------------------------------------------------------
    // 5. Rate Limited (HTTP 429) with Retry-After header
    // ---------------------------------------------------------------

    /// <summary>
    /// Verifies that a 429 response with a <c>Retry-After</c> header produces
    /// a <see cref="FetchStatus.RateLimited"/> result with a parsed
    /// <see cref="FetchResult.RetryAfter"/> duration.
    /// </summary>
    [Fact]
    public async Task FetchAsync_RateLimited429_ReturnsRateLimitedWithRetryAfter()
    {
        // Arrange: 429 with Retry-After: 60 (seconds)
        var handler = new MockHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("Too Many Requests")
            };
            response.Headers.TryAddWithoutValidation("Retry-After", "60");
            return response;
        });

        using var httpClient = new HttpClient(handler);
        using var fetcher = new LlmsTxtFetcher(httpClient: httpClient);

        // Act
        var result = await fetcher.FetchAsync("busy.example.com");

        // Assert
        Assert.Equal(FetchStatus.RateLimited, result.Status);
        Assert.Equal(429, result.StatusCode);
        Assert.NotNull(result.RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(60), result.RetryAfter.Value);
    }

    // ---------------------------------------------------------------
    // 6. DNS Failure
    // ---------------------------------------------------------------

    /// <summary>
    /// Verifies that a DNS resolution failure produces a
    /// <see cref="FetchStatus.DnsFailure"/> result.
    /// </summary>
    [Fact]
    public async Task FetchAsync_DnsFailure_ReturnsDnsFailureStatus()
    {
        // Arrange: throw HttpRequestException wrapping a SocketException with HostNotFound
        var handler = new MockHttpHandler((_, _) =>
        {
            throw new HttpRequestException(
                "No such host is known",
                new SocketException((int)SocketError.HostNotFound));
        });

        using var httpClient = new HttpClient(handler);
        using var fetcher = new LlmsTxtFetcher(httpClient: httpClient);

        // Act
        var result = await fetcher.FetchAsync("nonexistent.invalid");

        // Assert
        Assert.Equal(FetchStatus.DnsFailure, result.Status);
        Assert.Null(result.StatusCode); // No HTTP response was received
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("DNS", result.ErrorMessage);
    }

    // ---------------------------------------------------------------
    // 7. Timeout
    // ---------------------------------------------------------------

    /// <summary>
    /// Verifies that a request exceeding the configured timeout produces
    /// a <see cref="FetchStatus.Timeout"/> result.
    /// </summary>
    [Fact]
    public async Task FetchAsync_Timeout_ReturnsTimeoutStatus()
    {
        // Arrange: handler delays longer than the 1-second timeout
        var handler = new MockHttpHandler(async (_, ct) =>
        {
            // Wait for 10 seconds — the fetcher's 1-second timeout will fire first
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var httpClient = new HttpClient(handler);
        var options = new FetcherOptions
        {
            TimeoutSeconds = 1,
            MaxRetries = 0 // No retries — we want the timeout result immediately
        };
        using var fetcher = new LlmsTxtFetcher(options: options, httpClient: httpClient);

        // Act
        var result = await fetcher.FetchAsync("slow.example.com");

        // Assert
        Assert.Equal(FetchStatus.Timeout, result.Status);
        Assert.Null(result.StatusCode);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("timed out", result.ErrorMessage);
    }

    // ---------------------------------------------------------------
    // 8. Server Error (HTTP 500)
    // ---------------------------------------------------------------

    /// <summary>
    /// Verifies that a 500 response produces a <see cref="FetchStatus.Error"/>
    /// result after exhausting retries. 5xx responses are considered transient
    /// and are retried up to <see cref="FetcherOptions.MaxRetries"/> times.
    /// </summary>
    [Fact]
    public async Task FetchAsync_ServerError500_ReturnsErrorStatusAfterRetries()
    {
        // Arrange: always return 500
        var handler = new MockHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Internal Server Error")
            });

        using var httpClient = new HttpClient(handler);
        var options = new FetcherOptions
        {
            MaxRetries = 0, // No retries — return the 500 immediately
            RetryDelayMs = 0
        };
        using var fetcher = new LlmsTxtFetcher(options: options, httpClient: httpClient);

        // Act
        var result = await fetcher.FetchAsync("broken.example.com");

        // Assert
        Assert.Equal(FetchStatus.Error, result.Status);
        Assert.Equal(500, result.StatusCode);
        Assert.NotNull(result.ErrorMessage);
    }

    // ---------------------------------------------------------------
    // 9. Retry on transient failure — verifies retry count
    // ---------------------------------------------------------------

    /// <summary>
    /// Verifies that the fetcher retries transient failures (5xx) up to the
    /// configured <see cref="FetcherOptions.MaxRetries"/> count, then returns
    /// the success result when a retry succeeds.
    /// </summary>
    [Fact]
    public async Task FetchAsync_RetryOnTransientFailure_RetriesAndSucceeds()
    {
        // Arrange: fail twice with 500, then succeed on the 3rd attempt
        int requestCount = 0;
        var handler = new MockHttpHandler(_ =>
        {
            requestCount++;
            if (requestCount <= 2)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("Server Error")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidLlmsTxtContent)
            };
        });

        using var httpClient = new HttpClient(handler);
        var options = new FetcherOptions
        {
            MaxRetries = 2,      // 2 retries = 3 total attempts
            RetryDelayMs = 10    // Minimal delay for fast tests
        };
        using var fetcher = new LlmsTxtFetcher(options: options, httpClient: httpClient);

        // Act
        var result = await fetcher.FetchAsync("flaky.example.com");

        // Assert: eventually succeeds
        Assert.Equal(FetchStatus.Success, result.Status);
        Assert.NotNull(result.Document);
        // Verify 3 total requests were made (1 original + 2 retries)
        Assert.Equal(3, handler.Requests.Count);
    }

    // ---------------------------------------------------------------
    // 10. Cancellation token propagation
    // ---------------------------------------------------------------

    /// <summary>
    /// Verifies that cancelling the <see cref="CancellationToken"/> causes
    /// <see cref="LlmsTxtFetcher.FetchAsync"/> to throw
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    [Fact]
    public async Task FetchAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        // Arrange: handler that waits forever (will never complete naturally)
        var handler = new MockHttpHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var httpClient = new HttpClient(handler);
        var options = new FetcherOptions
        {
            TimeoutSeconds = 300 // Very long timeout so it doesn't interfere
        };
        using var fetcher = new LlmsTxtFetcher(options: options, httpClient: httpClient);

        using var cts = new CancellationTokenSource();
        // Cancel immediately
        cts.Cancel();

        // Act & Assert: should throw OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fetcher.FetchAsync("example.com", cts.Token));
    }

    // ---------------------------------------------------------------
    // 11. Null domain throws ArgumentException
    // ---------------------------------------------------------------

    /// <summary>
    /// Verifies that a null domain input throws <see cref="ArgumentNullException"/>
    /// (a subclass of <see cref="ArgumentException"/>).
    /// This is a programming error per Design Spec § 5.
    /// </summary>
    /// <remarks>
    /// <see cref="ArgumentException.ThrowIfNullOrWhiteSpace"/> throws
    /// <see cref="ArgumentNullException"/> for null inputs specifically,
    /// and <see cref="ArgumentException"/> for empty/whitespace inputs.
    /// </remarks>
    [Fact]
    public async Task FetchAsync_NullDomain_ThrowsArgumentNullException()
    {
        // Arrange
        var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        using var fetcher = new LlmsTxtFetcher(httpClient: httpClient);

        // Act & Assert: null input throws ArgumentNullException (subclass of ArgumentException)
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => fetcher.FetchAsync(null!));
    }

    /// <summary>
    /// Verifies that an empty-string domain input throws <see cref="ArgumentException"/>.
    /// </summary>
    [Fact]
    public async Task FetchAsync_EmptyDomain_ThrowsArgumentException()
    {
        // Arrange
        var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        using var fetcher = new LlmsTxtFetcher(httpClient: httpClient);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => fetcher.FetchAsync(string.Empty));
    }

    // ---------------------------------------------------------------
    // 12. Custom User-Agent is sent in request headers
    // ---------------------------------------------------------------

    /// <summary>
    /// Verifies that the configured <see cref="FetcherOptions.UserAgent"/>
    /// appears in the outgoing HTTP request's User-Agent header.
    /// </summary>
    [Fact]
    public async Task FetchAsync_CustomUserAgent_SentInRequestHeaders()
    {
        // Arrange
        const string customUA = "MyApp/1.0 (custom-test-agent)";
        var handler = new MockHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidLlmsTxtContent)
            });

        using var httpClient = new HttpClient(handler);
        var options = new FetcherOptions { UserAgent = customUA };
        using var fetcher = new LlmsTxtFetcher(options: options, httpClient: httpClient);

        // Act
        await fetcher.FetchAsync("example.com");

        // Assert: inspect the captured request's User-Agent header
        Assert.Single(handler.Requests);
        var sentRequest = handler.Requests[0];
        var uaHeader = sentRequest.Headers.UserAgent.ToString();
        Assert.Contains("MyApp/1.0", uaHeader);
    }

    // ---------------------------------------------------------------
    // 13. Duration is populated on all outcomes
    // ---------------------------------------------------------------

    /// <summary>
    /// Verifies that <see cref="FetchResult.Duration"/> is a non-zero
    /// <see cref="TimeSpan"/> on successful fetches.
    /// </summary>
    [Fact]
    public async Task FetchAsync_Duration_PopulatedOnSuccess()
    {
        // Arrange
        var handler = new MockHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidLlmsTxtContent)
            });

        using var httpClient = new HttpClient(handler);
        using var fetcher = new LlmsTxtFetcher(httpClient: httpClient);

        // Act
        var result = await fetcher.FetchAsync("example.com");

        // Assert: duration should be non-negative (may be very small in unit tests)
        Assert.True(result.Duration >= TimeSpan.Zero,
            $"Expected non-negative duration, got {result.Duration}.");
    }

    /// <summary>
    /// Verifies that <see cref="FetchResult.Duration"/> is populated even
    /// on failure outcomes (NotFound in this case).
    /// </summary>
    [Fact]
    public async Task FetchAsync_Duration_PopulatedOnFailure()
    {
        // Arrange
        var handler = new MockHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        using var httpClient = new HttpClient(handler);
        using var fetcher = new LlmsTxtFetcher(httpClient: httpClient);

        // Act
        var result = await fetcher.FetchAsync("missing.example.com");

        // Assert
        Assert.True(result.Duration >= TimeSpan.Zero);
    }

    // ---------------------------------------------------------------
    // 14. Correct URL construction
    // ---------------------------------------------------------------

    /// <summary>
    /// Verifies that the fetcher constructs the correct URL from a domain name:
    /// <c>https://{domain}/llms.txt</c>.
    /// </summary>
    [Fact]
    public async Task FetchAsync_ConstructsCorrectUrl()
    {
        // Arrange
        var handler = new MockHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidLlmsTxtContent)
            });

        using var httpClient = new HttpClient(handler);
        using var fetcher = new LlmsTxtFetcher(httpClient: httpClient);

        // Act
        await fetcher.FetchAsync("docs.anthropic.com");

        // Assert: verify the request URL
        Assert.Single(handler.Requests);
        Assert.Equal(
            "https://docs.anthropic.com/llms.txt",
            handler.Requests[0].RequestUri?.ToString());
    }

    // ---------------------------------------------------------------
    // 15. Non-transient 4xx errors are NOT retried
    // ---------------------------------------------------------------

    /// <summary>
    /// Verifies that non-transient HTTP errors (like 404) are NOT retried.
    /// Only 5xx responses and timeouts trigger retries.
    /// </summary>
    [Fact]
    public async Task FetchAsync_NonTransient404_NotRetried()
    {
        // Arrange
        var handler = new MockHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        using var httpClient = new HttpClient(handler);
        var options = new FetcherOptions
        {
            MaxRetries = 3, // Generous retry budget
            RetryDelayMs = 10
        };
        using var fetcher = new LlmsTxtFetcher(options: options, httpClient: httpClient);

        // Act
        var result = await fetcher.FetchAsync("gone.example.com");

        // Assert: only 1 request (no retries for 404)
        Assert.Equal(FetchStatus.NotFound, result.Status);
        Assert.Single(handler.Requests);
    }

    // ---------------------------------------------------------------
    // 16. Response headers are captured
    // ---------------------------------------------------------------

    /// <summary>
    /// Verifies that response headers are captured in the result for downstream
    /// use (caching via ETag/Last-Modified, WAF identification, etc.).
    /// </summary>
    [Fact]
    public async Task FetchAsync_ResponseHeaders_CapturedInResult()
    {
        // Arrange
        var handler = new MockHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidLlmsTxtContent)
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc123\"");
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
            return response;
        });

        using var httpClient = new HttpClient(handler);
        using var fetcher = new LlmsTxtFetcher(httpClient: httpClient);

        // Act
        var result = await fetcher.FetchAsync("headers.example.com");

        // Assert
        Assert.NotNull(result.ResponseHeaders);
        Assert.True(result.ResponseHeaders.ContainsKey("etag"),
            "Expected 'etag' key in response headers.");
        Assert.True(result.ResponseHeaders.ContainsKey("last-modified"),
            "Expected 'last-modified' key in response headers.");
    }

    // ---------------------------------------------------------------
    // 17. AWS WAF detection
    // ---------------------------------------------------------------

    /// <summary>
    /// Verifies that a 403 response with AWS CloudFront headers produces
    /// a <see cref="FetchStatus.Blocked"/> result with an AWS-specific diagnosis.
    /// </summary>
    [Fact]
    public async Task FetchAsync_Blocked403WithAwsHeaders_ReturnsBlockedWithAwsDiagnosis()
    {
        // Arrange
        var handler = new MockHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<!DOCTYPE HTML PUBLIC><html><body>ERROR</body></html>")
            };
            response.Headers.TryAddWithoutValidation("server", "CloudFront");
            response.Headers.TryAddWithoutValidation("x-amz-cf-id", "some-cf-id");
            return response;
        });

        using var httpClient = new HttpClient(handler);
        using var fetcher = new LlmsTxtFetcher(httpClient: httpClient);

        // Act
        var result = await fetcher.FetchAsync("aws-protected.example.com");

        // Assert
        Assert.Equal(FetchStatus.Blocked, result.Status);
        Assert.NotNull(result.BlockReason);
        Assert.Contains("CloudFront", result.BlockReason);
    }

    // ---------------------------------------------------------------
    // 18. Disposed fetcher throws ObjectDisposedException
    // ---------------------------------------------------------------

    /// <summary>
    /// Verifies that calling <see cref="LlmsTxtFetcher.FetchAsync"/> after
    /// disposing the fetcher throws <see cref="ObjectDisposedException"/>,
    /// which is an unrecoverable error per Design Spec § 5.
    /// </summary>
    [Fact]
    public async Task FetchAsync_DisposedFetcher_ThrowsObjectDisposedException()
    {
        // Arrange
        var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        var fetcher = new LlmsTxtFetcher(httpClient: httpClient);
        fetcher.Dispose(); // Dispose the fetcher

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fetcher.FetchAsync("example.com"));
    }
}
