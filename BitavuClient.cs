using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bitavu;

/// <summary>A .NET client for the Bitavu v1 API. Instances can be reused across concurrent calls.</summary>
public sealed partial class BitavuClient
{
    private const long MaxRevision = 9_007_199_254_740_991;
    private static readonly Uri DefaultApiOrigin = new("https://api.bitavu.com/");
    private static readonly JsonSerializerOptions ContractJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
    };
    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(120),
        MaxResponseContentBufferSize = 32 * 1024 * 1024,
    };

    private readonly HttpClient httpClient;
    private readonly Uri apiOrigin;
    private readonly AuthenticationHeaderValue authorization;

    /// <summary>Creates a reusable client with the API key supplied by the host application.</summary>
    /// <exception cref="ArgumentException">The API key is missing or has an invalid Bearer token format.</exception>
    public BitavuClient(string apiKey) : this(apiKey, DefaultApiOrigin) { }

    /// <summary>Creates a client for an explicitly selected Bitavu API origin.</summary>
    /// <remarks>Use an API key issued for the selected environment.</remarks>
    public BitavuClient(string apiKey, Uri apiOrigin) : this(apiKey, SharedHttpClient, apiOrigin) { }

    internal BitavuClient(string apiKey, HttpClient httpClient) : this(apiKey, httpClient, DefaultApiOrigin) { }

    internal BitavuClient(string apiKey, HttpClient httpClient, Uri apiOrigin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (!BearerTokenPattern().IsMatch(apiKey))
            throw new ArgumentException("Provide a valid Bearer API key.", nameof(apiKey));
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(apiOrigin);
        if (!apiOrigin.IsAbsoluteUri || apiOrigin.Scheme != Uri.UriSchemeHttps || !apiOrigin.IsDefaultPort ||
            apiOrigin.UserInfo.Length != 0 || apiOrigin.AbsolutePath != "/" ||
            apiOrigin.Query.Length != 0 || apiOrigin.Fragment.Length != 0 || !ApiHostPattern().IsMatch(apiOrigin.IdnHost))
            throw new ArgumentException("Provide an HTTPS Bitavu API origin.", nameof(apiOrigin));

        authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        this.httpClient = httpClient;
        this.apiOrigin = apiOrigin;
    }

    /// <summary>Checks the API key without accessing templates or consuming render credits.</summary>
    public async Task CheckCredentialsAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "v1/credentials/check", "application/json");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await ReadJsonObjectAsync(response, cancellationToken).ConfigureAwait(false);
        if (!json.TryGetProperty("authenticated", out var authenticated) || authenticated.ValueKind != JsonValueKind.True)
            throw UnexpectedResponse(response);
    }

    /// <summary>Lists the templates the API key can read, with their paths and latest saved revisions.</summary>
    public Task<BitavuTemplateCatalog> ListTemplatesAsync(CancellationToken cancellationToken = default) =>
        ReadTemplateAsync<BitavuTemplateCatalog>("v1/templates", cancellationToken);

    /// <summary>Gets integration details for a pinned revision, including its bindings and saved example.</summary>
    public Task<BitavuTemplateRevision> GetTemplateAsync(string templateId, long revision, CancellationToken cancellationToken = default)
    {
        ValidateTemplateId(templateId);
        ValidateRevision(revision, nameof(revision));
        return ReadTemplateAsync<BitavuTemplateRevision>(
            FormattableString.Invariant($"v1/templates/{templateId}?revision={revision}"), cancellationToken);
    }

    /// <summary>Saves example JSON as a new revision of an allowed template and returns its integration details.</summary>
    /// <remarks>Requires Save template examples access. Use the latest saved revision as the expected revision.</remarks>
    public async Task<BitavuTemplateRevision> SaveTemplateExampleAsync(
        string templateId, long expectedRevision, JsonElement exampleJson, CancellationToken cancellationToken = default)
    {
        ValidateTemplateId(templateId);
        ValidateRevision(expectedRevision, nameof(expectedRevision));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(expectedRevision, MaxRevision);
        if (exampleJson.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Example JSON must be an object.", nameof(exampleJson));

        using var request = CreateRequest(HttpMethod.Post, $"v1/templates/{templateId}/example", "application/json");
        request.Content = JsonContent.Create(new { expectedRevision, exampleJson });
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadContractAsync<BitavuTemplateRevision>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves an application-defined object as example JSON on a new template revision.</summary>
    /// <remarks>Provide serialization options to customize the example JSON. The default uses camel-case property names.</remarks>
    [OverloadResolutionPriority(-1)]
    public Task<BitavuTemplateRevision> SaveTemplateExampleAsync<TExample>(
        string templateId, long expectedRevision, TExample exampleJson,
        JsonSerializerOptions? jsonOptions = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SaveTemplateExampleAsync(templateId, expectedRevision, SerializeData(exampleJson, jsonOptions), cancellationToken);
    }

    /// <summary>Gets a page of saved-version summaries.</summary>
    public Task<BitavuTemplateVersionPage> GetTemplateVersionsAsync(
        string templateId, int page = 1, int pageSize = 10, CancellationToken cancellationToken = default)
    {
        ValidateTemplateId(templateId);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, 10_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 50);
        return ReadTemplateAsync<BitavuTemplateVersionPage>(FormattableString.Invariant(
            $"v1/templates/{templateId}/versions?summary=true&page={page}&pageSize={pageSize}"), cancellationToken);
    }

    /// <summary>Renders a PDF from a pinned saved revision and a JSON object.</summary>
    /// <remarks>Use a unique idempotency key for each intended render. Reuse it for a retry of the same request.</remarks>
    public Task<BitavuPdf> RenderPdfAsync(
        string templateId, long revision, JsonElement data, string idempotencyKey,
        string? mappingId = null, CancellationToken cancellationToken = default)
    {
        ValidateRevision(revision, nameof(revision));
        return RenderAsync(templateId, revision, null, data, idempotencyKey, mappingId, cancellationToken);
    }

    /// <summary>Renders a PDF from a pinned saved revision and an application-defined data object.</summary>
    /// <remarks>Provide serialization options to customize the render data. The default uses camel-case property names.</remarks>
    [OverloadResolutionPriority(-1)]
    public Task<BitavuPdf> RenderPdfAsync<TData>(
        string templateId, long revision, TData data, string idempotencyKey,
        string? mappingId = null, JsonSerializerOptions? jsonOptions = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RenderPdfAsync(templateId, revision, SerializeData(data, jsonOptions), idempotencyKey, mappingId, cancellationToken);
    }

    /// <summary>Renders a PDF from the latest published revision using an optional tested baseline.</summary>
    /// <remarks>Reuse the same idempotency key and request for a retry. A changed publication may cause a conflict.</remarks>
    public Task<BitavuPdf> RenderLatestPublishedPdfAsync(
        string templateId, JsonElement data, string idempotencyKey, long? baseRevision = null,
        string? mappingId = null, CancellationToken cancellationToken = default)
    {
        if (baseRevision is long baseline)
            ValidateRevision(baseline, nameof(baseRevision));

        return RenderAsync(templateId, null, baseRevision, data, idempotencyKey, mappingId, cancellationToken);
    }

    /// <summary>Renders a PDF from the latest published revision and an application-defined data object.</summary>
    /// <remarks>Provide serialization options to customize the render data. The default uses camel-case property names.</remarks>
    [OverloadResolutionPriority(-1)]
    public Task<BitavuPdf> RenderLatestPublishedPdfAsync<TData>(
        string templateId, TData data, string idempotencyKey, long? baseRevision = null,
        string? mappingId = null, JsonSerializerOptions? jsonOptions = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RenderLatestPublishedPdfAsync(
            templateId, SerializeData(data, jsonOptions), idempotencyKey, baseRevision, mappingId, cancellationToken);
    }

    private async Task<BitavuPdf> RenderAsync(
        string templateId, long? revision, long? baseRevision, JsonElement data, string idempotencyKey,
        string? mappingId, CancellationToken cancellationToken)
    {
        ValidateTemplateId(templateId);
        if (data.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Render data must be a JSON object.", nameof(data));
        if (string.IsNullOrEmpty(idempotencyKey) || !IdempotencyKeyPattern().IsMatch(idempotencyKey))
            throw new ArgumentException("Use 16 to 128 ASCII letters, digits, underscores or hyphens.", nameof(idempotencyKey));
        if (mappingId is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(mappingId);

        cancellationToken.ThrowIfCancellationRequested();
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("templateId", templateId);
            if (revision is long pinnedRevision)
                writer.WriteNumber("revision", pinnedRevision);
            else
                writer.WriteString("revision", "latest-published");
            if (baseRevision is long baseline)
                writer.WriteNumber("baseRevision", baseline);
            if (mappingId is not null)
                writer.WriteString("mappingId", mappingId);
            writer.WritePropertyName("data");
            data.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var request = CreateRequest(HttpMethod.Post, "v1/render", "application/pdf");
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = new ByteArrayContent(body.GetBuffer(), 0, checked((int)body.Length));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!HasMediaType(response, "application/pdf"))
            throw UnexpectedResponse(response);

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!bytes.AsSpan().StartsWith("%PDF-"u8))
            throw UnexpectedResponse(response);

        return new BitavuPdf(
            bytes,
            long.TryParse(GetHeader(response, "X-Bitavu-Revision"), NumberStyles.None, CultureInfo.InvariantCulture, out var resolvedRevision)
                && resolvedRevision is > 0 and <= MaxRevision ? resolvedRevision : null,
            int.TryParse(GetHeader(response, "X-Bitavu-Pages"), NumberStyles.None, CultureInfo.InvariantCulture, out var pages)
                && pages > 0 ? pages : null,
            GetRequestId(response));
    }

    private async Task<T> ReadTemplateAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        using var request = CreateRequest(HttpMethod.Get, path, "application/json");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadContractAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadContractAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) where T : class
    {
        if (!HasMediaType(response, "application/json"))
            throw UnexpectedResponse(response);

        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, ContractJsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw UnexpectedResponse(response);
        }
        catch (JsonException)
        {
            throw UnexpectedResponse(response);
        }
    }

    private static JsonElement SerializeData<T>(T data, JsonSerializerOptions? jsonOptions)
    {
        try
        {
            return JsonSerializer.SerializeToElement(data, jsonOptions ?? JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            throw new JsonException("The supplied data could not be serialized to JSON.");
        }
        catch (NotSupportedException)
        {
            throw new NotSupportedException("The supplied data type is not supported for JSON serialization.");
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string accept)
    {
        var request = new HttpRequestMessage(method, new Uri(apiOrigin, path));
        request.Headers.Authorization = authorization;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            string? errorCode = null;
            if (HasMediaType(response, "application/json") && response.Content.Headers.ContentLength is <= 65_536)
            {
                var json = await TryReadJsonObjectAsync(response, cancellationToken).ConfigureAwait(false);
                if (json is JsonElement body && body.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String &&
                    code.GetString() is string candidate && ErrorCodePattern().IsMatch(candidate) &&
                    !candidate.Equals(authorization.Parameter, StringComparison.Ordinal))
                    errorCode = candidate;
            }

            var retry = response.Headers.RetryAfter;
            TimeSpan? delay = retry?.Delta;
            if (retry?.Date is DateTimeOffset date)
                delay = date - DateTimeOffset.UtcNow;
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;

            throw new BitavuApiException(response.StatusCode, errorCode, GetRequestId(response), delay);
        }
    }

    private static async Task<JsonElement> ReadJsonObjectAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var json = HasMediaType(response, "application/json")
            ? await TryReadJsonObjectAsync(response, cancellationToken).ConfigureAwait(false) : null;
        return json ?? throw UnexpectedResponse(response);
    }

    private static async Task<JsonElement?> TryReadJsonObjectAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasMediaType(HttpResponseMessage response, string mediaType) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, mediaType, StringComparison.OrdinalIgnoreCase);

    private static string? GetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static Guid? GetRequestId(HttpResponseMessage response) =>
        Guid.TryParseExact(GetHeader(response, "X-Bitavu-Request-Id"), "D", out var id) ? id : null;

    private static BitavuApiException UnexpectedResponse(HttpResponseMessage response) =>
        new(response.StatusCode, null, GetRequestId(response), null, invalidResponse: true);

    private static void ValidateTemplateId(string templateId)
    {
        if (string.IsNullOrEmpty(templateId) || !TemplateIdPattern().IsMatch(templateId))
            throw new ArgumentException("Use 1 to 80 ASCII letters, digits, underscores or hyphens.", nameof(templateId));
    }

    private static void ValidateRevision(long revision, string parameterName)
    {
        if (revision is < 1 or > MaxRevision)
            throw new ArgumentOutOfRangeException(parameterName, "Use a positive saved revision number.");
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,80}\z")]
    private static partial Regex TemplateIdPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{16,128}\z")]
    private static partial Regex IdempotencyKeyPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_.~+/-]+=*\z")]
    private static partial Regex BearerTokenPattern();

    [GeneratedRegex(@"\Aapi(?:-[a-z0-9-]+)?\.bitavu\.com\z")]
    private static partial Regex ApiHostPattern();

    [GeneratedRegex(@"\A[A-Z][A-Z0-9_]{1,63}\z")]
    private static partial Regex ErrorCodePattern();
}
