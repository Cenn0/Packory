using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class V2IntegrationTests
{
    private const string Admin="integration-test-admin-token-longer-than-32-chars";
    private const string Reader="integration-test-reader-token-longer-than-32-chars";
    private const string Writer="integration-test-writer-token-longer-than-32-chars";

    private sealed class Origin(Func<HttpRequestMessage,HttpResponseMessage> handler):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct)=>Task.FromResult(handler(req));
    }
    private sealed class Server(Func<HttpRequestMessage,HttpResponseMessage>? handler=null):WebApplicationFactory<Program>
    {
        private readonly string _root=Path.Combine(Path.GetTempPath(),"packory-v2-"+Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services=>
            {
                services.RemoveAll<Settings>();
                services.AddSingleton(new Settings
                {
                    DataDirectory=_root,PublicBaseUrl="http://localhost:8080",
                    AdminToken=Admin,ReadToken=Reader,WriteToken=Writer,AnonymousRead=false
                });
                services.AddHttpClient("upstream").ConfigurePrimaryHttpMessageHandler(()=>
                    new Origin(handler??(_=>new HttpResponseMessage(HttpStatusCode.NotFound))));
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if(disposing&&Directory.Exists(_root))Directory.Delete(_root,true);
        }
    }

    private static HttpRequestMessage R(HttpMethod method,string path,HttpContent? body=null,string token=Admin)
    {
        var r=new HttpRequestMessage(method,path){Content=body};
        r.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        return r;
    }
    private static JsonContent NewFeed(string slug,string protocol,string mode="hosted",string visibility="private",string? upstream=null)=>
        JsonContent.Create(new {slug,protocol,mode,visibility,upstream});
    private static byte[] NugetPackage(string id="Test.Library",string version="1.0.0")
    {
        using var memory=new MemoryStream();
        using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))
        {
            using var writer=new StreamWriter(zip.CreateEntry(id+".nuspec").Open());
            writer.Write($"<package><metadata><id>{id}</id><version>{version}</version><authors>Example</authors><description>Test</description></metadata></package>");
        }
        return memory.ToArray();
    }
    private static HttpResponseMessage Remote(byte[] bytes,string mediaType="application/octet-stream")=>new(HttpStatusCode.OK)
    {
        Content=new ByteArrayContent(bytes)
    };

    [Fact]
    public async Task Feed_Is_Private_And_Raw_Immutable_And_Cas_Addressed()
    {
        using var server=new Server();using var client=server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/v1/repositories")).StatusCode);
        Assert.Equal(HttpStatusCode.Created,(await client.SendAsync(R(HttpMethod.Post,"/api/v1/repositories",NewFeed("private-files","raw")))).StatusCode);
        var bytes=Encoding.UTF8.GetBytes("V2 immutable artifact\n");
        var url="/r/raw/private-files/1.0.0/docs/example.txt";
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PutAsync(url,new ByteArrayContent(bytes))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.SendAsync(R(HttpMethod.Put,url,new ByteArrayContent(bytes),Reader))).StatusCode);
        using var uploaded=await client.SendAsync(R(HttpMethod.Put,url,new ByteArrayContent(bytes)));
        Assert.Equal(HttpStatusCode.Created,uploaded.StatusCode);
        var created=JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
        var hash=Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal(hash,created.RootElement.GetProperty("sha256").GetString());
        Assert.Equal(HttpStatusCode.Conflict,(await client.SendAsync(R(HttpMethod.Put,url,new ByteArrayContent(bytes)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync(url)).StatusCode);
        using var read=await client.SendAsync(R(HttpMethod.Get,url,token:Reader));
        Assert.Equal(HttpStatusCode.OK,read.StatusCode);
        Assert.Equal(bytes,await read.Content.ReadAsByteArrayAsync());
        Assert.Equal(hash,read.Headers.GetValues("X-Packory-SHA256").Single());
        Assert.Equal(HttpStatusCode.NoContent,(await client.SendAsync(R(HttpMethod.Patch,
            "/api/v1/artifacts/raw/private-files/1.0.0/docs/example.txt",JsonContent.Create(new {state="quarantined"})))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await client.SendAsync(R(HttpMethod.Get,url))).StatusCode);
        using var audits=await client.SendAsync(R(HttpMethod.Get,"/api/v1/audit"));
        Assert.Contains("artifact.publish",await audits.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Tokens_Are_Scoped_Revocable_And_Secrets_Not_In_Audit()
    {
        using var server=new Server();using var client=server.CreateClient();
        await client.SendAsync(R(HttpMethod.Post,"/api/v1/repositories",NewFeed("team-a","raw")));
        await client.SendAsync(R(HttpMethod.Post,"/api/v1/repositories",NewFeed("team-b","raw")));
        using var minted=await client.SendAsync(R(HttpMethod.Post,"/api/v1/tokens",JsonContent.Create(new
        {
            name="ci-reader",feed="team-a",scopes=new[]{"repository.read"},expiresAt=DateTimeOffset.UtcNow.AddHours(1)
        })));
        Assert.Equal(HttpStatusCode.Created,minted.StatusCode);
        using var json=JsonDocument.Parse(await minted.Content.ReadAsStringAsync());
        var token=json.RootElement.GetProperty("secret").GetString()!;
        var id=json.RootElement.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK,(await client.SendAsync(R(HttpMethod.Get,"/api/v1/repositories/team-a",token:token))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.SendAsync(R(HttpMethod.Get,"/api/v1/repositories/team-b",token:token))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.SendAsync(R(HttpMethod.Put,"/r/raw/team-a/1.0.0/test.txt",new ByteArrayContent([1]),token))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await client.SendAsync(R(HttpMethod.Delete,"/api/v1/tokens/"+id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.SendAsync(R(HttpMethod.Get,"/api/v1/repositories/team-a",token:token))).StatusCode);
        var audit=await (await client.SendAsync(R(HttpMethod.Get,"/api/v1/audit"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain(token,audit);
    }

    [Fact]
    public async Task Public_Feed_Reads_Require_Explicit_Visibility()
    {
        using var server=new Server();using var client=server.CreateClient();
        Assert.Equal(HttpStatusCode.Created,(await client.SendAsync(R(HttpMethod.Post,"/api/v1/repositories",
            NewFeed("shared","raw",visibility:"public")))).StatusCode);
        var uri="/r/raw/shared/0.1.0/file.bin";
        Assert.Equal(HttpStatusCode.Created,(await client.SendAsync(R(HttpMethod.Put,uri,new ByteArrayContent([1,2,3])))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(uri)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.SendAsync(R(HttpMethod.Patch,
            "/api/v1/repositories/shared/visibility",JsonContent.Create(new {visibility="private"})))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync(uri)).StatusCode);
    }

    [Fact]
    public async Task Proxy_Origins_Are_Strictly_Allowlisted()
    {
        using var server=new Server();using var client=server.CreateClient();
        var invalid=new[]{"http://api.nuget.org/v3-flatcontainer/","https://localhost/v3-flatcontainer/",
            "https://api.nuget.org.evil.invalid/v3-flatcontainer/","https://127.0.0.1/","https://api.nuget.org:444/"};
        foreach(var url in invalid)
        {
            var response=await client.SendAsync(R(HttpMethod.Post,"/api/v1/repositories",
                NewFeed("unsafe","nuget","proxy",upstream:url)));
            Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
        }
        Assert.Equal(HttpStatusCode.Created,(await client.SendAsync(R(HttpMethod.Post,"/api/v1/repositories",
            NewFeed("public-nuget","nuget","proxy",upstream:"https://api.nuget.org/v3-flatcontainer/")))).StatusCode);
    }

    [Fact]
    public async Task NugetV2_Hosted_And_Mirror_Isolate_Repositories()
    {
        var bytes=NugetPackage();
        var remoteCalls=0;
        using var server=new Server(req=>
        {
            Interlocked.Increment(ref remoteCalls);
            var path=req.RequestUri!.AbsolutePath;
            if(path.EndsWith(".nupkg.sha512",StringComparison.Ordinal))return new(HttpStatusCode.OK)
            {
                Content=new StringContent(Convert.ToBase64String(SHA512.HashData(bytes)))
            };
            if(path.EndsWith(".nupkg",StringComparison.Ordinal))return Remote(bytes);
            if(path.EndsWith("index.json",StringComparison.Ordinal))return new(HttpStatusCode.OK)
            {
                Content=new StringContent("{\"versions\":[\"1.0.0\"]}")
            };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client=server.CreateClient();
        await client.SendAsync(R(HttpMethod.Post,"/api/v1/repositories",NewFeed("internal-nuget","nuget")));
        await client.SendAsync(R(HttpMethod.Post,"/api/v1/repositories",NewFeed("mirror-nuget","nuget","proxy",
            upstream:"https://api.nuget.org/v3-flatcontainer/")));
        var form=new MultipartFormDataContent();form.Add(new ByteArrayContent(bytes),"package","test.library.1.0.0.nupkg");
        Assert.Equal(HttpStatusCode.Created,(await client.SendAsync(R(HttpMethod.Put,"/r/nuget/internal-nuget/v3/push",form))).StatusCode);
        var path="/v3/flatcontainer/test.library/1.0.0/test.library.1.0.0.nupkg";
        Assert.Equal(bytes,await (await client.SendAsync(R(HttpMethod.Get,"/r/nuget/internal-nuget"+path))).Content.ReadAsByteArrayAsync());
        Assert.Equal(bytes,await (await client.SendAsync(R(HttpMethod.Get,"/r/nuget/mirror-nuget"+path))).Content.ReadAsByteArrayAsync());
        var callsAfterFirst=remoteCalls;
        Assert.Equal(bytes,await (await client.SendAsync(R(HttpMethod.Get,"/r/nuget/mirror-nuget"+path))).Content.ReadAsByteArrayAsync());
        Assert.Equal(callsAfterFirst,remoteCalls);
        var service=await (await client.SendAsync(R(HttpMethod.Get,"/r/nuget/internal-nuget/v3/index.json"))).Content.ReadAsStringAsync();
        Assert.Contains("/r/nuget/internal-nuget/v3/flatcontainer/",service);
        Assert.DoesNotContain("api.nuget.org",service);
    }

    [Fact]
    public async Task NpmV2_Hosted_Publishes_And_Uses_Isolated_Tarball_Urls()
    {
        using var server=new Server();using var client=server.CreateClient();
        await client.SendAsync(R(HttpMethod.Post,"/api/v1/repositories",NewFeed("npm-team","npm")));
        byte[] tarball=[0x1f,0x8b,0x08,0,0,0,0,0];
        var publish=new JsonObject
        {
            ["name"]="local-demo",
            ["versions"]=new JsonObject{["1.0.0"]=new JsonObject
            { ["name"]="local-demo",["version"]="1.0.0",["description"]="Demo"}},
            ["_attachments"]=new JsonObject{["local-demo-1.0.0.tgz"]=new JsonObject
            { ["data"]=Convert.ToBase64String(tarball)}}
        };
        Assert.Equal(HttpStatusCode.Created,(await client.SendAsync(R(HttpMethod.Put,"/r/npm/npm-team/local-demo",
            new StringContent(publish.ToJsonString(),Encoding.UTF8,"application/json")))).StatusCode);
        var document=await (await client.SendAsync(R(HttpMethod.Get,"/r/npm/npm-team/local-demo"))).Content.ReadAsStringAsync();
        Assert.Contains("localhost:8080/r/npm/npm-team/local-demo/-/1.0.0.tgz",document);
        Assert.Contains("sha512-",document);
        Assert.Equal(tarball,await (await client.SendAsync(R(HttpMethod.Get,"/r/npm/npm-team/local-demo/-/1.0.0.tgz"))).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Mcp_Manifest_Is_Quarantined_Until_Approved()
    {
        using var server=new Server();using var client=server.CreateClient();
        var manifest=new {name="com.example/demo",version="1.2.3",description="Sample",remotes=new[]{new{type="streamable-http",url="https://mcp.example.invalid/mcp"}}};
        Assert.Equal(HttpStatusCode.Created,(await client.SendAsync(R(HttpMethod.Post,"/v0.1/publish",JsonContent.Create(manifest)))).StatusCode);
        var before=await (await client.SendAsync(R(HttpMethod.Get,"/v0.1/servers"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("com.example/demo",before);
        // Names include a slash; route parameter must preserve URL encoding.
        var approve=await client.SendAsync(R(HttpMethod.Post,"/api/v1/mcp/com.example%2Fdemo/versions/1.2.3/approve"));
        Assert.Equal(HttpStatusCode.NoContent,approve.StatusCode);
        var after=await (await client.SendAsync(R(HttpMethod.Get,"/v0.1/servers"))).Content.ReadAsStringAsync();
        Assert.Contains("com.example/demo",after);
    }

    [Fact]
    public async Task Sqlite_V2_Migration_Is_Idempotent_And_Preserves_Legacy_Records()
    {
        var root=Path.Combine(Path.GetTempPath(),"packory-schema-"+Guid.NewGuid().ToString("N"));
        try
        {
            var settings=new Settings{DataDirectory=root};
            var legacy=new Database(settings);legacy.Initialize();
            legacy.UpsertPackage("nuget","legacy.package","2.0.0","Legacy","hosted");
            var v2=new V2Catalog(legacy);v2.Initialize();v2.Initialize();
            Assert.True(legacy.HasPackage("nuget","legacy.package","2.0.0"));
            Assert.True(v2.CreateFeed("new-feed","raw","hosted","private",null));
            Assert.Equal("new-feed",v2.FeedBySlug("new-feed")?.Slug);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
