using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection.PortableExecutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bitavu;

const string fixtureKey = "synthetic-test-value";
const string operationId = "synthetic-operation-001";
const string fixtureRequestId = "00000000-0000-4000-8000-000000000001";
var fixtureData = JsonSerializer.SerializeToElement(new { itemCount = 12 });
var frameworkVersion = new FrameworkName(AppContext.TargetFrameworkName!).Version;
var framework = $"net{frameworkVersion.Major}.{frameworkVersion.Minor}";

using (var package = ZipFile.OpenRead(args.Single()))
using (var packedDll = package.GetEntry($"lib/{framework}/Bitavu.Client.dll")!.Open())
using (var buffer = new MemoryStream())
{
    packedDll.CopyTo(buffer);
    buffer.Position = 0;
    using var pe = new PEReader(buffer, PEStreamOptions.LeaveOpen);
    Require(pe.HasMetadata && pe.PEHeaders.CorHeader!.ResourcesDirectory.Size == 0, "Assembly contains unexpected managed resources.");
    Require(pe.ReadDebugDirectory().All(entry => entry.Type == DebugDirectoryEntryType.Reproducible), "Assembly contains a debug path or symbols.");
    Require(SHA256.HashData(buffer.ToArray()).SequenceEqual(SHA256.HashData(File.ReadAllBytes(typeof(BitavuClient).Assembly.Location))),
        "The verification program did not load the exact packed DLL. Use a fresh restore cache.");
}

using var handler = new FixtureHandler();
using var http = new HttpClient(handler);
var client = new BitavuClient(fixtureKey, http);

async Task<HttpResponseMessage> CheckPinnedRender(HttpRequestMessage request)
{
    CheckRequest(request, HttpMethod.Post, "/v1/render", "application/pdf", fixtureKey);
    Require(request.Headers.GetValues("Idempotency-Key").Single() == operationId, "Incorrect operation ID.");
    Require(request.Content?.Headers.ContentType?.MediaType == "application/json", "Incorrect render content type.");
    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
    var json = body.RootElement;
    Require(json.GetProperty("templateId").GetString() == "sample-inventory", "Incorrect template ID.");
    Require(json.GetProperty("revision").GetInt64() == 7, "Incorrect pinned revision.");
    Require(json.GetProperty("data").GetProperty("itemCount").GetInt32() == 12, "Incorrect document data.");
    Require(!json.TryGetProperty("baseRevision", out _) && !json.TryGetProperty("mappingId", out _), "Optional fields must be omitted.");
    return Pdf();
}
handler.Add(CheckPinnedRender);
var result = await client.RenderPdfAsync("sample-inventory", 7, fixtureData, operationId);
Require(result.Bytes.AsSpan().StartsWith("%PDF-"u8) && result.Revision == 7 && result.PageCount == 2 &&
    result.RequestId == Guid.Parse(fixtureRequestId), "Incorrect PDF result or headers.");
handler.Add(CheckPinnedRender);
await client.RenderPdfAsync("sample-inventory", 7, new InventoryData(12), operationId);

var snakeCaseOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
for (var call = 0; call < 2; call++)
{
    handler.Add(async request =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        var json = body.RootElement;
        Require(json.GetProperty("revision").GetString() == "latest-published" && json.GetProperty("baseRevision").GetInt64() == 3 &&
            json.GetProperty("mappingId").GetString() == "synthetic-mapping", "Incorrect latest-published body.");
        Require(json.GetProperty("data").GetProperty("item_count").GetInt32() == 12, "Custom data naming policy was ignored.");
        Require(request.Headers.GetValues("Idempotency-Key").Single() == operationId + "-latest", "Retry changed the operation ID.");
        return Pdf();
    });
    await client.RenderLatestPublishedPdfAsync(
        "sample-inventory", new InventoryData(12), operationId + "-latest", 3, "synthetic-mapping", jsonOptions: snakeCaseOptions);
}

