using System.Security.Cryptography;
using System.Text;

namespace Packory;

public static class Access
{
    public static IResult? Require(HttpContext context, Settings settings, bool write = false)
    {
        if (!write && settings.AnonymousRead) return null;
        var auth = context.Request.Headers.Authorization.ToString();
        string? token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..] : null;
        if (string.IsNullOrWhiteSpace(token) && auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            // NuGet.Config credentials may carry a token as the Basic password.
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..]));
                var delimiter = decoded.IndexOf(':');
                if (delimiter >= 0) token = decoded[(delimiter + 1)..];
            }
            catch (FormatException) { }
        }
        if (string.IsNullOrWhiteSpace(token)) token = context.Request.Headers["X-NuGet-ApiKey"].ToString();
        if (string.IsNullOrWhiteSpace(token)) return Results.Unauthorized();
        if (Matches(token, settings.AdminToken)) return null;
        if (write ? Matches(token, settings.WriteToken) : Matches(token, settings.ReadToken) || Matches(token, settings.WriteToken)) return null;
        return Results.Json(new { error = "Token lacks required permissions" }, statusCode: StatusCodes.Status403Forbidden);
    }

    private static bool Matches(string provided, string? expected)
    {
        if (expected is null) return false;
        // Hash both tokens first so comparison time does not depend on secret length.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }
}
