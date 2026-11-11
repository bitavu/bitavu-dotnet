# Bitavu.Client

PDF generation for .NET. Generate PDFs from saved [Bitavu](https://bitavu.com)
templates and JSON data in ASP.NET Core, worker services, WinForms, and WPF. The
client targets .NET 11 and .NET 10 and has no additional package dependencies.
WinForms and WPF projects can use it with `net11.0-windows` or `net10.0-windows`.

```sh
dotnet add package Bitavu.Client --version 0.1.1
```

## Configure the API key

The host application loads `Bitavu:ApiKey` through .NET configuration and
passes it to `BitavuClient`. Use a key with **Generate PDFs** access for the
templates the application renders.

- **Development.** Run `dotnet user-secrets init` in the host project. In your
  IDE's **Manage User Secrets** editor, set `Bitavu:ApiKey` using a development
  key. The default host reads User Secrets in the `Development` environment.
- **Production.** Load `Bitavu:ApiKey` with your deployment's secret manager and
  configuration provider. If the platform injects secrets as environment
  variables, set `Bitavu__ApiKey`. Keep the value out of committed settings and
  logs.

For a private desktop tool, load a key provided by the operator. For a desktop
app distributed to users, keep any shared Bitavu key on your backend and have
the desktop app call your backend.

## Create the client

Create a `BitavuClient` at startup and reuse it for your requests. This
example reads the key from the process environment. Apps that use .NET
configuration can read `configuration["Bitavu:ApiKey"]` and register the
client as a singleton.

```csharp
using Bitavu;

var apiKey = Environment.GetEnvironmentVariable("Bitavu__ApiKey");
if (string.IsNullOrWhiteSpace(apiKey))
    throw new InvalidOperationException("Set Bitavu__ApiKey before starting the app.");

var client = new BitavuClient(apiKey);
```

Client instances share a connection pool and attach their own credentials to
each request. Requests use HTTPS with a two-minute timeout and default to
`api.bitavu.com`. To use a
different Bitavu environment, pass its HTTPS API origin to the constructor
with a key for that environment. Restart the host after rotating the key.

## Use in ASP.NET Core

Install the package in your ASP.NET Core project. In `Program.cs`, read
`Bitavu:ApiKey` from the app's configuration and register a single
`BitavuClient` before calling `builder.Build()`. The default configuration
reads User Secrets in development and maps the `Bitavu__ApiKey` environment
variable to `Bitavu:ApiKey`.

```csharp
using Bitavu;

var builder = WebApplication.CreateBuilder(args);
var apiKey = builder.Configuration["Bitavu:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey))
    throw new InvalidOperationException("Configure Bitavu:ApiKey.");

builder.Services.AddSingleton(new BitavuClient(apiKey));
builder.Services.AddTransient<InventoryReportService>();
```

Define the report service in `InventoryReportService.cs`. ASP.NET Core injects
the registered client through its constructor.

```csharp
using Bitavu;

public sealed class InventoryReportService(BitavuClient client)
{
    public Task<BitavuPdf> RenderAsync(
        string operationId, CancellationToken cancellationToken = default)
    {
        var data = new { report = new { title = "Sample inventory", itemCount = 12 } };
        return client.RenderPdfAsync("sample-inventory", 4, data, operationId,
            cancellationToken: cancellationToken);
    }
}
```

Inject `InventoryReportService` into your controller or other application
code. Pass the caller's cancellation token and an operation ID for the render
job. Reuse that ID with the same input when retrying. Replace the sample
template ID, revision, and data with a request you tested in Studio.

## Render a PDF

In Studio, save a template version and use **Integrations → Prepare a template
request** to get its ID, revision, and sample JSON. Use the template ID,
revision, and data you tested in place of the fictional values below.

```csharp
const string templateId = "sample-inventory";
const long testedRevision = 4;
var data = new
{
    report = new { title = "Sample inventory", itemCount = 12 }
};
var operationId = Guid.NewGuid().ToString("N");
var cancellationToken = CancellationToken.None;
```

Choose one of the two calls below. Both send `POST /v1/render` with the
template ID and data. The method you call determines the JSON revision fields.

### Pin an explicit revision

```csharp
var pdf = await client.RenderPdfAsync(templateId, testedRevision, data, operationId,
    cancellationToken: cancellationToken);
```

The request sends `"revision": 4`. Studio renders exactly that saved revision.

### Follow the latest compatible published revision

```csharp
var pdf = await client.RenderLatestPublishedPdfAsync(templateId, data, operationId,
    baseRevision: testedRevision, cancellationToken: cancellationToken);
```

The request sends `"revision": "latest-published"` and `"baseRevision": 4`.
Studio renders the current publication if its input contract is compatible
with revision 4. An incompatible contract returns HTTP 409
(`JSON_COMPATIBILITY`). A template without a published revision returns
HTTP 404 (`NOT_PUBLISHED`). When you omit `baseRevision`, Studio uses the
first published revision as the baseline.

Both calls return a `BitavuPdf` with the PDF bytes and any resolved revision,
page count, and request ID returned by the API. Use a fresh operation ID for
each intended render. If your application has a cancellation token, pass it
to the render call.

```csharp
await File.WriteAllBytesAsync("inventory.pdf", pdf.Bytes, cancellationToken);
Console.WriteLine($"Rendered revision: {pdf.Revision}");
```

Both render calls accept a `mappingId` for a mapping saved in Studio.
Render and save methods accept your own C# model, a dictionary, or an existing
`JsonElement`. The input must be a JSON object. For C# models, the default
serializer uses camel-case property names. Pass `jsonOptions` if your
template expects a different naming policy or custom converters.

## Saved revisions

`ListTemplatesAsync` returns a `BitavuTemplateCatalog` with folder paths and
latest saved revisions. `GetTemplateVersionsAsync` returns a
`BitavuTemplateVersionPage`. `GetTemplateAsync` returns a
`BitavuTemplateRevision` with the bindings, saved example, and input review.
Use the `StudioUrl` property to open a revision in Studio. These operations
require **Read templates** access. `CheckCredentialsAsync` checks
authentication without accessing templates or consuming render credits.

Use `SaveTemplateExampleAsync(templateId, expectedRevision, exampleJson)` to
store example data on a new saved revision. It requires **Save template
examples** access for the report. The response includes the new revision,
its `StudioUrl`, and its `InputReview` with missing bindings and unused paths.
Use the latest saved revision as `expectedRevision`. On a conflict, read the
latest revision and review its bindings before deciding whether to save again.

```csharp
var catalog = await client.ListTemplatesAsync(cancellationToken);
var template = catalog.Templates.First(t => t.Id == "sample-inventory");
var revision = await client.GetTemplateAsync(template.Id, template.LatestRevision, cancellationToken);

var example = revision.DeserializeExample<InventoryData>();
if (example is not null)
    Console.WriteLine(example.Report.Title);

// ExampleJson is a nullable JsonElement for templates without a C# model.
var rawExample = revision.ExampleJson;
var review = revision.InputReview;

public sealed record InventoryData(InventoryReport Report);
public sealed record InventoryReport(string Title, int ItemCount);
```

## Errors and retries

`BitavuApiException` provides `StatusCode`, `Code`, `RequestId` and `RetryAfter`
when available. The exception message omits the raw API error body. Network and
timeout failures use the standard .NET HTTP and cancellation exceptions.

The client leaves retries to the caller. Keep the operation ID with your job if
it may be retried. After an uncertain outcome, reuse the ID with the same input
and respect `RetryAfter` when supplied. A new ID requests a new render and may
consume credits. If a retry reports an idempotency conflict, review the request
and its resolved revision before starting another render.

## License

MIT