handler.Add(request =>
{
    CheckRequest(request, HttpMethod.Get, "/v1/credentials/check", "application/json", fixtureKey);
    Require(request.Content is null, "Credential checks must not send content.");
    return Task.FromResult(Json("""{"authenticated":true}"""));
});
await client.CheckCredentialsAsync();

handler.Add(request =>
{
    CheckRequest(request, HttpMethod.Get, "/v1/templates", "application/json", fixtureKey);
    return Task.FromResult(Json("""
        {"templates":[{"id":"sample-inventory","name":"Sample inventory","path":["Examples","Sample inventory"],
          "latestRevision":7,"studioUrl":"https://studio.example.test/?template=sample-inventory&revision=7"}]}
        """));
});
var catalog = await client.ListTemplatesAsync();
var item = catalog.Templates.Single();
Require(item.Id == "sample-inventory" && item.LatestRevision == 7 && item.Path.SequenceEqual(["Examples", item.Name]) &&
    item.StudioUrl.Query.EndsWith("revision=7", StringComparison.Ordinal), "Incorrect template listing.");

handler.Add(request =>
{
    CheckRequest(request, HttpMethod.Get, "/v1/templates/sample-inventory?revision=7", "application/json", fixtureKey);
    return Task.FromResult(Json("""
        {"id":"sample-inventory","name":"Sample inventory","revision":7,"createdAt":1735689600000,
          "studioUrl":"https://studio.example.test/?template=sample-inventory&revision=7",
          "bindings":[{"path":"itemCount","type":"number","required":true},{"path":"items[].sku","type":"string","required":false}],
          "exampleJson":{"itemCount":12,"source":{"warehouse":["A",null]}},
          "inputReview":{"missing":[{"path":"items[].sku","required":false}],"unused":["source.warehouse"]},
          "futureMetadata":true}
        """));
});
var savedTemplate = await client.GetTemplateAsync("sample-inventory", 7);
var binding = savedTemplate.Bindings[0];
Require(savedTemplate.Revision == 7 && savedTemplate.CreatedAtUnixMilliseconds == 1735689600000 &&
    savedTemplate.UpdatedAtUnixMilliseconds is null && binding is { Path: "itemCount", Type: "number", Required: true },
    "Incorrect saved template details.");
Require(savedTemplate.InputReview?.Missing.Single() is { Path: "items[].sku", Required: false } &&
    savedTemplate.InputReview.Unused.Single() == "source.warehouse", "Incorrect input review.");
Require(savedTemplate.DeserializeExample<InventoryData>() is { ItemCount: 12 } &&
    savedTemplate.ExampleJson?.GetProperty("source").GetProperty("warehouse")[1].ValueKind == JsonValueKind.Null &&
    savedTemplate.DeserializeExample<Dictionary<string, JsonElement>>()?["source"].ValueKind == JsonValueKind.Object,
    "Could not read the saved example as a model, dictionary or raw JSON.");
var exampleFormatError = await Fails<JsonException>(() => Task.FromResult(savedTemplate.DeserializeExample<Dictionary<string, bool>>()));
Require(exampleFormatError.Path is null && exampleFormatError.InnerException is null, "Saved example parse failure exposed JSON details.");
var unsupportedExample = await Fails<NotSupportedException>(() =>
    Task.FromResult(savedTemplate.DeserializeExample<Dictionary<string, IntPtr>>()));
Require(unsupportedExample.Message == "The requested type is not supported for saved example JSON." &&
    unsupportedExample.InnerException is null, "Unsupported example model exposed JSON details.");

handler.Add(_ => Task.FromResult(Json("""
    {"id":"sample-inventory","name":"Sample inventory","revision":6,
      "studioUrl":"https://studio.example.test/?template=sample-inventory&revision=6",
      "bindings":[],"exampleJson":null,"inputReview":null}
    """)));
var noExample = await client.GetTemplateAsync("sample-inventory", 6);
Require(noExample.ExampleJson is null && noExample.InputReview is null &&
    noExample.DeserializeExample<InventoryData>() is null, "An absent example must remain absent.");

