using LlmsTxtKit.Core.Caching;
using LlmsTxtKit.Core.Context;
using LlmsTxtKit.Core.Fetching;
using LlmsTxtKit.Core.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LlmsTxtKit.Mcp.Server;

/// <summary>
/// Configures dependency injection for the LlmsTxtKit MCP server. Registers all
/// Core services (<see cref="LlmsTxtFetcher"/>, <see cref="LlmsTxtCache"/>,
/// <see cref="LlmsDocumentValidator"/>, <see cref="ContextGenerator"/>) as
/// singletons so they can be injected into MCP tool methods.
/// </summary>
/// <remarks>
/// <para>
/// The DI setup follows the architecture principle from Design Spec § 1:
/// the MCP layer is a thin protocol adapter over Core services. Each tool
/// receives its dependencies via constructor injection (or parameter injection
/// for static tool methods in the MCP SDK).
/// </para>
/// <para>
/// Configuration is read from environment variables with sensible defaults:
/// </para>
/// <list type="bullet">
/// <item><c>LLMSTXTKIT_USER_AGENT</c> — Custom User-Agent string (default: "LlmsTxtKit/0.7.0")</item>
/// <item><c>LLMSTXTKIT_TIMEOUT_SECONDS</c> — HTTP fetch timeout (default: 15)</item>
/// <item><c>LLMSTXTKIT_MAX_RETRIES</c> — Fetch retry count (default: 2)</item>
/// <item><c>LLMSTXTKIT_CACHE_TTL_MINUTES</c> — Cache TTL in minutes (default: 60)</item>
/// <item><c>LLMSTXTKIT_CACHE_MAX_ENTRIES</c> — Maximum cached domains (default: 1000)</item>
/// <item><c>LLMSTXTKIT_CACHE_DIR</c> — File-backed cache directory (optional, enables file persistence)</item>
/// </list>
/// </remarks>
public static class ServerConfiguration
{
    /// <summary>
    /// Registers all LlmsTxtKit Core services into the DI container. Call this
    /// during server startup to make Core services available for injection into
    /// MCP tool methods.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddLlmsTxtKitServices(this IServiceCollection services)
    {
        // ---------------------------------------------------------------
        // Read configuration from environment variables
        // ---------------------------------------------------------------
        var fetcherOptions = BuildFetcherOptions();
        var cacheOptions = BuildCacheOptions();

        // ---------------------------------------------------------------
        // Register Core services as singletons
        // ---------------------------------------------------------------

        // HttpClient: shared across fetcher and content fetcher
        services.AddSingleton<HttpClient>();

        // FetcherOptions: configuration for the HTTP fetcher
        services.AddSingleton(fetcherOptions);

        // LlmsTxtFetcher: fetches llms.txt files by domain
        // Constructor signature: LlmsTxtFetcher(FetcherOptions?, HttpClient?)
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<LlmsTxtFetcher>>();
            var httpClient = sp.GetRequiredService<HttpClient>();
            var options = sp.GetRequiredService<FetcherOptions>();
            logger.LogDebug(
                "Initializing LlmsTxtFetcher with UserAgent={UserAgent}, Timeout={Timeout}s, MaxRetries={MaxRetries}",
                options.UserAgent, options.TimeoutSeconds, options.MaxRetries);
            return new LlmsTxtFetcher(options, httpClient);
        });

        // CacheOptions: configuration for the document cache
        services.AddSingleton(cacheOptions);

        // LlmsTxtCache: domain-keyed cache with TTL, LRU, stale-while-revalidate
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<LlmsTxtCache>>();
            var options = sp.GetRequiredService<CacheOptions>();
            logger.LogDebug(
                "Initializing LlmsTxtCache with TTL={Ttl}, MaxEntries={MaxEntries}, StaleWhileRevalidate={Stale}",
                options.DefaultTtl, options.MaxEntries, options.StaleWhileRevalidate);
            return new LlmsTxtCache(options);
        });

        // LlmsDocumentValidator: validation orchestrator with all built-in rules
        services.AddSingleton<LlmsDocumentValidator>();

        // IContentFetcher: content fetcher for context generation
        services.AddSingleton<IContentFetcher>(sp =>
        {
            var httpClient = sp.GetRequiredService<HttpClient>();
            return new HttpContentFetcher(httpClient);
        });

        // ContextGenerator: generates LLM-ready context from parsed documents
        services.AddSingleton(sp =>
        {
            var fetcher = sp.GetRequiredService<IContentFetcher>();
            return new ContextGenerator(fetcher);
        });

        return services;
    }

    // ---------------------------------------------------------------
    // Configuration Builders
    // ---------------------------------------------------------------

    /// <summary>
    /// Builds <see cref="FetcherOptions"/> from environment variables with
    /// sensible defaults for MCP server usage.
    /// </summary>
    private static FetcherOptions BuildFetcherOptions()
    {
        // FetcherOptions uses init-only properties, so all config must be
        // set in the object initializer. Read env vars first, then build.
        var userAgent = GetEnvString("LLMSTXTKIT_USER_AGENT", "LlmsTxtKit/0.7.0");
        var timeoutStr = Environment.GetEnvironmentVariable("LLMSTXTKIT_TIMEOUT_SECONDS");
        var retriesStr = Environment.GetEnvironmentVariable("LLMSTXTKIT_MAX_RETRIES");

        return new FetcherOptions
        {
            UserAgent = userAgent,
            TimeoutSeconds = int.TryParse(timeoutStr, out var timeout)
                ? timeout : FetcherOptions.DefaultTimeoutSeconds,
            MaxRetries = int.TryParse(retriesStr, out var retries)
                ? retries : FetcherOptions.DefaultMaxRetries
        };
    }

    /// <summary>
    /// Builds <see cref="CacheOptions"/> from environment variables. Optionally
    /// enables file-backed persistence if <c>LLMSTXTKIT_CACHE_DIR</c> is set.
    /// </summary>
    private static CacheOptions BuildCacheOptions()
    {
        var options = new CacheOptions();

        if (int.TryParse(Environment.GetEnvironmentVariable("LLMSTXTKIT_CACHE_TTL_MINUTES"), out var ttlMinutes))
            options.DefaultTtl = TimeSpan.FromMinutes(ttlMinutes);

        if (int.TryParse(Environment.GetEnvironmentVariable("LLMSTXTKIT_CACHE_MAX_ENTRIES"), out var maxEntries))
            options.MaxEntries = maxEntries;

        var cacheDir = Environment.GetEnvironmentVariable("LLMSTXTKIT_CACHE_DIR");
        if (!string.IsNullOrWhiteSpace(cacheDir))
        {
            options.BackingStore = new FileCacheBackingStore(cacheDir);
        }

        return options;
    }

    /// <summary>
    /// Reads a string environment variable with a fallback default.
    /// </summary>
    private static string GetEnvString(string key, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }
}
