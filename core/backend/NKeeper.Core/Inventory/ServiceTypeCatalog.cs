using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NKeeper.Abstractions;

namespace NKeeper.Core.Inventory;

/// <summary>
/// The service types the enabled plugins contributed (docs/architecture.md §7.4), already checked by the
/// plugin host. Validates services' settings against them, and upgrades stored settings on read.
/// </summary>
public sealed class ServiceTypeCatalog(IEnumerable<ServiceType> types, ILogger<ServiceTypeCatalog> logger)
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    private readonly Dictionary<string, ServiceType> _types = types.ToDictionary(t => t.Id, StringComparer.Ordinal);

    /// <returns>The type, or null when no enabled plugin provides it.</returns>
    public ServiceType? Find(string id) => _types.GetValueOrDefault(id);

    /// <summary>
    /// Settings as a service sent them, checked against <paramref name="type"/>'s schema: unknown keys
    /// are refused, nulls and empty text are unset, and text is trimmed.
    /// </summary>
    /// <param name="field">The request field the settings came in, for errors: <c>services[0].settings</c>.</param>
    /// <exception cref="InventoryValidationException">A setting breaks the schema.</exception>
    public static JsonObject Validate(ServiceType type, JsonElement? settings, string field)
    {
        var result = new JsonObject();
        if (settings is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } given)
        {
            if (given.ValueKind != JsonValueKind.Object)
            {
                throw new InventoryValidationException(field, "Settings are an object of named values.");
            }

            var schema = type.Settings.ToDictionary(s => s.Key, StringComparer.Ordinal);
            foreach (var property in given.EnumerateObject())
            {
                var key = $"{field}.{property.Name}";
                if (!schema.TryGetValue(property.Name, out var setting))
                {
                    throw new InventoryValidationException(key, $"{type.Label} services have no setting '{property.Name}'.");
                }

                if (Checked(setting, property.Value, key) is { } value)
                {
                    result[setting.Key] = value;
                }
                else
                {
                    result.Remove(setting.Key);
                }
            }
        }

        foreach (var setting in type.Settings.Where(s => s.Required && !result.ContainsKey(s.Key)))
        {
            throw new InventoryValidationException($"{field}.{setting.Key}", $"Enter {setting.Label}.");
        }

        return result;
    }

    /// <summary>
    /// A stored service's settings, in its type's current version: upgraded in memory when older, never
    /// saved. Settings of an unavailable type, or that can't be upgraded, are returned as stored.
    /// </summary>
    public (JsonElement Settings, ServiceType? Type) Read(Persistence.HostService service)
    {
        var type = Find(service.Type);
        var stored = Parse(service.Settings);
        if (type is null || service.SettingsVersion >= type.SettingsVersion || type.Upgrade is null)
        {
            return (stored, type);
        }

        try
        {
            var upgraded = type.Upgrade(service.SettingsVersion, JsonNode.Parse(service.Settings) as JsonObject ?? []);
            return (JsonSerializer.SerializeToElement(upgraded), type);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Service type {Type} couldn't upgrade the settings of service {ServiceId} from version {Version}; returning them as stored.", type.Id, service.Id, service.SettingsVersion);
            return (stored, type);
        }
    }

    private static JsonElement Parse(string json)
    {
        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(json);
            return element.ValueKind == JsonValueKind.Object ? element : EmptyObject;
        }
        catch (JsonException)
        {
            return EmptyObject;
        }
    }

    /// <returns>The value to store, or null to leave the setting unset.</returns>
    private static JsonValue? Checked(ServiceSetting setting, JsonElement value, string key)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        switch (setting.Kind)
        {
            case ServiceSettingKind.Text:
                if (value.ValueKind != JsonValueKind.String)
                {
                    throw new InventoryValidationException(key, "Enter text.");
                }

                var text = value.GetString()!.Trim();
                return text.Length > setting.MaxLength
                    ? throw new InventoryValidationException(key, $"Use at most {setting.MaxLength} characters.")
                    : text.Length == 0 ? null : JsonValue.Create(text);

            case ServiceSettingKind.Number:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
                {
                    throw new InventoryValidationException(key, "Enter a whole number.");
                }

                return number < setting.Minimum || number > setting.Maximum
                    ? throw new InventoryValidationException(key, (setting.Minimum, setting.Maximum) switch
                    {
                        ({ } min, { } max) => $"Enter a number from {min} to {max}.",
                        ({ } min, null) => $"Enter a number of at least {min}.",
                        _ => $"Enter a number of at most {setting.Maximum}.",
                    })
                    : JsonValue.Create(number);

            case ServiceSettingKind.Switch:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? JsonValue.Create(value.GetBoolean())
                    : throw new InventoryValidationException(key, "Choose yes or no.");

            case ServiceSettingKind.Choice:
                return value.ValueKind == JsonValueKind.String && setting.Choices.Contains(value.GetString()!, StringComparer.Ordinal)
                    ? JsonValue.Create(value.GetString())
                    : throw new InventoryValidationException(key, $"Choose one of: {string.Join(", ", setting.Choices)}.");

            default:
                throw new InventoryValidationException(key, "This setting can't be set.");
        }
    }
}
