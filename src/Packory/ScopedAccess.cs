using System.Security.Cryptography;
using System.Text;

namespace Packory;

public static class ScopedAccess
{
    public static IResult? Require(HttpContext ctx,Settings settings,V2Catalog catalog,
        string? feed,string capability,bool publicRead=false)
    {
        if(publicRead && capability=="repository.read")return null;
        var token=Extract(ctx);
        if(string.IsNullOrWhiteSpace(token))return Results.Unauthorized();
        if(Match(token,settings.AdminToken))return null;
        var read=capability is "repository.read" or "catalog.read";
        if(read && (Match(token,settings.ReadToken)||Match(token,settings.WriteToken)))return null;
        if(capability=="artifact.publish"&&Match(token,settings.WriteToken))return null;
        var principal=catalog.ResolveToken(token);
        if(principal is null)return Results.Json(new {error="Invalid or expired token"},statusCode:403);
        if(principal.Value.Feed is not null && principal.Value.Feed!=feed)return Results.Json(new {error="Token is not valid for this feed"},statusCode:403);
        var scopes=principal.Value.Scopes.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        if(!scopes.Contains(capability,StringComparer.Ordinal))return Results.Json(new {error="Insufficient scope"},statusCode:403);
        return null;
    }

    public static string Actor(HttpContext context, Settings settings, V2Catalog catalog)
    {
        var token=Extract(context);
        if(Match(token,settings.AdminToken))return "bootstrap-admin";
        if(Match(token,settings.WriteToken))return "legacy-writer";
        if(Match(token,settings.ReadToken))return "legacy-reader";
        return token is null?"anonymous":catalog.ResolveToken(token)?.Id??"unverified";
    }

    private static bool Match(string? provided,string? expected)
    {
        if(provided is null||expected is null)return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)),SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }

    private static string? Extract(HttpContext ctx)
    {
        var auth=ctx.Request.Headers.Authorization.ToString();
        if(auth.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))return auth[7..].Trim();
        if(auth.StartsWith("Basic ",StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded=Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..]));
                var sep=decoded.IndexOf(':');if(sep>=0)return decoded[(sep+1)..];
            }
            catch(FormatException){ }
        }
        var key=ctx.Request.Headers["X-NuGet-ApiKey"].ToString();
        return string.IsNullOrWhiteSpace(key)?null:key;
    }
}
