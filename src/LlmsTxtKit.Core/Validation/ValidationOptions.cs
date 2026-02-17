namespace LlmsTxtKit.Core.Validation;

/// <summary>
/// Configuration options for <see cref="LlmsDocumentValidator"/>. Controls
/// which optional validation checks are performed.
/// </summary>
/// <remarks>
/// <para>
/// By default, only structural validation rules are enabled (those that inspect
/// the parsed document in memory). Network-dependent checks (URL reachability
/// and content freshness) are disabled by default because they are slow and
/// may produce false positives due to transient network issues.
/// </para>
/// <para>
/// Enable <see cref="CheckLinkedUrls"/> for CI/CD pipelines where you want to
/// verify that all linked resources are accessible. Enable <see cref="CheckFreshness"/>
/// when you need to detect stale llms.txt files that haven't been updated
/// to reflect changes in the linked content.
/// </para>
/// </remarks>
public sealed class ValidationOptions
{
    /// <summary>
    /// Whether to send HTTP HEAD requests to each linked URL to verify it
    /// returns a 200 response. When enabled, unreachable and redirected URLs
    /// produce <see cref="ValidationSeverity.Warning"/> issues.
    /// </summary>
    /// <value>Defaults to <c>false</c> (offline validation only).</value>
    public bool CheckLinkedUrls { get; init; } = false;

    /// <summary>
    /// Whether to compare the <c>Last-Modified</c> header of the llms.txt file
    /// against the <c>Last-Modified</c> headers of linked pages. When enabled,
    /// an llms.txt file that is older than the pages it links to produces a
    /// <see cref="ValidationSeverity.Warning"/>.
    /// </summary>
    /// <value>Defaults to <c>false</c>.</value>
    public bool CheckFreshness { get; init; } = false;

    /// <summary>
    /// Timeout in seconds for each individual URL check when
    /// <see cref="CheckLinkedUrls"/> or <see cref="CheckFreshness"/> is enabled.
    /// </summary>
    /// <value>Defaults to 10 seconds.</value>
    public int UrlCheckTimeout { get; init; } = 10;
}
