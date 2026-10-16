using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Packory;

public static class NativeNpmV2
{
    private static readonly ConcurrentDictionary<string,SemaphoreSlim> Locks=new();

    public static void Map(WebApplication app)
    {
        app.MapGet("/r/npm/{feed}/{**resource}",async (string feed,string? resource,HttpContext ctx,Settings s,
            V2Catalog db,CasStore cas,BlobStore oldBlobs,IHttpClientFactory clients)=>
        {
            var repo=db.FeedBySlug(feed);if(repo?.Protocol!="npm")return Results.NotFound();
            if(ScopedAccess.Require(ctx,s,db,feed,"repository.read",repo.Visibility=="public") is { } denied)return denied;
            if(string.IsNullOrEmpty(resource))return Results.NotFound();
            string raw;
            try {raw=Uri.UnescapeDataString(resource).Trim('/');} catch(UriFormatException){return Results.BadRequest();}
            if(raw.Contains("/-/",StringComparison.Ordinal))
            {
                var parts=raw.Split("/-/",2,StringSplitOptions.None);
                if(!Names.Npm(parts[0])||!parts[1].EndsWith(".tgz",StringComparison.Ordinal)||
                    !Names.Version(parts[1][..^4]))return Results.NotFound();
                var name=parts[0];var version=parts[1][..^4];
                var asset=db.Asset(feed,"npm",name,version,"bundle.tgz");
                if(asset is null&&repo.Mode=="proxy")
                {
                    var key=feed+"/"+name+"/"+version;
                    var semaphore=Locks.GetOrAdd(key,_=>new SemaphoreSlim(1,1));
                    await semaphore.WaitAsync(ctx.RequestAborted);
                    try
                    {
                        asset=db.Asset(feed,"npm",name,version,"bundle.tgz");
                        if(asset is null)
                        {
                            var metadata=await FetchPackument(feed,name,repo,s,oldBlobs,clients.CreateClient("upstream"),ctx.RequestAborted);
                            if(metadata?["versions"]?[version]?["dist"] is not JsonObject dist)return Results.NotFound();
                            var tarball=dist["tarball"]?.GetValue<string>();
                            if(!OriginRules.Allowed(tarball,s,"npm"))return Results.StatusCode(502);
                            using var remote=await clients.CreateClient("upstream").GetAsync(tarball,HttpCompletionOption.ResponseHeadersRead,ctx.RequestAborted);
                            if(remote.StatusCode==HttpStatusCode.NotFound)return Results.NotFound();
                            if(!remote.IsSuccessStatusCode)return Results.StatusCode(502);
                            if(remote.Content.Headers.ContentLength>s.MaxArtifactBytes)return Results.StatusCode(413);
                            var blob=await cas.PutAsync(await remote.Content.ReadAsStreamAsync(ctx.RequestAborted),s.MaxArtifactBytes,ctx.RequestAborted);
                            await using(var fs=File.OpenRead(blob.Path))
                            {
                                if(dist["integrity"]?.GetValue<string>() is { } integrity && integrity.StartsWith("sha512-",StringComparison.Ordinal))
                                {
                                    if(!string.Equals(integrity[7..],Convert.ToBase64String(await SHA512.HashDataAsync(fs,ctx.RequestAborted)),StringComparison.Ordinal))
                                        return Results.StatusCode(502);
                                }
                                else if(dist["shasum"]?.GetValue<string>() is { } shasum)
                                {
                                    if(!string.Equals(shasum,Convert.ToHexStringLower(await SHA1.HashDataAsync(fs,ctx.RequestAborted)),StringComparison.OrdinalIgnoreCase))
                                        return Results.StatusCode(502);
                                }
                                else return Results.StatusCode(502);
                            }
                            asset=new StoredAsset(feed,"npm",name,version,"bundle.tgz",blob.Sha256,blob.Size,
                                "application/octet-stream","mirror","published",DateTimeOffset.UtcNow.ToString("O"));
                            db.Publish(asset);
                            asset=db.Asset(feed,"npm",name,version,"bundle.tgz");
                        }
                    }
                    finally{semaphore.Release();}
                }
                if(asset is null||asset.State!="published")return Results.NotFound();
                var path=cas.Find(asset.Digest);return path is null?Results.StatusCode(503):Results.File(path,"application/octet-stream",enableRangeProcessing:true);
            }
            var nameOnly=raw;string? requestedVersion=null;
            if(!Names.Npm(raw))
            {
                var cut=raw.LastIndexOf('/');if(cut<0)return Results.NotFound();
                nameOnly=raw[..cut];requestedVersion=raw[(cut+1)..];
                if(!Names.Npm(nameOnly)||!Names.Version(requestedVersion))return Results.NotFound();
            }
            var hosted=db.NpmManifests(feed,nameOnly);
            var doc=repo.Mode=="proxy"?await FetchPackument(feed,nameOnly,repo,s,oldBlobs,
                clients.CreateClient("upstream"),ctx.RequestAborted):null;
            if(doc is null&&hosted.Count==0)return Results.NotFound();
            doc??=new JsonObject{["name"]=nameOnly,["versions"]=new JsonObject(),["dist-tags"]=new JsonObject()};
            if(doc["versions"] is not JsonObject versions)doc["versions"]=versions=new JsonObject();
            if(doc["dist-tags"] is not JsonObject tags)doc["dist-tags"]=tags=new JsonObject();
            foreach(var pair in hosted)versions[pair.Key]=JsonNode.Parse(pair.Value);
            if(hosted.Count>0)tags["latest"]=hosted.Keys.Last();
            foreach(var pair in versions)
            {
                if(!Names.Version(pair.Key)||pair.Value is not JsonObject manifest)continue;
                if(manifest["dist"] is not JsonObject dist)manifest["dist"]=dist=new JsonObject();
                dist["tarball"]=TarballUrl(s,feed,nameOnly,pair.Key);
            }
            return requestedVersion is null?Results.Json(doc):
                versions[requestedVersion] is { } selected?Results.Json(selected):Results.NotFound();
        });
        app.MapPut("/r/npm/{feed}/{**packageName}",async (string feed,string? packageName,HttpContext ctx,
            Settings s,V2Catalog db,CasStore cas)=>
        {
            var repo=db.FeedBySlug(feed);if(repo?.Protocol!="npm")return Results.NotFound();
            if(ScopedAccess.Require(ctx,s,db,feed,"artifact.publish") is { } denied)return denied;
            if(repo.Mode!="hosted")return Results.StatusCode(405);
            var name=Uri.UnescapeDataString(packageName??"");if(!Names.Npm(name))return Results.BadRequest();
            var data=await BlobStore.ReadBoundedAsync(ctx.Request.Body,Math.Min(s.MaxArtifactBytes*2,s.MaxMetadataBytes),ctx.RequestAborted);
            JsonObject? publish;
            try {publish=JsonNode.Parse(data) as JsonObject;}catch(System.Text.Json.JsonException){return Results.BadRequest();}
            if(publish?["name"]?.ToString()!=name||publish["versions"] is not JsonObject versions||versions.Count!=1 ||
                publish["_attachments"] is not JsonObject attachments||attachments.Count!=1)return Results.BadRequest();
            var (version,node)=versions.First();
            if(!Names.Version(version)||node is not JsonObject manifest||manifest["name"]?.ToString()!=name ||
                manifest["version"]?.ToString()!=version)return Results.BadRequest();
            if(db.Asset(feed,"npm",name,version,"bundle.tgz") is not null)return Results.Conflict();
            if(attachments.First().Value is not JsonObject attachment||attachment["data"]?.GetValue<string>() is not { } encoded)
                return Results.BadRequest();
            byte[] decoded;
            try{decoded=Convert.FromBase64String(encoded);}catch(FormatException){return Results.BadRequest();}
            if(decoded.Length<2||decoded[0]!=0x1f||decoded[1]!=0x8b)return Results.BadRequest();
            if(decoded.Length>s.MaxArtifactBytes)return Results.StatusCode(413);
            var blob=await cas.PutAsync(new MemoryStream(decoded,writable:false),s.MaxArtifactBytes,ctx.RequestAborted);
            manifest["dist"]=new JsonObject
            {
                ["tarball"]=TarballUrl(s,feed,name,version),
                ["shasum"]=Convert.ToHexStringLower(SHA1.HashData(decoded)),
                ["integrity"]="sha512-"+Convert.ToBase64String(SHA512.HashData(decoded))
            };
            var asset=new StoredAsset(feed,"npm",name,version,"bundle.tgz",blob.Sha256,blob.Size,
                "application/octet-stream","hosted","published",DateTimeOffset.UtcNow.ToString("O"));
            if(!db.PublishNpm(asset,manifest.ToJsonString()))return Results.Conflict();
            db.Audit(ScopedAccess.Actor(ctx,s,db),"npm.publish",feed+"/"+name+"/"+version,blob.Sha256);
            return Results.Json(new {ok=true,id=name,version},statusCode:201);
        });
    }

