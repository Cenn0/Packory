using System.Text.RegularExpressions;

namespace Packory;

public sealed record CreateFeedRequest(string Slug,string Protocol,string Mode,string Visibility,string? Upstream);
public sealed record VisibilityRequest(string Visibility);
public sealed record TokenRequest(string Name,string? Feed,string[] Scopes,DateTimeOffset? ExpiresAt);
public sealed record StateRequest(string State);

public static class ControlPlaneEndpoints
{
    private static readonly Regex SlugRule=new("^[a-z0-9](?:[a-z0-9-]{0,62})$",RegexOptions.Compiled|RegexOptions.CultureInvariant);
    private static readonly string[] Scopes=["catalog.read","repository.read","artifact.publish","artifact.yank","repository.manage","policy.manage","mcp.publish","system.admin"];
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/repositories", (HttpContext ctx,Settings s,V2Catalog db) =>
        {
            if(ScopedAccess.Require(ctx,s,db,null,"catalog.read") is { } denied)return denied;
            return Results.Ok(new {items=db.Feeds()});
        });
        app.MapGet("/api/v1/repositories/{slug}",(string slug,HttpContext ctx,Settings s,V2Catalog db)=>
        {
            var feed=db.FeedBySlug(slug);if(feed is null)return Results.NotFound();
            if(ScopedAccess.Require(ctx,s,db,slug,"repository.read",feed.Visibility=="public") is { } denied)return denied;
            return Results.Ok(feed);
        });
        app.MapPost("/api/v1/repositories",(CreateFeedRequest input,HttpContext ctx,Settings s,V2Catalog db)=>
        {
            if(ScopedAccess.Require(ctx,s,db,null,"repository.manage") is { } denied)return denied;
            if(!SlugRule.IsMatch(input.Slug??"") || input.Protocol is not ("nuget" or "npm" or "raw" or "skills" or "mcp") ||
               input.Mode is not ("hosted" or "proxy") || input.Visibility is not ("private" or "public"))
                return Results.BadRequest(new {error="Invalid repository configuration"});
            if(input.Mode=="proxy" && (input.Protocol is not ("nuget" or "npm") ||
                !OriginRules.Allowed(input.Upstream,s,input.Protocol)))
                return Results.BadRequest(new {error="Upstream is not an approved HTTPS origin"});
            if(input.Mode=="hosted" && input.Upstream is not null)return Results.BadRequest(new {error="Hosted feeds cannot specify an upstream"});
            if(!db.CreateFeed(input.Slug!,input.Protocol,input.Mode,input.Visibility,input.Upstream))return Results.Conflict();
            db.Audit(ScopedAccess.Actor(ctx,s,db),"repository.create",input.Slug,input.Protocol+":"+input.Mode);
            return Results.Created("/api/v1/repositories/"+input.Slug,db.FeedBySlug(input.Slug));
        });
        app.MapPatch("/api/v1/repositories/{slug}/visibility",(string slug,VisibilityRequest request,HttpContext ctx,Settings s,V2Catalog db)=>
        {
            if(ScopedAccess.Require(ctx,s,db,null,"repository.manage") is { } denied)return denied;
            if(request.Visibility is not ("private" or "public"))return Results.BadRequest();
            if(!db.SetVisibility(slug,request.Visibility))return Results.NotFound();
            db.Audit(ScopedAccess.Actor(ctx,s,db),"repository.visibility",slug,request.Visibility);
            return Results.Ok(db.FeedBySlug(slug));
        });
        app.MapGet("/api/v1/catalog",(string? q,string? kind,string? feed,int? limit,HttpContext ctx,Settings s,V2Catalog db)=>
        {
            if(feed is not null)
            {
                var repo=db.FeedBySlug(feed);if(repo is null)return Results.NotFound();
                if(ScopedAccess.Require(ctx,s,db,feed,"repository.read",repo.Visibility=="public") is { } denied)return denied;
            }
            else if(ScopedAccess.Require(ctx,s,db,null,"catalog.read") is { } denied)return denied;
            return Results.Ok(new {items=db.Search(q,kind,limit??100).Where(x=>feed is null||x.Feed==feed)});
        });
        app.MapPost("/api/v1/tokens",(TokenRequest request,HttpContext ctx,Settings s,V2Catalog db)=>
        {
            if(ScopedAccess.Require(ctx,s,db,null,"system.admin") is { } denied)return denied;
            if(string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>100||request.Scopes is null||request.Scopes.Length==0||
                request.Scopes.Any(x=>!Scopes.Contains(x,StringComparer.Ordinal))||
                request.ExpiresAt is { } expiry && (expiry<=DateTimeOffset.UtcNow||expiry>DateTimeOffset.UtcNow.AddYears(2)) ||
                request.Feed is not null && db.FeedBySlug(request.Feed) is null)
                return Results.BadRequest(new {error="Invalid name, scope, feed or expiry"});
            var token=db.CreateToken(request.Name,request.Feed,string.Join(' ',request.Scopes.Distinct()),request.ExpiresAt);
            db.Audit(ScopedAccess.Actor(ctx,s,db),"token.create",token.Id,"Secret not recorded");
            return Results.Created("/api/v1/tokens/"+token.Id,new {id=token.Id,secret=token.Secret});
        });
        app.MapDelete("/api/v1/tokens/{id}",(string id,HttpContext ctx,Settings s,V2Catalog db)=>
        {
            if(ScopedAccess.Require(ctx,s,db,null,"system.admin") is { } denied)return denied;
            if(!db.RevokeToken(id))return Results.NotFound();
            db.Audit(ScopedAccess.Actor(ctx,s,db),"token.revoke",id);return Results.NoContent();
        });
        app.MapGet("/api/v1/audit",(int? limit,HttpContext ctx,Settings s,V2Catalog db)=>
        {
            if(ScopedAccess.Require(ctx,s,db,null,"system.admin") is { } denied)return denied;
            return Results.Ok(new {items=db.AuditEntries(limit??100)});
        });
        app.MapGet("/api/v1/jobs",(int? limit,HttpContext ctx,Settings s,V2Catalog db)=>
        {
            if(ScopedAccess.Require(ctx,s,db,null,"system.admin") is { } denied)return denied;
            return Results.Ok(new {items=db.Jobs(limit??100)});
        });
        app.MapPost("/api/v1/maintenance/reconcile",(HttpContext ctx,Settings s,V2Catalog db)=>
        {
            if(ScopedAccess.Require(ctx,s,db,null,"system.admin") is { } denied)return denied;
            var id=db.Enqueue("blob.reconcile",null,"{}");db.Audit(ScopedAccess.Actor(ctx,s,db),"maintenance.queue",id.ToString());
            return Results.Accepted("/api/v1/jobs",new {jobId=id});
        });
    }
}

public static class OriginRules
{
    public static bool Allowed(string? url,Settings settings,string protocol)
    {
        if(string.IsNullOrEmpty(url)||!Uri.TryCreate(url,UriKind.Absolute,out var uri))return false;
        if(uri.Scheme!="https"||uri.Port!=443||!string.IsNullOrEmpty(uri.UserInfo)||
            !string.IsNullOrEmpty(uri.Query)||!string.IsNullOrEmpty(uri.Fragment)||uri.HostNameType!=UriHostNameType.Dns)
            return false;
        // Explicit host policy, not wildcard suffix matching or arbitrary user-supplied URLs.
        var approved=protocol switch
        {
            "nuget"=>new[]{"api.nuget.org"},
            "npm"=>new[]{"registry.npmjs.org"},
            _=>Array.Empty<string>()
        };
        return approved.Contains(uri.IdnHost,StringComparer.OrdinalIgnoreCase);
    }
}
