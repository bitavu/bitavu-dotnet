using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bitavu;

/// <summary>The templates the API key can read.</summary>
public sealed class BitavuTemplateCatalog
{
    /// <summary>The accessible templates.</summary>
    public required IReadOnlyList<BitavuTemplateListItem> Templates { get; init; }
}

/// <summary>A template in the workspace catalog.</summary>
public sealed class BitavuTemplateListItem
{
    /// <summary>The template ID.</summary>
    public required string Id { get; init; }

    /// <summary>The template name.</summary>
    public required string Name { get; init; }

    /// <summary>Folder names in order, followed by the template name.</summary>
    public required IReadOnlyList<string> Path { get; init; }

    /// <summary>The latest saved revision.</summary>
    public required long LatestRevision { get; init; }

    /// <summary>The link to the latest saved revision in Studio.</summary>
    public required Uri StudioUrl { get; init; }
}

/// <summary>A page of saved template revisions.</summary>
public sealed class BitavuTemplateVersionPage
{
    /// <summary>The saved revisions on this page.</summary>
    public required IReadOnlyList<BitavuTemplateVersion> Versions { get; init; }

    /// <summary>The latest saved revision across all pages.</summary>
    public required BitavuTemplateRevisionSummary Latest { get; init; }

    /// <summary>The selected revision when one was requested and found.</summary>
    public required BitavuTemplateRevisionSummary? Selected { get; init; }

    /// <summary>The page returned by the API, starting at one.</summary>
    public required int Page { get; init; }

    /// <summary>The number of revisions requested per page.</summary>
    public required int PageSize { get; init; }

    /// <summary>The total number of saved revisions.</summary>
    public required int Total { get; init; }

    /// <summary>The total number of pages.</summary>
    public required int Pages { get; init; }
}

/// <summary>Metadata for a saved template revision.</summary>
public class BitavuTemplateRevisionSummary
{
    /// <summary>The template ID.</summary>
    public required string Id { get; init; }

    /// <summary>The template name at this revision.</summary>
    public required string Name { get; init; }

    /// <summary>The saved revision number.</summary>
    public required long Revision { get; init; }

    /// <summary>The creation timestamp in Unix milliseconds, if recorded.</summary>
    [JsonPropertyName("createdAt")]
    public long? CreatedAtUnixMilliseconds { get; init; }

    /// <summary>The update timestamp in Unix milliseconds, if recorded.</summary>
    [JsonPropertyName("updatedAt")]
    public long? UpdatedAtUnixMilliseconds { get; init; }

    /// <summary>The link to this saved revision in Studio.</summary>
    public required Uri StudioUrl { get; init; }
}

/// <summary>A saved revision in the version history.</summary>
public sealed class BitavuTemplateVersion : BitavuTemplateRevisionSummary
{
    /// <summary>The revision title, if set.</summary>
    public required string? Title { get; init; }
}

/// <summary>The input contract for a saved template revision.</summary>
public sealed class BitavuTemplateRevision : BitavuTemplateRevisionSummary
{
    /// <summary>The data fields used by this revision.</summary>
    public required IReadOnlyList<BitavuDataBinding> Bindings { get; init; }

    /// <summary>The saved example JSON object, or null if no example was saved.</summary>
    public required JsonElement? ExampleJson { get; init; }

    /// <summary>The review of the saved example, or null if no example was saved.</summary>
    public required BitavuInputReview? InputReview { get; init; }

    /// <summary>Deserializes the saved example to an application-defined type.</summary>
    /// <param name="jsonOptions">Serialization options. Defaults to the System.Text.Json web defaults.</param>
    /// <returns>The example, or the default value of <typeparamref name="T"/> when no example was saved.</returns>
    /// <exception cref="JsonException">The example cannot be read as <typeparamref name="T"/>.</exception>
    /// <exception cref="NotSupportedException">The requested type is not supported for saved example JSON.</exception>
    public T? DeserializeExample<T>(JsonSerializerOptions? jsonOptions = null)
    {
        if (ExampleJson is not JsonElement example)
            return default;

        try
        {
            return example.Deserialize<T>(jsonOptions ?? JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            throw new JsonException("The saved example does not match the requested type.");
        }
        catch (NotSupportedException)
        {
            throw new NotSupportedException("The requested type is not supported for saved example JSON.");
        }
    }
}

/// <summary>A field the template can bind to.</summary>
public sealed class BitavuDataBinding
{
    /// <summary>The JSON field path used by the template.</summary>
    public required string Path { get; init; }

    /// <summary>The binding type reported by the API.</summary>
    public required string Type { get; init; }

    /// <summary>Whether the template requires a value at this path.</summary>
    public required bool Required { get; init; }
}

/// <summary>Compares a saved example with the template bindings.</summary>
public sealed class BitavuInputReview
{
    /// <summary>Template bindings with no value in the example.</summary>
    public required IReadOnlyList<BitavuMissingInput> Missing { get; init; }

    /// <summary>Example paths the template does not use.</summary>
    public required IReadOnlyList<string> Unused { get; init; }
}

/// <summary>A template binding with no value in the example.</summary>
public sealed class BitavuMissingInput
{
    /// <summary>The missing JSON path.</summary>
    public required string Path { get; init; }

    /// <summary>Whether the template requires a value at this path.</summary>
    public required bool Required { get; init; }
}
