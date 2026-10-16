using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Packory;

public static class NativeNugetV2
{
    private static readonly ConcurrentDictionary<string,SemaphoreSlim> Locks=new();

    public static void Map(WebApplication app)
    {
        app.MapGet("/r/nuget/{feed}/v3/index.json",(string feed,HttpContext ctx,Settings settings,V2Catalog catalog)=>
        {
            var repo=catalog.FeedBySlug(feed);
            if(repo?.Protocol!="nuget")return Results.NotFound();
            if(ScopedAccess.Require(ctx,settings,catalog,feed,"repository.read",repo.Visibility=="public") is { } denied)return denied;
            var baseUrl=settings.Base+"/r/nuget/"+feed+"/v3/";
            var resources=new List<Dictionary<string,string>>
            {
                new(){["@id"]=baseUrl+"flatcontainer/",["@type"]="PackageBaseAddress/3.0.0"},
                new(){["@id"]=baseUrl+"registration/",["@type"]="RegistrationsBaseUrl/3.6.0"}
            };
            if(repo.Mode=="hosted")resources.Add(new(){["@id"]=baseUrl+"push",["@type"]="PackagePublish/2.0.0"});
            return Results.Json(new {version="3.0.0",resources});
        });
        app.MapGet("/r/nuget/{feed}/v3/flatcontainer/{id}/index.json",async (string feed,string id,HttpContext ctx,
            Settings s,V2Catalog catalog,IHttpClientFactory clients)=>
        {
            var repo=catalog.FeedBySlug(feed);if(repo?.Protocol!="nuget")return Results.NotFound();
            if(ScopedAccess.Require(ctx,s,catalog,feed,"repository.read",repo.Visibility=="public") is { } denied)return denied;
            if(!Names.Nuget(id))return Results.BadRequest();id=id.ToLowerInvariant();
            var versions=new HashSet<string>(catalog.Assets(feed,"nuget",id).Where(a=>a.State=="published")
                .Select(x=>x.Version),StringComparer.OrdinalIgnoreCase);
            if(repo.Mode=="proxy")
            {
                using var result=await clients.CreateClient("upstream").GetAsync(repo.Upstream!.TrimEnd('/')+"/"+id+"/index.json",ctx.RequestAborted);
                if(result.StatusCode!=HttpStatusCode.NotFound)
                {
                    if(!result.IsSuccessStatusCode)return Results.StatusCode(502);
                    var content=await BlobStore.ReadBoundedAsync(await result.Content.ReadAsStreamAsync(ctx.RequestAborted),s.MaxMetadataBytes,ctx.RequestAborted);
                    using var json=System.Text.Json.JsonDocument.Parse(content);
                    if(json.RootElement.TryGetProperty("versions",out var remote)&&remote.ValueKind==System.Text.Json.JsonValueKind.Array)
                        foreach(var version in remote.EnumerateArray())
                            if(version.ValueKind==System.Text.Json.JsonValueKind.String && version.GetString() is { } v && Names.Version(v))versions.Add(v.ToLowerInvariant());
                }
            }
            return versions.Count==0?Results.NotFound():Results.Json(new {versions=versions.OrderBy(x=>x,StringComparer.Ordinal).ToArray()});
        });
        app.MapGet("/r/nuget/{feed}/v3/flatcontainer/{id}/{version}/{filename}",async (string feed,string id,string version,string filename,
            HttpContext ctx,Settings s,V2Catalog catalog,CasStore cas,IHttpClientFactory clients)=>
        {
            var repo=catalog.FeedBySlug(feed);if(repo?.Protocol!="nuget")return Results.NotFound();
            if(ScopedAccess.Require(ctx,s,catalog,feed,"repository.read",repo.Visibility=="public") is { } denied)return denied;
            if(!Names.Nuget(id)||!Names.Version(version))return Results.BadRequest();
            id=id.ToLowerInvariant();version=version.ToLowerInvariant();
            var canonical=id+"."+version+".nupkg";
            if(filename!=canonical && filename!=canonical+".sha512" && filename!=id+".nuspec")return Results.NotFound();
            var asset=await Ensure(feed,id,version,repo,s,catalog,cas,clients.CreateClient("upstream"),ctx.RequestAborted);
            if(asset is null||asset.State!="published")return Results.NotFound();
            var path=cas.Find(asset.Digest);if(path is null)return Results.StatusCode(503);
            if(filename.EndsWith(".sha512",StringComparison.Ordinal))
            {
                await using var stream=File.OpenRead(path);
                return Results.Text(Convert.ToBase64String(await SHA512.HashDataAsync(stream,ctx.RequestAborted)),"text/plain");
            }
            if(filename.EndsWith(".nuspec",StringComparison.Ordinal))return Results.Bytes(NuGetEndpoints.ReadNuspec(path).Xml,"application/xml");
            return Results.File(path,"application/octet-stream",enableRangeProcessing:true);
        });
        app.MapMethods("/r/nuget/{feed}/v3/push",["PUT","POST"],async (string feed,HttpContext ctx,
            Settings settings,V2Catalog db,CasStore cas)=>
        {
            var repo=db.FeedBySlug(feed);if(repo?.Protocol!="nuget")return Results.NotFound();
            if(ScopedAccess.Require(ctx,settings,db,feed,"artifact.publish") is { } denied)return denied;
            if(repo.Mode!="hosted")return Results.StatusCode(405);
            if(!ctx.Request.HasFormContentType)return Results.BadRequest(new {error="Expected multipart/form-data"});
            var form=await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            if(form.Files.Count!=1)return Results.BadRequest();
            var upload=form.Files[0];if(upload.Length>settings.MaxArtifactBytes)return Results.StatusCode(413);
            await using var input=upload.OpenReadStream();
            var blob=await cas.PutAsync(input,settings.MaxArtifactBytes,ctx.RequestAborted);
            NugetV2Info info;
            try {info=ParsePackage(blob.Path);} catch(Exception ex) when(ex is InvalidDataException or System.Xml.XmlException)
            {return Results.BadRequest(new {error="Invalid NuGet package"});}
            var asset=new StoredAsset(feed,"nuget",info.Id,info.Version,info.Id+"."+info.Version+".nupkg",
                blob.Sha256,blob.Size,"application/octet-stream","hosted","published",DateTimeOffset.UtcNow.ToString("O"));
            if(!db.Publish(asset))return Results.Conflict();
            db.Audit(ScopedAccess.Actor(ctx,settings,db),"nuget.publish",feed+"/"+info.Id+"/"+info.Version,blob.Sha256);
            return Results.StatusCode(201);
        });
        app.MapGet("/r/nuget/{feed}/v3/registration/{id}/index.json",async (string feed,string id,HttpContext ctx,
            Settings settings,V2Catalog db,CasStore cas,IHttpClientFactory clients)=>
        {
            var repo=db.FeedBySlug(feed);if(repo?.Protocol!="nuget")return Results.NotFound();
            if(ScopedAccess.Require(ctx,settings,db,feed,"repository.read",repo.Visibility=="public") is { } denied)return denied;
            if(!Names.Nuget(id))return Results.BadRequest();id=id.ToLowerInvariant();
            // Registration metadata for a proxy must come from the origin. Do not trust its links as download URLs.
            if(repo.Mode=="proxy")
            {
                var url="https://api.nuget.org/v3/registration5-semver2/"+id+"/index.json";
                using var response=await clients.CreateClient("upstream").GetAsync(url,ctx.RequestAborted);
                if(response.StatusCode==HttpStatusCode.NotFound)return Results.NotFound();
                if(!response.IsSuccessStatusCode)return Results.StatusCode(502);
                var bytes=await BlobStore.ReadBoundedAsync(await response.Content.ReadAsStreamAsync(ctx.RequestAborted),settings.MaxMetadataBytes,ctx.RequestAborted);
                var raw=Encoding.UTF8.GetString(bytes);
                // External registration IDs remain public upstream references, while package downloads
                // must always be served from Packory. Rebase only the package content fields.
                raw=raw.Replace("https://api.nuget.org/v3-flatcontainer/",settings.Base+"/r/nuget/"+feed+"/v3/flatcontainer/",StringComparison.OrdinalIgnoreCase);
                return Results.Content(raw,"application/json; charset=utf-8");
            }
            var local=db.Assets(feed,"nuget",id).Where(x=>x.State=="published").ToArray();
            if(local.Length==0)return Results.NotFound();
            var baseUrl=settings.Base+"/r/nuget/"+feed+"/v3/";
            var leaves=local.Select(asset=>(object)new
            {
                @id=baseUrl+"registration/"+id+"/"+asset.Version+".json",
                catalogEntry=new {id,version=asset.Version},
                packageContent=baseUrl+"flatcontainer/"+id+"/"+asset.Version+"/"+asset.Filename
            }).ToArray();
            return Results.Json(new {count=1,items=new[]{new {count=leaves.Length,items=leaves}}});
        });
    }

