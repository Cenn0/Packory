using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Packory;

[McpServerToolType]
public sealed class PackorySkillQualityTools
{
    [McpServerTool(Name = "assess_skill_quality", ReadOnly = true)]
    [Description("Assess one published Skill's immutable bytes with deterministic format, documentation and local-reference checks. Returns bounded findings and suggestions with digests; never executes code, checks external sites, certifies safety or scores behavioral quality.")]
    public static object AssessSkillQuality(string name, string version, IPackageDatabase db,
        BlobStore blobs, SkillSecurityStore security, Settings settings,
        CancellationToken cancellationToken = default)
    {
        var report = SkillQualityAssessment.AssessPublished(name, version, db, blobs, security, settings,
            cancellationToken);
        return report is null ? new { status = "unavailable" } as object : report;
    }
}
