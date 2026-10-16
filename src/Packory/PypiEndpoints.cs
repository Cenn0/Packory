using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Packory;

// PEP 503-style Simple API for hosted wheels/sdists; standard Twine upload is a future gate.
public static partial class PypiEndpoints
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",RegexOptions.CultureInvariant)]
    private static partial Regex ProjectRule();
    [GeneratedRegex("[-_.]+",RegexOptions.CultureInvariant)]
    private static partial Regex Separators();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+~-]{1,229}\\.(?:whl|zip|tar\\.gz)$",RegexOptions.CultureInvariant)]
    private static partial Regex WheelRule();

    public static string? NormalizeProject(string? value)=>
        value is not null&&ProjectRule().IsMatch(value)?Separators().Replace(value,"-").ToLowerInvariant():null;

    public static void Map(WebApplication app)
    {
        app.MapGet("/r/pypi/{feed}/simple/",(string feed,HttpContext ctx,Settings s,V2Catalog db)=>
        {
            var repository=db.FeedBySlug(feed);if(repository?.Protocol!="pypi")return Results.NotFound();
            if(ScopedAccess.Require(ctx,s,db,feed,"repository.read",repository.Visibility=="public") is { } denied)return denied;
            var names=db.Assets(feed,"pypi").Where(a=>a.State=="published")
                .Select(a=>a.Name).Distinct(StringComparer.Ordinal).OrderBy(x=>x);
            var html=new StringBuilder("<!doctype html><html><head><meta name=\"pypi:repository-version\" content=\"1.0\"></head><body>");
            foreach(var name in names)html.Append("<a href=\"").Append(Uri.EscapeDataString(name)).Append("/\">")
                .Append(WebUtility.HtmlEncode(name)).Append("</a>\n");
            html.Append("</body></html>");
            return Results.Text(html.ToString(),"text/html; charset=utf-8");
        });
        app.MapGet("/r/pypi/{feed}/simple/{project}/",(string feed,string project,HttpContext ctx,Settings s,V2Catalog db)=>
        {
            var repository=db.FeedBySlug(feed);if(repository?.Protocol!="pypi")return Results.NotFound();
            if(ScopedAccess.Require(ctx,s,db,feed,"repository.read",repository.Visibility=="public") is { } denied)return denied;
            var name=NormalizeProject(project);if(name is null)return Results.BadRequest();
            var assets=db.Assets(feed,"pypi",name).Where(a=>a.State=="published").ToArray();
            if(assets.Length==0)return Results.NotFound();
            var sb=new StringBuilder("<!doctype html><html><head><meta name=\"pypi:repository-version\" content=\"1.0\"></head><body>");
            foreach(var asset in assets)
            {
                var safe=Uri.EscapeDataString(asset.Filename);
                sb.Append("<a href=\"../../files/").Append(safe).Append("#sha256=").Append(asset.Digest)
                    .Append("\">").Append(WebUtility.HtmlEncode(asset.Filename)).Append("</a>\n");
            }
            sb.Append("</body></html>");return Results.Text(sb.ToString(),"text/html; charset=utf-8");
        });
        app.MapPut("/r/pypi/{feed}/upload/{project}/{version}/{filename}",async (string feed,string project,string version,string filename,
            HttpContext ctx,Settings s,V2Catalog db,CasStore cas)=>
        {
            var repository=db.FeedBySlug(feed);if(repository?.Protocol!="pypi")return Results.NotFound();
            if(ScopedAccess.Require(ctx,s,db,feed,"artifact.publish") is { } denied)return denied;
            if(repository.Mode!="hosted")return Results.StatusCode(405);
            var name=NormalizeProject(project);
            if(name is null||!Names.Version(version)||!WheelRule().IsMatch(filename))return Results.BadRequest();
            if(db.Asset(feed,"pypi",name,version,filename) is not null)return Results.Conflict();
            if(ctx.Request.ContentLength>s.MaxArtifactBytes)return Results.StatusCode(413);
            var blob=await cas.PutAsync(ctx.Request.Body,s.MaxArtifactBytes,ctx.RequestAborted);
            var asset=new StoredAsset(feed,"pypi",name,version,filename,blob.Sha256,blob.Size,
                "application/octet-stream","hosted","published",DateTimeOffset.UtcNow.ToString("O"));
            if(!db.Publish(asset))return Results.Conflict();
            db.Audit(ScopedAccess.Actor(ctx,s,db),"pypi.publish",feed+"/"+name+"/"+version,blob.Sha256);
            return Results.Created("/r/pypi/"+feed+"/files/"+filename,new {sha256=blob.Sha256,size=blob.Size});
        });
        app.MapGet("/r/pypi/{feed}/files/{filename}",(string feed,string filename,HttpContext ctx,Settings s,V2Catalog db,CasStore cas)=>
        {
            var repository=db.FeedBySlug(feed);if(repository?.Protocol!="pypi")return Results.NotFound();
            if(ScopedAccess.Require(ctx,s,db,feed,"repository.read",repository.Visibility=="public") is { } denied)return denied;
            if(!WheelRule().IsMatch(filename))return Results.BadRequest();
            var asset=db.Assets(feed,"pypi").FirstOrDefault(a=>a.Filename==filename&&a.State=="published");
            if(asset is null)return Results.NotFound();var file=cas.Find(asset.Digest);
            return file is null?Results.StatusCode(503):Results.File(file,"application/octet-stream",fileDownloadName:filename,enableRangeProcessing:true);
        });
    }
}
