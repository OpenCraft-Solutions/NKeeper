using System.Text.Json;
using System.Text.RegularExpressions;
using NKeeper.Core.Persistence;

namespace NKeeper.Identity;

/// <summary>A colour scheme a user built: the four colours the shell derives the full palette from (docs/architecture.md §6.1).</summary>
public sealed record CustomColorScheme(string Id, string Name, string Background, string Surface, string Text, string Accent);

/// <summary>The scheme the user uses, a preset's ID or one of theirs, and the schemes they built.</summary>
public sealed record ColorSchemesResponse(string Active, IReadOnlyList<CustomColorScheme> Custom);

public sealed record ColorSchemesRequest(string? Active, IReadOnlyList<CustomColorScheme?>? Custom);

/// <summary>Reads, checks and stores a user's colour schemes, ported from 1.x. The server checks only their form: the presets are the shell's.</summary>
public static partial class ColorSchemes
{
    public const string Default = "navy";
    public const int MaxCustom = 20;
    public const int MaxNameLength = 40;

    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[a-z0-9-]{1,64}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    public static ColorSchemesResponse Of(ApplicationUser user) => new(user.ColorScheme, Parse(user.CustomColorSchemes));

    /// <summary>The stored list. A bad value mustn't stop the user signing in, so it reads as empty.</summary>
    public static IReadOnlyList<CustomColorScheme> Parse(string stored)
    {
        try
        {
            return JsonSerializer.Deserialize<List<CustomColorScheme>>(stored, StoredJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Checks a request and applies it to the account.</summary>
    /// <exception cref="AccountValidationException">A field is malformed.</exception>
    public static void Apply(ApplicationUser user, ColorSchemesRequest? request)
    {
        var active = request?.Active ?? string.Empty;
        if (!IdPattern().IsMatch(active))
        {
            throw new AccountValidationException("active", "Choose a colour scheme.");
        }

        var custom = request?.Custom ?? [];
        if (custom.Count > MaxCustom)
        {
            throw new AccountValidationException("custom", $"You can keep up to {MaxCustom} colour schemes.");
        }

        for (var i = 0; i < custom.Count; i++)
        {
            var scheme = custom[i];
            var field = $"custom[{i}]";
            if (scheme is null || !IdPattern().IsMatch(scheme.Id ?? string.Empty))
            {
                throw new AccountValidationException($"{field}.id", "This colour scheme has no valid ID.");
            }

            if (string.IsNullOrWhiteSpace(scheme.Name) || scheme.Name.Length > MaxNameLength)
            {
                throw new AccountValidationException($"{field}.name", $"Give the colour scheme a name of at most {MaxNameLength} characters.");
            }

            foreach (var (name, value) in new[] { ("background", scheme.Background), ("surface", scheme.Surface), ("text", scheme.Text), ("accent", scheme.Accent) })
            {
                if (!ColorPattern().IsMatch(value ?? string.Empty))
                {
                    throw new AccountValidationException($"{field}.{name}", "Use a colour as #rrggbb.");
                }
            }
        }

        if (custom.Select(s => s!.Id).Distinct(StringComparer.Ordinal).Count() != custom.Count)
        {
            throw new AccountValidationException("custom", "Colour scheme IDs must be unique.");
        }

        user.ColorScheme = active;
        user.CustomColorSchemes = JsonSerializer.Serialize(custom.Select(s => s! with { Name = s.Name.Trim() }).ToList(), StoredJson);
    }
}