    private static string TarballUrl(Settings s,string feed,string name,string version)=>
        s.Base+"/r/npm/"+feed+"/"+Names.EscapedNpm(name)+"/-/"+version+".tgz";

    private static async Task<JsonObject?> FetchPackument(string feed,string name,Feed repo,Settings s,
        BlobStore blobs,HttpClient client,CancellationToken ct)
    {
        var key="v2-npm-meta/"+feed+"/"+Names.HashName(name)+".json";
        var cache=blobs.Existing(key);
        try
        {
            using var result=await client.GetAsync(repo.Upstream!.TrimEnd('/')+"/"+Names.EscapedNpm(name),HttpCompletionOption.ResponseHeadersRead,ct);
            if(result.StatusCode==HttpStatusCode.NotFound)return null;
            if(!result.IsSuccessStatusCode)throw new HttpRequestException("npm metadata fetch failed",null,result.StatusCode);
            var bytes=await BlobStore.ReadBoundedAsync(await result.Content.ReadAsStreamAsync(ct),s.MaxMetadataBytes,ct);
            var obj=JsonNode.Parse(bytes) as JsonObject;
            if(obj is null||obj["versions"] is not JsonObject)throw new InvalidDataException("Invalid npm metadata");
            await blobs.ReplaceAsync(key,new MemoryStream(bytes,false),s.MaxMetadataBytes,ct);
            return obj;
        }
        catch(HttpRequestException) when(cache is not null){return JsonNode.Parse(await File.ReadAllBytesAsync(cache,ct)) as JsonObject;}
        catch(TaskCanceledException) when(cache is not null && !ct.IsCancellationRequested){return JsonNode.Parse(await File.ReadAllBytesAsync(cache,ct)) as JsonObject;}
    }
}
