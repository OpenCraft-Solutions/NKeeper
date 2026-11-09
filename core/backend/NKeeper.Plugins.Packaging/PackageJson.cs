using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NKeeper.Plugins.Packaging;

/// <summary>Strict JSON for package metadata: exact camelCase names, no unknown fields, no comments, required fields present.</summary>
internal static class PackageJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 16,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool TryDeserialize<T>(ReadOnlySpan<byte> json, [NotNullWhen(true)] out T? value, [NotNullWhen(false)] out string? error)
        where T : class
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json, Options);
            error = value is null ? "The document is null." : null;
            return value is not null;
        }
        catch (JsonException ex)
        {
            value = null;
            error = ex.Message;
            return false;
        }
    }
}
