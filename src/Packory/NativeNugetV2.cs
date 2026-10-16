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
            Settings settings,V2Catalog db,CasStore cas,BlobStore blobs,IHttpClientFactory clients)=>
        {
            var repo=db.FeedBySlug(feed);if(repo?.Protocol!="nuget")return Results.NotFound();
            if(ScopedAccess.Require(ctx,settings,db,feed,"repository.read",repo.Visibility=="public") is { } denied)return denied;
            if(!Names.Nuget(id))return Results.BadRequest();id=id.ToLowerInvariant();
            if(repo.Mode=="proxy")
            {
                var payload=await ProxyRegistration(feed,id,"index.json",settings,blobs,
                    clients.CreateClient("upstream"),ctx.RequestAborted);
                return payload is null?Results.NotFound():Results.Content(payload,"application/json; charset=utf-8");
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
        app.MapGet("/r/nuget/{feed}/v3/registration/{id}/{**suffix}",async (string feed,string id,string? suffix,
            HttpContext ctx,Settings settings,V2Catalog db,CasStore cas,BlobStore blobs,IHttpClientFactory clients)=>
        {
            var repo=db.FeedBySlug(feed);if(repo?.Protocol!="nuget")return Results.NotFound();
            if(ScopedAccess.Require(ctx,settings,db,feed,"repository.read",repo.Visibility=="public") is { } denied)return denied;
            if(!Names.Nuget(id)||string.IsNullOrEmpty(suffix)||!ValidRegistrationPath(suffix))return Results.BadRequest();
            id=id.ToLowerInvariant();
            if(repo.Mode=="proxy")
            {
                var payload=await ProxyRegistration(feed,id,suffix,settings,blobs,clients.CreateClient("upstream"),ctx.RequestAborted);
                return payload is null?Results.NotFound():Results.Content(payload,"application/json; charset=utf-8");
            }
            if(!suffix.EndsWith(".json",StringComparison.Ordinal))return Results.NotFound();
            var version=suffix[..^5];if(!Names.Version(version))return Results.NotFound();
            var asset=db.Asset(feed,"nuget",id,version,id+"."+version+".nupkg");
            if(asset is null||asset.State!="published")return Results.NotFound();
            var path=cas.Find(asset.Digest);if(path is null)return Results.StatusCode(503);
            var baseUrl=settings.Base+"/r/nuget/"+feed+"/v3/";
            return Results.Json(new
            {
                @id=baseUrl+"registration/"+id+"/"+suffix,
                catalogEntry=new {id,version,description=NuGetEndpoints.ReadNuspec(path).Description},
                packageContent=baseUrl+"flatcontainer/"+id+"/"+version+"/"+asset.Filename
            });
        });
    }

    private static bool ValidRegistrationPath(string suffix)=>suffix.Length<=300 &&
        suffix.Split('/').All(segment=>segment.Length is >0 and <=150 && segment is not ("." or "..") &&
            segment.All(c=>char.IsAsciiLetterOrDigit(c)||c is '.' or '-' or '+' or '_'));

    private static async Task<string?> ProxyRegistration(string feed,string id,string suffix,Settings settings,
        BlobStore blobs,HttpClient http,CancellationToken ct)
    {
        if(!ValidRegistrationPath(suffix))throw new InvalidDataException("Invalid registration path");
        var key="v2-nuget-reg/"+feed+"/"+id+"/"+Names.HashName(suffix)+".json";
        var cached=blobs.Existing(key);
        try
        {
            var uri="https://api.nuget.org/v3/registration5-semver2/"+id+"/"+suffix;
            using var response=await http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,ct);
            if(response.StatusCode==HttpStatusCode.NotFound)return null;
            if(!response.IsSuccessStatusCode)throw new HttpRequestException("Registration fetch failed",null,response.StatusCode);
            var bytes=await BlobStore.ReadBoundedAsync(await response.Content.ReadAsStreamAsync(ct),settings.MaxMetadataBytes,ct);
            using(var doc=System.Text.Json.JsonDocument.Parse(bytes))
                if(doc.RootElement.ValueKind!=System.Text.Json.JsonValueKind.Object)
                    throw new InvalidDataException("Invalid registration metadata");
            var payload=Encoding.UTF8.GetString(bytes);
            payload=payload.Replace("https://api.nuget.org/v3/registration5-semver2/",
                settings.Base+"/r/nuget/"+feed+"/v3/registration/",StringComparison.OrdinalIgnoreCase);
            payload=payload.Replace("https://api.nuget.org/v3-flatcontainer/",
                settings.Base+"/r/nuget/"+feed+"/v3/flatcontainer/",StringComparison.OrdinalIgnoreCase);
            var output=Encoding.UTF8.GetBytes(payload);
            await blobs.ReplaceAsync(key,new MemoryStream(output,false),settings.MaxMetadataBytes,ct);
            return payload;
        }
        catch(HttpRequestException) when(cached is not null){return await File.ReadAllTextAsync(cached,ct);}
        catch(TaskCanceledException) when(cached is not null && !ct.IsCancellationRequested){return await File.ReadAllTextAsync(cached,ct);}
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
            // NuGet's PackageBaseAddress V3 contract documents .nupkg and .nuspec,
            // but does not require publishing an origin .nupkg.sha512 sidecar.
            // A missing sidecar is not a checksum mismatch. Preserve that fact in
            // provenance instead of falsely treating TLS-only fetches as SHA512-verified.
            byte[]? expected=null;
            if(checkResponse.StatusCode!=HttpStatusCode.NotFound)
            {
                if(!checkResponse.IsSuccessStatusCode)
                    throw new HttpRequestException("NuGet origin checksum fetch failed",null,checkResponse.StatusCode);
                var hashString=Encoding.UTF8.GetString(await BlobStore.ReadBoundedAsync(
                    await checkResponse.Content.ReadAsStreamAsync(ct),1024,ct)).Trim();
                try {expected=Convert.FromBase64String(hashString);}
                catch(FormatException){throw new InvalidDataException("Invalid NuGet origin checksum");}
                if(expected.Length!=64)throw new InvalidDataException("Invalid SHA512 digest length");
            }
            if(response.Content.Headers.ContentLength>settings.MaxArtifactBytes)throw new PayloadTooLargeException();
            var blob=await cas.PutAsync(await response.Content.ReadAsStreamAsync(ct),settings.MaxArtifactBytes,ct);
            await using(var fs=File.OpenRead(blob.Path))
            {
                var actual=await SHA512.HashDataAsync(fs,ct);
                if(expected is not null && !CryptographicOperations.FixedTimeEquals(expected,actual))
                    throw new InvalidDataException("NuGet origin digest mismatch");
            }
            var info=ParsePackage(blob.Path);
            if(info.Id!=id||info.Version!=version)throw new InvalidDataException("NuGet package identity mismatch");
            var asset=new StoredAsset(feed,"nuget",id,version,filename,blob.Sha256,blob.Size,"application/octet-stream",
                "mirror","published",DateTimeOffset.UtcNow.ToString("O"));
            if(!catalog.Publish(asset))return catalog.Asset(feed,"nuget",id,version,filename);
            catalog.Audit("origin:nuget.org","nuget.mirror.verification",feed+"/"+id+"/"+version,
                expected is null?"tls-only; local-sha256":"origin-sha512; local-sha256");
            return asset;
        }
        finally {semaphore.Release();}
    }
}
