// LlmsTxtKit.Mcp — MCP Server Entry Point
//
// This is the main entry point for the LlmsTxtKit MCP server process.
// It configures dependency injection, registers MCP tools, sets up the
// stdio transport layer, and starts listening for tool invocations from
// AI agents (Claude Desktop, GitHub Copilot, etc.).
//
// Architecture overview:
//   - The server uses the official C# MCP SDK (ModelContextProtocol NuGet package)
//     with Microsoft.Extensions.Hosting for DI and lifecycle management.
//   - Transport: stdio (standard input/output) — the most common transport for
//     local MCP servers. JSON-RPC messages are read from stdin and written to stdout.
//   - Logging goes to stderr so it doesn't interfere with the JSON-RPC protocol.
//   - Core services (LlmsTxtFetcher, LlmsTxtCache, LlmsDocumentValidator,
//     ContextGenerator) are registered as singletons and injected into tool methods.
//   - Tools are discovered by scanning the assembly for [McpServerToolType] classes.
//
// Configuration:
//   Environment variables (all optional, sensible defaults provided):
//     LLMSTXTKIT_USER_AGENT        — Custom User-Agent string
//     LLMSTXTKIT_TIMEOUT_SECONDS   — HTTP fetch timeout (default: 15)
//     LLMSTXTKIT_MAX_RETRIES       — Fetch retry count (default: 2)
//     LLMSTXTKIT_CACHE_TTL_MINUTES — Cache TTL in minutes (default: 60)
//     LLMSTXTKIT_CACHE_MAX_ENTRIES — Maximum cached domains (default: 1000)
//     LLMSTXTKIT_CACHE_DIR         — File-backed cache directory (optional)
//
// For the full design rationale, see specs/design-spec.md.
// For the MCP tool definitions, see the Tools/ directory.

using LlmsTxtKit.Mcp.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace LlmsTxtKit.Mcp;

/// <summary>
/// Entry point for the LlmsTxtKit MCP server. Configures the host builder
/// with DI services, MCP server infrastructure, and stdio transport.
/// </summary>
public class Program
{
    /// <summary>
    /// Starts the MCP server with configured tools and transport.
    /// </summary>
    /// <param name="args">Command-line arguments (currently unused, reserved for future config overrides).</param>
    public static async Task Main(string[] args)
    {
        // ---------------------------------------------------------------
        // Build the application host
        // ---------------------------------------------------------------
        var builder = Host.CreateApplicationBuilder(args);

        // Configure logging to stderr (MCP convention: stdout is for JSON-RPC,
        // stderr is for diagnostic output). Set minimum level to Debug for
        // comprehensive diagnostic output during development.
        builder.Logging.AddConsole(consoleLogOptions =>
        {
            // Route ALL log output to stderr so it doesn't corrupt the
            // JSON-RPC transport on stdout. This is critical for MCP servers.
            consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        // ---------------------------------------------------------------
        // Register LlmsTxtKit Core services (fetcher, cache, validator, etc.)
        // ---------------------------------------------------------------
        builder.Services.AddLlmsTxtKitServices();

        // ---------------------------------------------------------------
        // Configure MCP server with stdio transport and auto-discovered tools
        // ---------------------------------------------------------------
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();  // Scans for [McpServerToolType] classes

        // ---------------------------------------------------------------
        // Start the server
        // ---------------------------------------------------------------
        await builder.Build().RunAsync();
    }
}
