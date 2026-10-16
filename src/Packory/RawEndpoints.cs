namespace Packory;

public static class RawEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapMethods("/r/raw/{feed}/{version}/{**name}",["PUT"],async (string feed,string version,string? name,
            HttpContext ctx,Settings settings,V2Catalog db,CasStore cas)=>
        {
            var repository=db.FeedBySlug(feed);
            if(repository is null || repository.Protocol!="raw")return Results.NotFound();
            if(ScopedAccess.Require(ctx,settings,db,feed,"artifact.publish") is { } denied)return denied;
            if(repository.Mode!="hosted")return Results.StatusCode(405);
            if(!Names.Version(version)||!ValidPath(name))return Results.BadRequest(new {error="Invalid version or raw path"});
            var key=name!;
            if(db.Asset(feed,"raw",key,version,"payload") is not null)return Results.Conflict();
            var expected=ctx.Request.Headers["X-Packory-SHA256"].ToString();
            if(expected.Length==0)expected=null;
            if(ctx.Request.ContentLength>settings.MaxArtifactBytes)return Results.StatusCode(413);
            CasBlob blob;
            try {blob=await cas.PutAsync(ctx.Request.Body,settings.MaxArtifactBytes,ctx.RequestAborted,expected);}
            catch(InvalidDataException){return Results.BadRequest(new {error="Invalid content digest"});}
            var now=DateTimeOffset.UtcNow.ToString("O");
            var record=new StoredAsset(feed,"raw",key,version,"payload",blob.Sha256,blob.Size,
                "application/octet-stream","hosted","published",now);
            if(!db.Publish(record))return Results.Conflict();
            db.Audit(ScopedAccess.Actor(ctx,settings,db),"artifact.publish",$"{feed}/{version}/{key}",blob.Sha256);
            return Results.Created($"/r/raw/{feed}/{version}/{key}",new {sha256=blob.Sha256,size=blob.Size});
        });
        app.MapMethods("/r/raw/{feed}/{version}/{**name}",["GET","HEAD"],(string feed,string version,string? name,
            HttpContext ctx,Settings settings,V2Catalog db,CasStore cas)=>
        {
            var repository=db.FeedBySlug(feed);
            if(repository is null||repository.Protocol!="raw")return Results.NotFound();
            if(ScopedAccess.Require(ctx,settings,db,feed,"repository.read",repository.Visibility=="public") is { } denied)return denied;
            if(!Names.Version(version)||!ValidPath(name))return Results.BadRequest();
            var asset=db.Asset(feed,"raw",name!,version,"payload");
            if(asset is null || asset.State!="published")return Results.NotFound();
            var path=cas.Find(asset.Digest);
            if(path is null)return Results.StatusCode(503);
            ctx.Response.Headers.ETag='"'+asset.Digest+'"';
            ctx.Response.Headers["X-Packory-SHA256"]=asset.Digest;
            return Results.File(path,asset.ContentType,enableRangeProcessing:true,
                fileDownloadName:Path.GetFileName(name));
        });
        app.MapPatch("/api/v1/artifacts/raw/{feed}/{version}/{**name}",(string feed,string version,string? name,
            StateRequest request,HttpContext ctx,Settings settings,V2Catalog db)=>
        {
            if(ScopedAccess.Require(ctx,settings,db,feed,"artifact.yank") is { } denied)return denied;
            if(request.State is not ("published" or "quarantined" or "yanked")||!ValidPath(name)||!Names.Version(version))
                return Results.BadRequest();
            if(!db.SetAssetState(feed,"raw",name!,version,"payload",request.State))return Results.NotFound();
            db.Audit(ScopedAccess.Actor(ctx,settings,db),"artifact.state",$"{feed}/{version}/{name}",request.State);
            return Results.NoContent();
        });
    }

    internal static bool ValidPath(string? value)
    {
        if(string.IsNullOrWhiteSpace(value)||value.Length>500||value.Contains('\\')||value.Contains(':')||
            value.StartsWith('/')||value.Contains('%')||value.Any(char.IsControl))return false;
        return value.Split('/').All(x=>x.Length is >0 and <=128 && x is not ("." or "..") &&
            x.All(c=>char.IsAsciiLetterOrDigit(c)||c is '.' or '-' or '_'));
    }
}
