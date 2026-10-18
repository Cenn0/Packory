using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Packory;

/// <summary>
/// Optional evidence tools. Registration requires explicit operator opt-in;
/// tool methods separately enforce the dedicated OIDC validation permission.
/// </summary>
[McpServerToolType]
public sealed class PackoryValidationMcpTools
{
    [McpServerTool(Name = "list_validation_runs", ReadOnly = true)]
    [Description("List only operator-approved, unexpired, independently signed sanitized offline validation exports. No test execution.")]
    public static object ListValidationRuns(IHttpContextAccessor contextAccessor,
        Settings settings, ValidationEvidenceStore evidence, ValidationMcpQuota quota,
        ILogger<PackoryValidationMcpTools> audit)
    {
        Authorize(contextAccessor, settings, quota, audit);
        var items = evidence.List();
        audit.LogInformation("validation-list approved_count={Count}", items.Count);
        return new { items };
    }

    [McpServerTool(Name = "get_validation_findings", ReadOnly = true)]
    [Description("Read bounded, signed, operator-approved validation findings by opaque alias; signatures authenticate external attestations, not Packory test execution.")]
    public static object GetValidationFindings(string alias, IHttpContextAccessor contextAccessor,
        Settings settings, ValidationEvidenceStore evidence, ValidationMcpQuota quota,
        ILogger<PackoryValidationMcpTools> audit)
    {
        Authorize(contextAccessor, settings, quota, audit);
        var run = evidence.Get(alias);
        // Never audit a user-controlled raw alias; errors reveal no existence.
        audit.LogInformation("validation-get decision={Decision}", run is null ? "unavailable" : "approved");
        return run is null ? new { error = "unavailable" } as object : new { run };
    }

    private static void Authorize(IHttpContextAccessor accessor,
        Settings settings, ValidationMcpQuota quota,
        ILogger<PackoryValidationMcpTools> audit)
    {
        if (!Access.HasValidationMcpAccess(accessor.HttpContext, settings))
        {
            audit.LogWarning("validation-tool denied actor_class=not_validation_authorized");
            throw new UnauthorizedAccessException("validation_unauthorized");
        }
        if (!quota.TryAcquire())
        {
            audit.LogWarning("validation-tool denied reason=rate_limited");
            throw new InvalidOperationException("validation_rate_limited");
        }
    }
}
