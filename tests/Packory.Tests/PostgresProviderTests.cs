using Packory;
using Xunit;

namespace Packory.Tests;

/// <summary>Runs against a disposable PostgreSQL service container in the dedicated CI job.</summary>
public sealed class PostgresProviderTests
{
    [Fact]
    public void PersistsAllRegistryKindsAndTransactionalAudit()
    {
        var connection = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var settings = new Settings
        {
            MetadataProvider = "postgresql",
            PostgresConnectionString = connection
        };
        settings.Validate();
        IPackageDatabase db = new PostgresDatabase(settings);
        db.Initialize();
        Assert.True(db.IsHealthy());
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var nuget = "pgtest." + suffix;
        Assert.True(db.PublishNuget(nuget, "1.0.0", "test", "integration-writer"));
        Assert.False(db.PublishNuget(nuget, "1.0.0", "test", "integration-writer"));
        Assert.Equal("hosted", db.PackageOrigin("nuget", nuget, "1.0.0"));
        db.UpsertPackage("nuget", nuget, "1.0.0", "do-not-overwrite", "mirror");
        Assert.Equal("hosted", db.PackageOrigin("nuget", nuget, "1.0.0"));
        Assert.Contains("1.0.0", db.Versions("nuget", nuget));
        Assert.Single(db.Search(nuget, "nuget", 50));

        var npm = "pg-" + suffix;
        Assert.True(db.PutNpmVersion(npm, "1.0.0", "{\"name\":\"test\"}",
            "test", new Dictionary<string, string> { ["latest"] = "1.0.0" }, "integration-writer"));
        Assert.Single(db.NpmHosted(npm).Versions);
        Assert.Equal("1.0.0", db.NpmHosted(npm).Tags["latest"]);

        var skill = "test-" + suffix;
        Assert.True(db.AddSkill(skill, "1.0.0", "test", "integration-writer"));
        Assert.Single(db.Skills(skill));

        var cargo = "pgcargo" + suffix;
        Assert.True(db.PublishCargo(cargo, "1.0.0", "{\"vers\":\"1.0.0\"}", "test", "integration-writer"));
        Assert.Single(db.CargoIndex(cargo));

        var project = "py-" + suffix;
        Assert.True(db.PublishPyFile(new PyFileEntry(project, "1.0.0",
            project + "-1.0.0-py3-none-any.whl", new string('a', 64), 128),
            "test", "integration-writer"));
        Assert.Single(db.PyFiles(project));
        Assert.Contains(project, db.PyProjects());

        var repo = "test/" + suffix;
        var digest = "sha256:" + new string('b', 64);
        db.OciAddBlob(repo, digest);
        Assert.True(db.OciHasBlob(repo, digest));
        Assert.False(db.OciHasBlob("other/repo", digest));
        Assert.True(db.OciPutManifest(repo, "latest",
            new OciManifestEntry(digest, "application/vnd.oci.image.manifest.v1+json", 512),
            null, null, "integration-writer"));
        Assert.NotNull(db.OciManifest(repo, "latest"));
        Assert.Contains("latest", db.OciTags(repo));
        Assert.Contains(digest, db.OciAllManifestDigests());

        var audits = db.AuditEvents(null, 100);
        Assert.Contains(audits, x => x.Kind == "nuget" && x.Name == nuget);
        Assert.Contains(audits, x => x.Kind == "npm" && x.Name == npm);
        Assert.Contains(audits, x => x.Kind == "oci" && x.Name == repo);
    }
}