    private sealed record NugetV2Info(string Id,string Version);
    private static NugetV2Info ParsePackage(string path)
    {
        var p=NuGetEndpoints.ReadNuspec(path);
        return new(p.Id.ToLowerInvariant(),p.Version.ToLowerInvariant());
    }

    private static async Task<StoredAsset?> Ensure(string feed,string id,string version,Feed repo,Settings settings,
        V2Catalog catalog,CasStore cas,HttpClient http,CancellationToken ct)
    {
        var filename=id+"."+version+".nupkg";
        var found=catalog.Asset(feed,"nuget",id,version,filename);
        if(found is not null||repo.Mode=="hosted")return found;
        var semaphore=Locks.GetOrAdd(feed+"/"+id+"/"+version,_=>new SemaphoreSlim(1,1));
        await semaphore.WaitAsync(ct);
        try
        {
            found=catalog.Asset(feed,"nuget",id,version,filename);if(found is not null)return found;
            var uri=repo.Upstream!.TrimEnd('/')+"/"+id+"/"+version+"/"+filename;
            using var response=await http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,ct);
            if(response.StatusCode==HttpStatusCode.NotFound)return null;
            if(!response.IsSuccessStatusCode)throw new HttpRequestException("NuGet origin request failed",null,response.StatusCode);
            var checksumUri=uri+".sha512";
            using var checkResponse=await http.GetAsync(checksumUri,HttpCompletionOption.ResponseHeadersRead,ct);
            if(!checkResponse.IsSuccessStatusCode)throw new InvalidDataException("Missing origin checksum");
            var hashString=Encoding.UTF8.GetString(await BlobStore.ReadBoundedAsync(
                await checkResponse.Content.ReadAsStreamAsync(ct),1024,ct)).Trim();
            byte[]? expected;
            try {expected=Convert.FromBase64String(hashString);}
            catch(FormatException){throw new InvalidDataException("Invalid NuGet origin checksum");}
            if(expected.Length!=64)throw new InvalidDataException("Invalid SHA512 digest length");
            if(response.Content.Headers.ContentLength>settings.MaxArtifactBytes)throw new PayloadTooLargeException();
            var blob=await cas.PutAsync(await response.Content.ReadAsStreamAsync(ct),settings.MaxArtifactBytes,ct);
            await using(var fs=File.OpenRead(blob.Path))
            {
                var actual=await SHA512.HashDataAsync(fs,ct);
                if(!CryptographicOperations.FixedTimeEquals(expected,actual))throw new InvalidDataException("NuGet origin digest mismatch");
            }
            var info=ParsePackage(blob.Path);
            if(info.Id!=id||info.Version!=version)throw new InvalidDataException("NuGet package identity mismatch");
            var asset=new StoredAsset(feed,"nuget",id,version,filename,blob.Sha256,blob.Size,"application/octet-stream",
                "mirror","published",DateTimeOffset.UtcNow.ToString("O"));
            if(!catalog.Publish(asset))return catalog.Asset(feed,"nuget",id,version,filename);
            return asset;
        }
        finally {semaphore.Release();}
    }
}
