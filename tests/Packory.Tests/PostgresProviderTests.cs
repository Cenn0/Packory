using Packory;
using Npgsql;
using System.Text.Json;
using Xunit;

namespace Packory.Tests;

/// <summary>Runs against a disposable PostgreSQL service container in the dedicated CI job.</summary>
public sealed class PostgresProviderTests
{
    [Fact]
    public void ReadReplicaHealthRejectsUninitializedFutureAndIncompleteSchema()
    {
        var shared = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(shared)) return;
        var schema = "packory_health_" + Guid.NewGuid().ToString("N")[..16];
        using var administrator = new NpgsqlConnection(shared);
        administrator.Open();
        using (var create = new NpgsqlCommand("CREATE SCHEMA " + schema, administrator))
            create.ExecuteNonQuery();
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(shared) { SearchPath = schema };
            var settings = new Settings
            {
                MetadataProvider = "postgresql",
                PostgresConnectionString = builder.ConnectionString
            };
            var database = new PostgresDatabase(settings);
            Assert.False(database.IsHealthy()); // Connection works, no application schema.
            database.Initialize();
            Assert.True(database.IsHealthy());
            using var isolated = new NpgsqlConnection(builder.ConnectionString);
            isolated.Open();
            using (var version = new NpgsqlCommand(
                "UPDATE packory_schema_version SET version=99 WHERE slot=1", isolated))
                Assert.Equal(1, version.ExecuteNonQuery());
            Assert.False(database.IsHealthy()); // Fail closed on future writer schema.
            using (var version = new NpgsqlCommand(
                "UPDATE packory_schema_version SET version=10 WHERE slot=1", isolated))
                Assert.Equal(1, version.ExecuteNonQuery());
            Assert.True(database.IsHealthy());
            using (var drop = new NpgsqlCommand("DROP TABLE feeds CASCADE", isolated))
                drop.ExecuteNonQuery();
            Assert.False(database.IsHealthy()); // Marker alone cannot prove a valid migration.
        }
        finally
        {
            using var drop = new NpgsqlCommand("DROP SCHEMA " + schema + " CASCADE", administrator);
            drop.ExecuteNonQuery();
        }
    }

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
        var draftId = "test-feed-" + suffix;
        var now = DateTimeOffset.UtcNow.ToString("O");
        var draft = new FeedDefinition(draftId, "nuget", "hosted", "private",
            "Isolated test draft", false, now, now);
        Assert.True(db.TryCreateDraftFeed(draft, "integration-admin"));
        Assert.False(db.TryCreateDraftFeed(draft, "integration-admin"));
        Assert.NotNull(db.Feed(draftId));
        Assert.False(db.Feed(draftId)!.Enabled);
        Assert.Equal("private", db.Feed(draftId)!.Visibility);
        Assert.False(db.TryDeleteDraftFeed("default-nuget", "integration-admin"));
        Assert.True(db.TryDeleteDraftFeed(draftId, "integration-admin"));
        Assert.Null(db.Feed(draftId));
        Assert.False(db.TryDeleteDraftFeed(draftId, "integration-admin"));

        var nuget = "pgtest." + suffix;
        var expectedNuget = Convert.ToBase64String(
            System.Security.Cryptography.SHA512.HashData("pg-immutable"u8.ToArray()));
        Assert.True(db.PublishNuget(nuget, "1.0.0", "test", "integration-writer",
            expectedNuget, "pg-immutable"u8.Length));
        Assert.False(db.PublishNuget(nuget, "1.0.0", "test", "integration-writer"));
        Assert.Equal(expectedNuget, db.GetNugetDigest(nuget, "1.0.0")?.Sha512);
        Assert.Equal("hosted", db.GetNugetDigest(nuget, "1.0.0")?.Origin);
        Assert.Throws<InvalidOperationException>(() => db.UpsertNugetMirror(
            nuget, "1.0.0", "wrong source", expectedNuget, "pg-immutable"u8.Length));
        var mirrorName = "pgmirror" + suffix;
        db.UpsertNugetMirror(mirrorName, "1.0.0", "mirror", expectedNuget,
            "pg-immutable"u8.Length);
        Assert.Equal("mirror", db.PackageOrigin("nuget", mirrorName, "1.0.0"));
        Assert.Equal(expectedNuget, db.GetNugetDigest(mirrorName, "1.0.0")?.Sha512);
        Assert.Throws<InvalidDataException>(() => db.UpsertNugetMirror(
            mirrorName, "1.0.0", "altered", Convert.ToBase64String(
                System.Security.Cryptography.SHA512.HashData("other-bytes"u8.ToArray())),
            "other-bytes"u8.Length));
        Assert.Equal("hosted", db.PackageOrigin("nuget", nuget, "1.0.0"));
        db.UpsertPackage("nuget", nuget, "1.0.0", "do-not-overwrite", "mirror");
        Assert.Equal("hosted", db.PackageOrigin("nuget", nuget, "1.0.0"));
        Assert.Contains("1.0.0", db.Versions("nuget", nuget));
        Assert.Single(db.Search(nuget, "nuget", 50));

        var feedId = "isolated-" + suffix;
        var created = DateTimeOffset.UtcNow.ToString("O");
        Assert.True(db.TryCreateDraftFeed(new FeedDefinition(feedId, "nuget", "hosted",
            "private", "Synthetic isolated NuGet", false, created, created), "integration-admin"));
        Assert.True(db.TryActivateHostedNugetFeed(feedId, "integration-admin"));
        var scopedDigest = Convert.ToBase64String(
            System.Security.Cryptography.SHA512.HashData("feed-only"u8.ToArray()));
        Assert.True(db.PublishFeedNuget(feedId, nuget, "1.0.0", "only-in-feed",
            scopedDigest, "feed-only"u8.Length, "integration-writer"));
        Assert.False(db.PublishFeedNuget(feedId, nuget, "1.0.0", "overwrite",
            scopedDigest, "feed-only"u8.Length, "integration-writer"));
        Assert.Single(db.FeedNugetVersions(feedId, nuget));
        Assert.Equal(scopedDigest, db.GetFeedNugetRevision(feedId, nuget, "1.0.0")?.Sha512);
        Assert.Null(db.GetFeedNugetRevision("default-nuget", nuget, "1.0.0"));
        Assert.True(db.TryDeactivateHostedNugetFeed(feedId, "integration-admin"));
        Assert.False(db.PublishFeedNuget(feedId, "disabled", "1.0.0", "deny",
            scopedDigest, "feed-only"u8.Length, "integration-writer"));
        Assert.False(db.TryDeleteDraftFeed(feedId, "integration-admin"));
        Assert.True(db.TryActivateHostedNugetFeed(feedId, "integration-admin"));
        Assert.Equal(scopedDigest, db.GetFeedNugetRevision(feedId, nuget, "1.0.0")?.Sha512);

        // Shared E0 metadata contract: two distinct private Raw feeds can
        // publish the same coordinates with separate immutable digests.
        var firstRawFeed = "raw-alpha-" + suffix;
        var secondRawFeed = "raw-beta-" + suffix;
        foreach (var id in new[] { firstRawFeed, secondRawFeed })
        {
            Assert.True(db.TryCreateDraftFeed(new FeedDefinition(id, "raw", "hosted",
                "private", "Synthetic Raw " + id, false, created, created),
                "integration-admin"));
            Assert.True(db.TrySetHostedFeedEnabled(id, "raw", true, "integration-admin"));
        }
        var rawName = "pkg-" + suffix;
        var rawPath = FeedAssetEntry.RawPath(rawName, "1.0.0");
        var alphaSha = new string('a', 64);
        var betaSha = new string('b', 64);
        FeedAssetEntry RawEntry(string id, string digest) =>
            new(id, "raw", rawPath, rawName, "1.0.0",
                "application/octet-stream", digest, 42, created);
        Assert.True(db.PublishFeedAsset(RawEntry(firstRawFeed, alphaSha), "integration-writer"));
        Assert.True(db.PublishFeedAsset(RawEntry(secondRawFeed, betaSha), "integration-writer"));
        Assert.False(db.PublishFeedAsset(RawEntry(firstRawFeed, betaSha), "integration-writer"));
        Assert.Equal(alphaSha, db.GetFeedAsset(firstRawFeed, "raw", rawPath)?.Sha256);
        Assert.Equal(betaSha, db.GetFeedAsset(secondRawFeed, "raw", rawPath)?.Sha256);
        Assert.Equal(2, db.FeedAssets().Count(x => x.Name == rawName));
        Assert.True(db.TrySetHostedFeedEnabled(firstRawFeed, "raw", false, "integration-admin"));
        Assert.False(db.PublishFeedAsset(RawEntry(firstRawFeed, betaSha), "integration-writer"));
        Assert.False(db.TryDeleteDraftFeed(firstRawFeed, "integration-admin"));
        Assert.Null(db.PackageOrigin("raw", rawName, "1.0.0"));

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
        Assert.True(db.SetCargoYanked(cargo, "1.0.0", true, "integration-writer"));
        using (var versionRecord = JsonDocument.Parse(db.CargoIndex(cargo).Single()))
            Assert.True(versionRecord.RootElement.GetProperty("yanked").GetBoolean());
        Assert.True(db.SetCargoYanked(cargo, "1.0.0", false, "integration-writer"));
        using (var versionRecord = JsonDocument.Parse(db.CargoIndex(cargo).Single()))
            Assert.False(versionRecord.RootElement.GetProperty("yanked").GetBoolean());
        Assert.False(db.SetCargoYanked(cargo, "9.0.0", true, "integration-writer"));

        var project = "py-" + suffix;
        Assert.True(db.PublishPyFile(new PyFileEntry(project, "1.0.0",
            project + "-1.0.0-py3-none-any.whl", new string('a', 64), 128),
            "test", "integration-writer"));
        Assert.Single(db.PyFiles(project));
        Assert.Contains(project, db.PyProjects());

        var mavenPath = "org/example/pg-" + suffix + "/1.0/pg-" + suffix + "-1.0.pom";
        var maven = new MavenAssetEntry(mavenPath, "org.example", "pg-" + suffix,
            "1.0", new string('e', 64), 128, "application/xml",
            DateTimeOffset.UtcNow.ToString("O"));
        Assert.True(db.PublishMavenAsset(maven, "integration-writer"));
        Assert.False(db.PublishMavenAsset(maven, "integration-writer"));
        Assert.Equal(maven, db.GetMavenAsset(mavenPath));
        Assert.Single(db.MavenVersions("org.example", "pg-" + suffix));
        Assert.Contains(db.MavenAssets(), x => x.Path == mavenPath);
        Assert.Equal("hosted", db.PackageOrigin("maven", "org.example:pg-" + suffix, "1.0"));

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

        var tokenId = Guid.NewGuid().ToString("N");
        var digestHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        db.CreateManagedToken(new ManagedTokenRecord(tokenId, digestHash, "write",
            "nuget:pg-private.*", DateTimeOffset.UtcNow.AddDays(1).ToString("O")));
        Assert.Equal(tokenId, db.FindManagedToken(digestHash)?.Id);
        Assert.True(db.RevokeManagedToken(tokenId));
        Assert.Null(db.FindManagedToken(digestHash));
        Assert.False(db.RevokeManagedToken(tokenId));

        var publicationKey = NuGetEndpoints.NugetKey("pg-journal-" + suffix, "2.0.0");
        db.BeginPublication(publicationKey, "nuget", "pg-journal-" + suffix, "2.0.0");
        Assert.Contains(db.PendingPublications(), x => x.Key == publicationKey);
        db.CompletePublication(publicationKey);
        Assert.DoesNotContain(db.PendingPublications(), x => x.Key == publicationKey);

        var audits = db.AuditEvents(null, 100);
        Assert.Contains(audits, x => x.Kind == "feed" && x.Name == draftId && x.Action == "feed-draft-create");
        Assert.Contains(audits, x => x.Kind == "feed" && x.Name == draftId && x.Action == "feed-draft-delete");
        Assert.Contains(audits, x => x.Kind == "nuget" && x.Name == nuget);
        Assert.Contains(audits, x => x.Kind == "npm" && x.Name == npm);
        Assert.Contains(audits, x => x.Kind == "oci" && x.Name == repo);
    }
}