handler.Add(async request =>
{
    CheckRequest(request, HttpMethod.Post, "/v1/templates/sample-inventory/example", "application/json", fixtureKey);
    Require(request.Content?.Headers.ContentType?.MediaType == "application/json", "Incorrect example content type.");
    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
    Require(body.RootElement.EnumerateObject().Count() == 2 &&
        body.RootElement.GetProperty("expectedRevision").GetInt64() == 7 &&
        JsonElement.DeepEquals(body.RootElement.GetProperty("exampleJson"), fixtureData), "Incorrect example save payload.");
    return Json("""
        {"id":"sample-inventory","name":"Sample inventory","revision":8,
          "studioUrl":"https://studio.example.test/?template=sample-inventory&revision=8",
          "bindings":[{"path":"itemCount","type":"number","required":true}],
          "exampleJson":{"itemCount":12},"inputReview":{"missing":[],"unused":[]}}
        """, HttpStatusCode.Created);
});
var example = await client.SaveTemplateExampleAsync("sample-inventory", 7, new InventoryData(12));
Require(example.Revision == 8 && example.StudioUrl.Query.EndsWith("revision=8", StringComparison.Ordinal) &&
    example.InputReview?.Missing.Count == 0, "Incorrect saved example response.");
handler.Add(_ => Task.FromResult(Json("""{"code":"TEMPLATE_REVISION_CONFLICT","message":"SYNTHETIC_PRIVATE_VALUE"}""", HttpStatusCode.Conflict)));
var staleExample = await Fails<BitavuApiException>(() => client.SaveTemplateExampleAsync("sample-inventory", 7, fixtureData));
Require(staleExample.StatusCode == HttpStatusCode.Conflict && staleExample.Code == "TEMPLATE_REVISION_CONFLICT" &&
    !staleExample.ToString().Contains("SYNTHETIC_PRIVATE_VALUE", StringComparison.Ordinal), "Incorrect stale revision error.");
await Fails<ArgumentException>(() => client.SaveTemplateExampleAsync("sample-inventory", 7, JsonSerializer.SerializeToElement(new[] { 1 })));
await Fails<ArgumentException>(() => client.SaveTemplateExampleAsync("sample-inventory", 7, (InventoryData?)null));
await Fails<ArgumentOutOfRangeException>(() => client.SaveTemplateExampleAsync("sample-inventory", 0, fixtureData));

handler.Add(request =>
{
    CheckRequest(request, HttpMethod.Get, "/v1/templates/sample-inventory/versions?summary=true&page=2&pageSize=5", "application/json", fixtureKey);
    return Task.FromResult(Json("""
        {"versions":[{"id":"sample-inventory","name":"Sample inventory","revision":2,"title":"Updated inventory layout",
            "studioUrl":"https://studio.example.test/?template=sample-inventory&revision=2"}],
          "latest":{"id":"sample-inventory","name":"Sample inventory","revision":7,
            "studioUrl":"https://studio.example.test/?template=sample-inventory&revision=7"},
          "selected":null,"page":2,"pageSize":5,"total":6,"pages":2}
        """));
});
var versions = await client.GetTemplateVersionsAsync("sample-inventory", 2, 5);
Require(versions is { Page: 2, PageSize: 5, Total: 6, Pages: 2, Latest.Revision: 7, Selected: null } &&
    versions.Versions.Single() is { Revision: 2, Title: "Updated inventory layout" }, "Incorrect version summaries.");

foreach (var invalidCatalog in new[] { "{}", """{"templates":null}""", """{"templates":"SYNTHETIC_PRIVATE_VALUE"}""" })
{
    handler.Add(_ =>
    {
        var response = Json(invalidCatalog);
        response.Headers.Add("X-Bitavu-Request-Id", fixtureRequestId);
        return Task.FromResult(response);
    });
    var invalidResponse = await Fails<BitavuApiException>(() => client.ListTemplatesAsync());
    Require(invalidResponse.RequestId == Guid.Parse(fixtureRequestId) && invalidResponse.InnerException is null &&
        !invalidResponse.ToString().Contains("SYNTHETIC_PRIVATE_VALUE", StringComparison.Ordinal),
        "Invalid contract response lost request metadata or exposed response values.");
}

