using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class NugetUpstreamIntegrityTests
{
    private const string Id = "test.nuget.package";
    private const string Version = "1.2.3";
    private const string Sidecar =
        "https://api.nuget.org/v3-flatcontainer/test.nuget.package/1.2.3/test.nuget.package.1.2.3.nupkg.sha512";
    private const string Index =
        "https://api.nuget.org/v3/registration5-semver2/test.nuget.package/index.json";
    private const string Catalog =
        "https://api.nuget.org/v3/catalog0/data/2026.11.23/test.nuget.package.1.2.3.json";

    private static readonly byte[] Package = Encoding.UTF8.GetBytes("synthetic package content");

    private sealed class Requests(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<string> Paths = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Paths.Add(uri.AbsoluteUri);
            return Task.FromResult(respond(uri));
        }
    }

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) {
            Content = new StringContent(JsonSerializer.Serialize(value),
                Encoding.UTF8, "application/json")
        };

    private static object Registration(string catalog = Catalog) =>
        new {
            items = new[] { new {
                items = new[] { new Dictionary<string, object> {
                    ["@id"] = "https://api.nuget.org/v3/registration5-semver2/test.nuget.package/1.2.3.json",
                    ["catalogEntry"] = new Dictionary<string, string> {
                        ["@id"] = catalog, ["id"] = Id, ["version"] = Version
                    }
                } }
            } }
        };

    private static object CatalogDocument(byte[] package, string? hash = null,
        string algorithm = "SHA512") =>
        new {
            id = Id,
            version = Version,
            packageHashAlgorithm = algorithm,
            packageHash = hash ?? Convert.ToBase64String(SHA512.HashData(package)),
            packageSize = package.Length
        };

    private static async Task<(Exception? Error, List<string> Requests)> Exercise(
        Func<Uri, HttpResponseMessage> respond, byte[]? package = null,
        Settings? settings = null)
    {
        var handler = new Requests(respond);
        using var http = new HttpClient(handler);
        Exception? error = await Record.ExceptionAsync(() =>
            NugetUpstreamIntegrity.VerifyAsync(settings ?? new Settings(),
                http, Id, Version, package ?? Package, CancellationToken.None));
        return (error, handler.Paths);
    }

    [Fact]
    public async Task AcceptsCanonicalSidecarHashWithoutCatalogLookup()
    {
        var hash = Convert.ToBase64String(SHA512.HashData(Package));
        var result = await Exercise(uri => {
            Assert.Equal(Sidecar, uri.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(hash)
            };
        });
        Assert.Null(result.Error);
        Assert.Equal(new[] { Sidecar }, result.Requests);
    }

    [Fact]
    public async Task UsesOfficialCatalogHashWhenSidecarIsNotFound()
    {
        var result = await Exercise(uri => uri.AbsoluteUri switch {
            Sidecar => new(HttpStatusCode.NotFound),
            Index => Json(Registration()),
            Catalog => Json(CatalogDocument(Package)),
            _ => throw new Exception("Unexpected upstream request")
        });
        Assert.Null(result.Error);
        Assert.Equal(new[] { Sidecar, Index, Catalog }, result.Requests);
    }

    [Fact]
    public async Task RejectsCatalogDigestMismatchWithoutCachingUnverifiedBytes()
    {
        var result = await Exercise(uri => uri.AbsoluteUri switch {
            Sidecar => new(HttpStatusCode.NotFound),
            Index => Json(Registration()),
            Catalog => Json(CatalogDocument(Package, hash:
                Convert.ToBase64String(SHA512.HashData("other"u8.ToArray())))),
            _ => throw new Exception("Unexpected upstream request")
        });
        Assert.IsType<InvalidDataException>(result.Error);
        Assert.Equal(new[] { Sidecar, Index, Catalog }, result.Requests);
    }

    [Fact]
    public async Task DoesNotFollowOffRegistryCatalogReferences()
    {
        var result = await Exercise(uri => uri.AbsoluteUri switch {
            Sidecar => new(HttpStatusCode.NotFound),
            Index => Json(Registration("https://redirect.invalid/v3/catalog/leak.json")),
            _ => throw new Exception("Untrusted request must not be made")
        });
        Assert.IsType<InvalidDataException>(result.Error);
        Assert.Equal(new[] { Sidecar, Index }, result.Requests);
    }

    [Fact]
    public async Task RejectsMissingAndMalformedOfficialIntegrityEvidence()
    {
        foreach (var response in new[] { HttpStatusCode.InternalServerError,
            HttpStatusCode.Forbidden })
        {
            var result = await Exercise(uri => {
                Assert.Equal(Sidecar, uri.AbsoluteUri);
                return new HttpResponseMessage(response);
            });
            Assert.IsType<InvalidDataException>(result.Error);
            Assert.Equal(new[] { Sidecar }, result.Requests);
        }

        var invalidCatalog = await Exercise(uri => uri.AbsoluteUri switch {
            Sidecar => new(HttpStatusCode.NotFound),
            Index => Json(Registration()),
            Catalog => Json(CatalogDocument(Package, algorithm: "SHA256")),
            _ => throw new Exception("Unexpected upstream request")
        });
        Assert.IsType<InvalidDataException>(invalidCatalog.Error);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    public async Task RejectsNonObjectRegistrationMetadata(string payload)
    {
        var result = await Exercise(uri => uri.AbsoluteUri switch {
            Sidecar => new(HttpStatusCode.NotFound),
            Index => new(HttpStatusCode.OK) {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            },
            _ => throw new Exception("Unexpected upstream request")
        });
        Assert.IsType<InvalidDataException>(result.Error);
        Assert.Equal(new[] { Sidecar, Index }, result.Requests);
    }

    [Fact]
    public async Task RejectsNonObjectExternalRegistrationPage()
    {
        const string page = "https://api.nuget.org/v3/registration5-semver2/test.nuget.package/page/1.0.0/2.0.0.json";
        var result = await Exercise(uri => uri.AbsoluteUri switch {
            Sidecar => new(HttpStatusCode.NotFound),
            Index => Json(new { items = new[] { new Dictionary<string, string> { ["@id"] = page } } }),
            page => Json(Array.Empty<object>()),
            _ => throw new Exception("Unexpected upstream request")
        });
        Assert.IsType<InvalidDataException>(result.Error);
        Assert.Equal(new[] { Sidecar, Index, page }, result.Requests);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"25\"")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task RejectsNonNumericCatalogPackageSize(string payload)
    {
        using var size = JsonDocument.Parse(payload);
        var result = await Exercise(uri => uri.AbsoluteUri switch {
            Sidecar => new(HttpStatusCode.NotFound),
            Index => Json(Registration()),
            Catalog => Json(new {
                id = Id,
                version = Version,
                packageHashAlgorithm = "SHA512",
                packageHash = Convert.ToBase64String(SHA512.HashData(Package)),
                packageSize = size.RootElement
            }),
            _ => throw new Exception("Unexpected upstream request")
        });
        Assert.IsType<InvalidDataException>(result.Error);
        Assert.Equal(new[] { Sidecar, Index, Catalog }, result.Requests);
    }

    [Fact]
    public async Task DoesNotUseCatalogForUnrelatedCustomUpstreams()
    {
        var config = new Settings {
            NugetUpstreamUrl = "https://packages.example.invalid/flat/",
            NugetRegistrationUrl = "https://packages.example.invalid/registration/"
        };
        var result = await Exercise(uri =>
            new HttpResponseMessage(HttpStatusCode.NotFound), settings: config);
        Assert.IsType<InvalidDataException>(result.Error);
        Assert.Single(result.Requests);
        Assert.StartsWith("https://packages.example.invalid/flat/", result.Requests[0]);
    }
}
