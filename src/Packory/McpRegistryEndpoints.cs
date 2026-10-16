using System.Text.Json;
using System.Text.Json.Nodes;

namespace Packory;

// Explicitly a metadata registry, never an MCP tool gateway or execution environment.
public static class McpRegistryEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/v0.1/servers",(string? search,string? cursor,int? limit,HttpContext ctx,Settings s,V2Catalog db)=>
        {
            if(ScopedAccess.Require(ctx,s,db,null,"catalog.read") is { } denied)return denied;
            var all=db.Mcp().Where(x=>search is null||x.Name.Contains(search,StringComparison.OrdinalIgnoreCase)).ToArray();
            var start=0;
            if(cursor is not null && (!int.TryParse(cursor,out start)||start<0||start>10000))return Results.BadRequest();
            var length=Math.Clamp(limit??50,1,100);
            var slice=all.Skip(start).Take(length).ToArray();
            var next=start+slice.Length<all.Length?(start+slice.Length).ToString():null;
            return Results.Json(new {servers=slice.Select(RegistryResponse).ToArray(),metadata=new {nextCursor=next,count=slice.Length}});
        });
        app.MapGet("/v0.1/servers/{name}/versions",(string name,HttpContext ctx,Settings s,V2Catalog db)=>
        {
            if(ScopedAccess.Require(ctx,s,db,null,"catalog.read") is { } denied)return denied;
            var list=db.Mcp(name);return list.Count==0?Results.NotFound():Results.Json(new {servers=list.Select(RegistryResponse).ToArray()});
        });
        app.MapGet("/v0.1/servers/{name}/versions/{version}",(string name,string version,HttpContext ctx,Settings s,V2Catalog db)=>
        {
            if(ScopedAccess.Require(ctx,s,db,null,"catalog.read") is { } denied)return denied;
            var list=db.Mcp(name);var item=version=="latest"?list.LastOrDefault():list.FirstOrDefault(x=>x.Version==version);
            return item is null?Results.NotFound():Results.Json(RegistryResponse(item));
        });
        app.MapPost("/v0.1/publish",async (HttpContext ctx,Settings s,V2Catalog db)=>
        {
            // Admin-only until publisher namespace ownership verification is implemented.
            if(ScopedAccess.Require(ctx,s,db,null,"system.admin") is { } denied)return denied;
            if(ctx.Request.ContentLength>s.MaxMetadataBytes)return Results.StatusCode(413);
            using var input=new MemoryStream();
            var bytes=await BlobStore.ReadBoundedAsync(ctx.Request.Body,Math.Min(s.MaxMetadataBytes,256*1024),ctx.RequestAborted);
            JsonObject? json;
            try{json=JsonNode.Parse(bytes) as JsonObject;}
            catch(JsonException){return Results.BadRequest(new {error="Invalid server.json"});}
            var name=json?["name"]?.GetValue<string>();var version=json?["version"]?.GetValue<string>();
            if(name is null||version is null||name.Length>250||!name.Contains('/')||
                name.Any(char.IsWhiteSpace)||!Names.Version(version)||json?["description"] is null)
                return Results.BadRequest(new {error="Missing name, version or description"});
            if(!db.AddMcp(name,version,json!.ToJsonString(),"quarantined"))return Results.Conflict();
            db.Audit(ScopedAccess.Actor(ctx,s,db),"mcp.publish",name+"/"+version,"quarantined");
            return Results.Json(new {name,version,state="quarantined",requiresApproval=true},statusCode:201);
        });
        app.MapPost("/api/v1/mcp/{name}/versions/{version}/approve",(string name,string version,HttpContext ctx,Settings s,V2Catalog db)=>
        {
            if(ScopedAccess.Require(ctx,s,db,null,"system.admin") is { } denied)return denied;
            if(!db.ChangeMcpState(name,version,"approved"))return Results.NotFound();
            db.Audit(ScopedAccess.Actor(ctx,s,db),"mcp.approve",name+"/"+version);return Results.NoContent();
        });
    }

    private static object RegistryResponse(McpRecord record)=>new
    {
        server=JsonNode.Parse(record.ManifestJson),
        _meta=new Dictionary<string,object>{["io.modelcontextprotocol.registry/official"]=new
        {
            status="active",publishedAt=record.CreatedAt,updatedAt=record.CreatedAt,isLatest=true
        }}
    };
}