handler.Add(_ =>
{
    var response = Json("""{"code":"API_VERSION_LIMIT","message":"SYNTHETIC_PRIVATE_VALUE","details":["SYNTHETIC_DOCUMENT"]}""", HttpStatusCode.TooManyRequests);
    response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
    response.Headers.Add("X-Bitavu-Request-Id", fixtureRequestId);
    return Task.FromResult(response);
});
var rateLimit = await Fails<BitavuApiException>(() => client.RenderPdfAsync("sample-inventory", 7, fixtureData, operationId));
Require(rateLimit.StatusCode == HttpStatusCode.TooManyRequests && rateLimit.Code == "API_VERSION_LIMIT" &&
    rateLimit.RetryAfter == TimeSpan.FromSeconds(5) && rateLimit.RequestId == Guid.Parse(fixtureRequestId), "Missing rate limit details.");
Require(!rateLimit.ToString().Contains("SYNTHETIC_", StringComparison.Ordinal), "Raw server data leaked into an exception.");

handler.Add(_ => Task.FromResult(Json("""{"code":"SYNTHETIC_UPPERCASE_VALUE"}""", HttpStatusCode.Unauthorized)));
var secondClient = new BitavuClient("SYNTHETIC_UPPERCASE_VALUE", http);
Require((await Fails<BitavuApiException>(() => secondClient.CheckCredentialsAsync())).Code is null, "The key leaked into the error code.");

foreach (var invalidKey in new[] { "", " ", "synthetic\r\nvalue", "=synthetic", "synthetic=value" })
{
    var invalid = await Fails<ArgumentException>(() => Task.FromResult(new BitavuClient(invalidKey)));
    Require(invalid.ParamName == "apiKey" && !invalid.ToString().Contains("synthetic", StringComparison.Ordinal),
        "Invalid key validation included credential material.");
}

handler.Add(request =>
{
    CheckRequest(request, HttpMethod.Get, "/v1/credentials/check", "application/json", "synthetic-padded-value=");
    return Task.FromResult(Json("""{"authenticated":true}"""));
});
handler.Add(request =>
{
    CheckRequest(request, HttpMethod.Get, "/v1/credentials/check", "application/json", fixtureKey);
    return Task.FromResult(Json("""{"authenticated":true}"""));
});
await Task.WhenAll(new BitavuClient("synthetic-padded-value=", http).CheckCredentialsAsync(), client.CheckCredentialsAsync());

var alternateOrigin = new Uri("https://api-example.bitavu.com/");
handler.Add(request =>
{
    Require(request.RequestUri == new Uri(alternateOrigin, "v1/credentials/check") &&
        request.Headers.Authorization?.Parameter == fixtureKey, "Request did not use the selected Bitavu origin.");
    return Task.FromResult(Json("""{"authenticated":true}"""));
});
await new BitavuClient(fixtureKey, http, alternateOrigin).CheckCredentialsAsync();

foreach (var invalidOrigin in new[] { "/v1", "http://api.bitavu.com", "https://api.bitavu.com:8443",
             "https://api.bitavu.com/v1", "https://api.bitavu.com/?test=1", "https://api.bitavu.com/#test",
             "https://api.bitavu.com.example.test", "https://example@api.bitavu.com", "https://studio.bitavu.com",
             "https://127.0.0.1:5180/" })
{
    var invalid = await Fails<ArgumentException>(() => Task.FromResult(new BitavuClient(fixtureKey, new Uri(invalidOrigin, UriKind.RelativeOrAbsolute))));
    Require(invalid.ParamName == "apiOrigin", "Unexpected origin validation error.");
}

