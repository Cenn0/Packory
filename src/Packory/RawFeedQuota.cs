using System.Data.Common;

namespace Packory;

public sealed record RawFeedUsage(long Artifacts, long Bytes);

/// <summary>Logical committed-asset quota for a supported single writer.</summary>
public static class RawFeedQuota
{
    public static bool Admit(RawFeedUsage usage, long incoming, Settings settings) =>
        usage.Artifacts >= 0 && usage.Bytes >= 0 && incoming > 0 &&
        (settings.MaxRawFeedArtifacts == 0 || usage.Artifacts < settings.MaxRawFeedArtifacts) &&
        (settings.MaxRawFeedBytes == 0 || usage.Bytes <= settings.MaxRawFeedBytes &&
            incoming <= settings.MaxRawFeedBytes - usage.Bytes);

    internal static RawFeedUsage Read(DbConnection db, string feedId)
    {
        if (!FeedDefinition.ValidId(feedId)) throw new ArgumentException("Invalid Raw feed identity.");
        using var query = db.CreateCommand();
        query.CommandText = "SELECT COUNT(*),COALESCE(SUM(size),0),COALESCE(MIN(size),0) " +
            "FROM feed_assets WHERE feed_id=@feed AND kind='raw'";
        var parameter = query.CreateParameter();
        parameter.ParameterName = "feed";
        parameter.Value = feedId;
        query.Parameters.Add(parameter);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("Raw feed accounting unavailable.");
        var usage = new RawFeedUsage(Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)));
        if (usage.Artifacts < 0 || usage.Bytes < 0 || usage.Artifacts > 0 && Convert.ToInt64(reader.GetValue(2)) <= 0)
            throw new InvalidDataException("Invalid Raw feed accounting.");
        return usage;
    }
}
