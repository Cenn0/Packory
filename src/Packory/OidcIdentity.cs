using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Packory;

// OIDC is optional and never a prerequisite for native package clients.
public sealed class OidcOptions
{
    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string[] AdminSubjects { get; set; }=[];
    public string[] ReaderSubjects { get; set; }=[];
    public bool Enabled => !string.IsNullOrWhiteSpace(Authority);

    public void Validate()
    {
        if(!Enabled)return;
        if(!Uri.TryCreate(Authority,UriKind.Absolute,out var uri)||uri.Scheme!="https"||
           !string.IsNullOrEmpty(uri.UserInfo)||!string.IsNullOrEmpty(uri.Query)||
           !string.IsNullOrEmpty(uri.Fragment)||string.IsNullOrWhiteSpace(ClientId)||
           string.IsNullOrWhiteSpace(ClientSecret)||ClientSecret.Length<16)
            throw new InvalidOperationException("OIDC requires an HTTPS authority, client ID and client secret");
    }
}

public static class OidcIdentity
{
    public static OidcOptions Configure(WebApplicationBuilder builder,Settings settings)
    {
        var options=new OidcOptions();
        builder.Configuration.GetSection("Packory:Oidc").Bind(options);
        options.Authority=Environment.GetEnvironmentVariable("PACKORY_OIDC_AUTHORITY")??options.Authority;
        options.ClientId=Environment.GetEnvironmentVariable("PACKORY_OIDC_CLIENT_ID")??options.ClientId;
        options.ClientSecret=Environment.GetEnvironmentVariable("PACKORY_OIDC_CLIENT_SECRET")??options.ClientSecret;
        if(Environment.GetEnvironmentVariable("PACKORY_OIDC_ADMIN_SUBJECTS") is { } admins)
            options.AdminSubjects=admins.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        if(Environment.GetEnvironmentVariable("PACKORY_OIDC_READER_SUBJECTS") is { } readers)
            options.ReaderSubjects=readers.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        options.Validate();
        if(!options.Enabled)return options;
        var keyring=Path.Combine(Path.GetFullPath(settings.DataDirectory),"keyring");
        Directory.CreateDirectory(keyring);
        builder.Services.AddDataProtection().SetApplicationName("Packory.V2")
            .PersistKeysToFileSystem(new DirectoryInfo(keyring));
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookie=>
            {
                cookie.Cookie.Name="__Host-PackorySession";
                cookie.Cookie.Path="/";
                cookie.Cookie.HttpOnly=true;
                cookie.Cookie.SameSite=SameSiteMode.Lax;
                cookie.Cookie.SecurePolicy=CookieSecurePolicy.Always;
                cookie.ExpireTimeSpan=TimeSpan.FromHours(8);
                cookie.SlidingExpiration=false;
                cookie.LoginPath="/auth/login";
                cookie.Events.OnRedirectToLogin=ctx=>
                {
                    ctx.Response.StatusCode=401;return Task.CompletedTask;
                };
            })
            .AddOpenIdConnect("oidc",oidc=>
            {
                oidc.Authority=options.Authority;
                oidc.ClientId=options.ClientId;
                oidc.ClientSecret=options.ClientSecret;
                oidc.ResponseType=OpenIdConnectResponseType.Code;
                oidc.UsePkce=true;
                oidc.SaveTokens=false;
                oidc.GetClaimsFromUserInfoEndpoint=true;
                oidc.RequireHttpsMetadata=true;
                oidc.MapInboundClaims=false;
                oidc.TokenValidationParameters.NameClaimType="sub";
            });
        builder.Services.AddAntiforgery(x=>x.HeaderName="X-Packory-CSRF");
        return options;
    }

    public static void Use(WebApplication app,OidcOptions config)
    {
        if(!config.Enabled)return;
        app.UseAuthentication();
        // Cookie-authenticated writes require a same-origin antiforgery token;
        // Bearer-authenticated native clients are not subject to browser CSRF.
        app.Use(async (ctx,next)=>
        {
            var unsafeRequest=ctx.Request.Method is not ("GET" or "HEAD" or "OPTIONS" or "TRACE");
            var cookie=ctx.User.Identity?.IsAuthenticated==true &&
                string.IsNullOrEmpty(ctx.Request.Headers.Authorization.ToString());
            if(unsafeRequest && cookie && !ctx.Request.Path.StartsWithSegments("/signin-oidc"))
            {
                var anti=ctx.RequestServices.GetRequiredService<IAntiforgery>();
                if(!await anti.IsRequestValidAsync(ctx))
                {
                    ctx.Response.StatusCode=StatusCodes.Status400BadRequest;
                    return;
                }
            }
            await next(ctx);
        });
        app.MapGet("/auth/login",()=>Results.Challenge(authenticationSchemes:["oidc"]));
        app.MapPost("/auth/logout",async (HttpContext ctx)=>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });
        app.MapGet("/api/v1/session",(HttpContext ctx,IAntiforgery anti)=>
        {
            if(ctx.User.Identity?.IsAuthenticated!=true)return Results.Unauthorized();
            var tokens=anti.GetAndStoreTokens(ctx);
            return Results.Ok(new {authenticated=true,csrf=tokens.RequestToken});
        });
    }

    public static bool HasCookiePermission(HttpContext ctx,OidcOptions options,string capability)
    {
        if(!options.Enabled||ctx.User.Identity?.IsAuthenticated!=true)return false;
        var sub=ctx.User.FindFirstValue("sub");
        if(string.IsNullOrWhiteSpace(sub))return false;
        if(options.AdminSubjects.Contains(sub,StringComparer.Ordinal))return true;
        if(capability is "catalog.read" or "repository.read")
            return options.ReaderSubjects.Contains(sub,StringComparer.Ordinal);
        return false;
    }
}
