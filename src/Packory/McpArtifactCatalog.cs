using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using Npgsql;

namespace Packory;

/// <summary>
/// Bounded global-catalog reads with the release gate applied before pagination.
/// The keyset is immutable (kind/name/version); cursors never grant access and
/// each page observes current approval state. This is not a historical snapshot.
/// </summary>
public static class McpArtifactCatalog
{
    public sealed record SearchPage(
        [property: JsonPropertyName("items")] IReadOnlyList<CatalogEntry> Items,
        [property: JsonPropertyName("nextCursor")] string? NextCursor);

    public sealed record VersionPage(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("versions")] IReadOnlyList<string> Versions,
        [property: JsonPropertyName("nextCursor")] string? NextCursor);

    private sealed record Cursor(int Format, string Scope, string Kind, string Name, string Version);

    public static SearchPage Search(Settings settings, string query, string? kind = null,
        int limit = 30, string? cursor = null) => Read(settings, query, kind, null, limit, cursor);

    public static VersionPage Versions(Settings settings, string kind, string name,
        int limit = 100, string? cursor = null)
    {
        if (!ValidText(name, 1024, allowEmpty: false))
            throw Invalid("Invalid artifact identity.");
        var page = Read(settings, "", kind, name, limit, cursor);
        return new(kind, name, page.Items.Select(x => x.Version).ToArray(), page.NextCursor);
    }

    private static SearchPage Read(Settings settings, string query, string? kind,
        string? exactName, int limit, string? cursor)
    {
        if (!ValidText(query, 200, allowEmpty: true) || limit is < 1 or > 100 ||
            kind is not null && !RegistryCapabilities.All.Any(x => x.Ecosystem == kind) ||
            exactName is not null && kind is null)
            throw Invalid("Invalid catalog query, ecosystem or limit (1-100).");

        var scope = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { method = exactName is null ? "search_artifacts" : "artifact_versions",
                query, kind, exactName, approvalEnforced = settings.SkillSecurityEnforced })));
        var after = Decode(cursor, scope);
        using var db = Open(settings);
        using var command = db.CreateCommand();
        command.CommandTimeout = 10;
        // Provider collation must match the same bytewise order for both WHERE
        // and ORDER BY; database locale cannot change a cursor's interpretation.
        var collation = db is NpgsqlConnection ? "COLLATE \"C\"" : "COLLATE BINARY";
        var kindColumn = "p.kind " + collation;
        var nameColumn = "p.name " + collation;
        var versionColumn = "p.version " + collation;
        var approved = settings.SkillSecurityEnforced
            ? "AND (p.kind <> 'skill' OR EXISTS (SELECT 1 FROM skill_security_revisions s " +
                "WHERE s.name=p.name AND s.version=p.version AND s.state='APPROVED'))"
            : "";
        var search = db is NpgsqlConnection ? "ILIKE" : "LIKE";
        var kinds = RegistryCapabilities.All.Select((_, index) => "@supported" + index).ToArray();
        command.CommandText = $"""
            SELECT p.kind,p.name,p.version,p.description,p.origin,p.updated_at
            FROM packages p
            WHERE p.kind IN ({string.Join(',', kinds)})
              AND (@kind IS NULL OR p.kind=@kind)
              AND (@name IS NULL OR p.name=@name)
              AND (p.name {search} @query ESCAPE '\' OR p.description {search} @query ESCAPE '\')
              {approved}
              AND (@afterKind IS NULL OR {kindColumn}>@afterKind
                OR ({kindColumn}=@afterKind AND {nameColumn}>@afterName)
                OR ({kindColumn}=@afterKind AND {nameColumn}=@afterName AND {versionColumn}>@afterVersion))
            ORDER BY {kindColumn},{nameColumn},{versionColumn}
            LIMIT @limit
            """;
        Add(command, "kind", kind);
        Add(command, "name", exactName);
        Add(command, "query", "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        Add(command, "afterKind", after?.Kind);
        Add(command, "afterName", after?.Name);
        Add(command, "afterVersion", after?.Version);
        Add(command, "limit", limit + 1, DbType.Int32);
        for (var i = 0; i < kinds.Length; i++) Add(command, "supported" + i, RegistryCapabilities.All[i].Ecosystem);
        using var reader = command.ExecuteReader();
        var items = new List<CatalogEntry>(limit + 1);
        while (reader.Read())
            items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        string? next = null;
        if (items.Count > limit)
        {
            items.RemoveAt(limit);
            var last = items[^1];
            next = Encode(new(1, scope, last.Kind, last.Name, last.Version));
        }
        return new(items, next);
    }

    private static DbConnection Open(Settings settings)
    {
        DbConnection connection = settings.MetadataProvider == "postgresql"
            ? new NpgsqlConnection(settings.PostgresConnectionString)
            : new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(Path.GetFullPath(settings.DataDirectory), "packory.sqlite"),
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ToString());
        connection.Open();
        if (connection is SqliteConnection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=5000;";
            command.ExecuteNonQuery();
        }
        return connection;
    }

    private static void Add(DbCommand command, string name, object? value,
        DbType type = DbType.String)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string Encode(Cursor cursor) =>
        WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(cursor));

    private static Cursor? Decode(string? value, string scope)
    {
        if (value is null) return null;
        if (value.Length is < 1 or > 4096) throw Invalid("Invalid catalog cursor.");
        try
        {
            var cursor = JsonSerializer.Deserialize<Cursor>(WebEncoders.Base64UrlDecode(value));
            if (cursor is null || cursor.Format != 1 || cursor.Scope != scope ||
                !RegistryCapabilities.All.Any(x => x.Ecosystem == cursor.Kind) ||
                !ValidText(cursor.Name, 1024, allowEmpty: false) ||
                !ValidText(cursor.Version, 256, allowEmpty: false) || Encode(cursor) != value)
                throw Invalid("Invalid catalog cursor.");
            return cursor;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        {
            throw Invalid("Invalid catalog cursor.");
        }
    }

    private static bool ValidText(string? value, int limit, bool allowEmpty) =>
        value is not null && value.Length <= limit && (allowEmpty || value.Length > 0) &&
        !value.Any(char.IsControl);

    private static McpProtocolException Invalid(string message) => new(message, McpErrorCode.InvalidParams);
}