handler.Add(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)));
Require((await Fails<BitavuApiException>(() => client.CheckCredentialsAsync())).StatusCode == HttpStatusCode.TemporaryRedirect,
    "Unexpected redirect handling.");

handler.Add(_ => Task.FromResult(Json("""{"authenticated":false}""")));
await Fails<BitavuApiException>(() => client.CheckCredentialsAsync());
handler.Add(_ => Task.FromResult(Json("{broken")));
Require((await Fails<BitavuApiException>(() => client.CheckCredentialsAsync())).InnerException is null, "Unexpected JSON details leaked.");
handler.Add(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent("Not a PDF", Encoding.UTF8, "application/pdf"),
}));
await Fails<BitavuApiException>(() => client.RenderPdfAsync("sample-inventory", 7, fixtureData, operationId));

await Fails<ArgumentException>(() => client.RenderPdfAsync("sample-inventory", 7, fixtureData, "bad-key"));
await Fails<ArgumentException>(() => client.RenderPdfAsync("sample-inventory", 7, JsonSerializer.SerializeToElement(new[] { 1 }), operationId));
await Fails<ArgumentException>(() => client.RenderPdfAsync("sample-inventory", 7, new[] { 1 }, operationId));
var unsupportedData = await Fails<NotSupportedException>(() => client.RenderPdfAsync(
    "sample-inventory", 7, new Dictionary<string, IntPtr> { ["itemCount"] = new IntPtr(1) }, operationId));
Require(unsupportedData.Message == "The supplied data type is not supported for JSON serialization." &&
    unsupportedData.InnerException is null, "Unsupported render model exposed JSON details.");
await Fails<ArgumentException>(() => client.GetTemplateAsync("../credentials/check", 1));
await Fails<ArgumentOutOfRangeException>(() => client.GetTemplateVersionsAsync("sample-inventory", pageSize: 51));
await Fails<ArgumentOutOfRangeException>(() => client.GetTemplateAsync("sample-inventory", 0));
using var canceled = new CancellationTokenSource();
await canceled.CancelAsync();
await Fails<OperationCanceledException>(() => client.RenderPdfAsync("sample-inventory", 7, fixtureData, operationId, cancellationToken: canceled.Token));
await Fails<OperationCanceledException>(() => client.RenderPdfAsync("sample-inventory", 7, new InventoryData(12), operationId, cancellationToken: canceled.Token));
Require(handler.Pending == 0, "A fixture response was not consumed.");
Console.WriteLine("Packed client passed transport, rendering, reads, errors, input validation and assembly checks.");

HttpResponseMessage Pdf()
{
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent("%PDF-1.7\n%%EOF"u8.ToArray()),
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
    response.Headers.Add("X-Bitavu-Revision", "7");
    response.Headers.Add("X-Bitavu-Pages", "2");
    response.Headers.Add("X-Bitavu-Request-Id", fixtureRequestId);
    return response;
}

static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
{
    Content = new StringContent(body, Encoding.UTF8, "application/json"),
};

static void CheckRequest(HttpRequestMessage request, HttpMethod method, string path, string accept, string key)
{
    Require(request.RequestUri?.AbsoluteUri == "https://api.bitavu.com" + path && request.Method == method, "Incorrect URL or method.");
    Require(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == key, "Incorrect authentication.");
    Require(request.Headers.Accept.Single().MediaType == accept, "Incorrect Accept header.");
    Require(!request.Headers.Contains("Cookie"), "Unexpected cookie.");
}

static async Task<T> Fails<T>(Func<Task> run) where T : Exception
{
    try { await run(); }
    catch (T error) { return error; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static void Require(bool condition, string failure)
{
    if (!condition)
        throw new InvalidOperationException(failure);
}

sealed record InventoryData(int ItemCount);

sealed class FixtureHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> responses = new();
    public int Pending => responses.Count;
    public void Add(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) => responses.Enqueue(response);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return responses.Dequeue()(request);
    }
}
