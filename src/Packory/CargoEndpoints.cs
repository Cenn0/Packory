using System.Text;

namespace Packory;

// Hosted Cargo sparse index + tarball API. No crates.io source-replacement claim until client tests.
public static class CargoEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/r/cargo/{feed}/index/config.json",(string feed,HttpContext ctx,Settings s,V2Catalog db)=>
        {
            var repo=db.FeedBySlug(feed);if(repo?.Protocol!="cargo")return Results.NotFound();
            if(ScopedAccess.Require(ctx,s,db,feed,"repository.read",repo.Visibility=="public") is { } denied)return denied;
            var baseUrl=s.Base+"/r/cargo/"+feed;
            return Results.Json(new Dictionary<string,object>
            {
                ["dl"]=baseUrl+"/api/v1/crates",
                ["api"]=baseUrl+"/api/v1",
                ["auth-required"]=repo.Visibility!="public"
            });
        });
        app.MapGet("/r/cargo/{feed}/index/{**path}",(string feed,string? path,HttpContext ctx,Settings s,V2Catalog db)=>
        {
            var repo=db.FeedBySlug(feed);if(repo?.Protocol!="cargo")return Results.NotFound();
            if(ScopedAccess.Require(ctx,s,db,feed,"repository.read",repo.Visibility=="public") is { } denied)return denied;
            if(string.IsNullOrEmpty(path)||path.Length>300)return Results.BadRequest();
            var name=path.Split('/').Last().ToLowerInvariant();
            if(!ValidName(name)||!string.Equals(path,IndexPath(name),StringComparison.Ordinal))return Results.NotFound();
            var artifacts=db.Assets(feed,"cargo",name).Where(a=>a.State is "published" or "yanked").ToArray();
            if(artifacts.Length==0)return Results.NotFound();
            var sb=new StringBuilder();
            foreach(var asset in artifacts)
            {
                var json=System.Text.Json.JsonSerializer.Serialize(new
                {
                    name,vers=asset.Version,deps=Array.Empty<object>(),cksum=asset.Digest,
                    features=new Dictionary<string,object>(),yanked=asset.State=="yanked",v=2
                });
                sb.Append(json).Append('\n');
            }
            return Results.Text(sb.ToString(),"text/plain; charset=utf-8");
        });
        app.MapPut("/r/cargo/{feed}/publish/{crate}/{version}",async (string feed,string crate,string version,
            HttpContext ctx,Settings s,V2Catalog db,CasStore cas)=>
        {
            var repo=db.FeedBySlug(feed);if(repo?.Protocol!="cargo")return Results.NotFound();
            if(ScopedAccess.Require(ctx,s,db,feed,"artifact.publish") is { } denied)return denied;
            if(repo.Mode!="hosted")return Results.StatusCode(405);
            if(!ValidName(crate)||!Names.Version(version))return Results.BadRequest();
            crate=crate.ToLowerInvariant();
            if(db.Asset(feed,"cargo",crate,version,crate+"-"+version+".crate") is not null)return Results.Conflict();
            if(ctx.Request.ContentLength>s.MaxArtifactBytes)return Results.StatusCode(413);
            var blob=await cas.PutAsync(ctx.Request.Body,s.MaxArtifactBytes,ctx.RequestAborted);
            await using(var input=File.OpenRead(blob.Path))
            {
                var header=new byte[2];
                if(await input.ReadAsync(header,ctx.RequestAborted)!=2 || header[0]!=0x1f||header[1]!=0x8b)
                    return Results.BadRequest(new {error="Cargo archive must be gzip compressed"});
            }
            var asset=new StoredAsset(feed,"cargo",crate,version,crate+"-"+version+".crate",
                blob.Sha256,blob.Size,"application/octet-stream","hosted","published",DateTimeOffset.UtcNow.ToString("O"));
            if(!db.Publish(asset))return Results.Conflict();
            db.Audit(ScopedAccess.Actor(ctx,s,db),"cargo.publish",feed+"/"+crate+"/"+version,blob.Sha256);
            return Results.Created("/r/cargo/"+feed+"/api/v1/crates/"+crate+"/"+version+"/download",new {sha256=blob.Sha256});
        });
        app.MapGet("/r/cargo/{feed}/api/v1/crates/{crate}/{version}/download",(string feed,string crate,string version,
            HttpContext ctx,Settings s,V2Catalog db,CasStore cas)=>
        {
            var repo=db.FeedBySlug(feed);if(repo?.Protocol!="cargo")return Results.NotFound();
            if(ScopedAccess.Require(ctx,s,db,feed,"repository.read",repo.Visibility=="public") is { } denied)return denied;
            if(!ValidName(crate)||!Names.Version(version))return Results.BadRequest();
            crate=crate.ToLowerInvariant();var asset=db.Asset(feed,"cargo",crate,version,crate+"-"+version+".crate");
            if(asset is null||asset.State!="published")return Results.NotFound();
            var path=cas.Find(asset.Digest);return path is null?Results.StatusCode(503):Results.File(path,"application/octet-stream",enableRangeProcessing:true);
        });
    }

    public static bool ValidName(string? name)=>name is {Length:>0 and <=64} &&
        char.IsAsciiLetterOrDigit(name[0])&&name.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '-');

    public static string IndexPath(string name)=>name.Length switch
    {
        1=>"1/"+name,
        2=>"2/"+name,
        3=>"3/"+name[0]+"/"+name,
        _=>name[..2]+"/"+name.Substring(2,2)+"/"+name
    };
}
