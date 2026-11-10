using System.Text.Json;
using System.Text.Json.Nodes;

namespace NKeeper.Abstractions;

/// <summary>What a plugin adds to core's catalogues, from <see cref="IPlugin.Contribute"/> (SDK 0.1.2).</summary>
public interface IPluginContributions
{
    /// <summary>Contributes a kind of service hosts can offer (docs/architecture.md §7.4).</summary>
    void AddServiceType(ServiceType serviceType);

    /// <summary>Contributes a kind of session users open on a service (docs/architecture.md §7.10, SDK 0.1.5).</summary>
    void AddSessionType(SessionType sessionType);
}

/// <summary>
/// A kind of service, such as SSH: its ID, label, default port and settings (docs/architecture.md §7.4).
/// Core checks it at startup and refuses the plugin if it breaks a rule.
/// </summary>
/// <param name="id">Lower-case letters, digits and <c>-</c>, starting with a letter, up to 32 characters. Unique across plugins.</param>
/// <param name="label">What users see, e.g. <c>SSH</c>.</param>
public sealed class ServiceType(string id, string label)
{
    public string Id { get; } = id;

    public string Label { get; } = label;

    /// <summary>The port a service of this type listens on unless it says otherwise.</summary>
    public int? DefaultPort { get; init; }

    /// <summary>The settings a service of this type has, in the order the editor shows them.</summary>
    public IReadOnlyList<ServiceSetting> Settings { get; init; } = [];

    /// <summary>The version of <see cref="Settings"/>. Raise it when the settings change, and give an <see cref="Upgrade"/>.</summary>
    public int SettingsVersion { get; init; } = 1;

    /// <summary>
    /// Brings settings stored at an older version (the first argument) up to <see cref="SettingsVersion"/>.
    /// Pure and deterministic: core calls it on read, and doesn't save the result.
    /// </summary>
    public Func<int, JsonObject, JsonObject>? Upgrade { get; init; }
}

public enum ServiceSettingKind
{
    /// <summary>A string, up to <see cref="ServiceSetting.MaxLength"/> characters.</summary>
    Text,

    /// <summary>A whole number, between <see cref="ServiceSetting.Minimum"/> and <see cref="ServiceSetting.Maximum"/> when set.</summary>
    Number,

    /// <summary>Yes or no.</summary>
    Switch,

    /// <summary>One of <see cref="ServiceSetting.Choices"/>.</summary>
    Choice,
}

/// <summary>One field of a service type's settings. Made with <see cref="Text"/>, <see cref="Number"/>, <see cref="Switch"/> or <see cref="Choice"/>.</summary>
public sealed class ServiceSetting
{
    private ServiceSetting(string key, string label, ServiceSettingKind kind, bool required)
    {
        Key = key;
        Label = label;
        Kind = kind;
        Required = required;
    }

    /// <summary>The field's name in the settings object: a camel-case identifier.</summary>
    public string Key { get; }

    public string Label { get; }

    public ServiceSettingKind Kind { get; }

    /// <summary>Whether a service must set it.</summary>
    public bool Required { get; }

    public int? MaxLength { get; private init; }

    public long? Minimum { get; private init; }

    public long? Maximum { get; private init; }

    public IReadOnlyList<string> Choices { get; private init; } = [];

    /// <param name="maxLength">At most 4000.</param>
    public static ServiceSetting Text(string key, string label, int maxLength = 200, bool required = false) =>
        new(key, label, ServiceSettingKind.Text, required) { MaxLength = maxLength };

    public static ServiceSetting Number(string key, string label, long? minimum = null, long? maximum = null, bool required = false) =>
        new(key, label, ServiceSettingKind.Number, required) { Minimum = minimum, Maximum = maximum };

    public static ServiceSetting Switch(string key, string label, bool required = false) =>
        new(key, label, ServiceSettingKind.Switch, required);

    public static ServiceSetting Choice(string key, string label, IReadOnlyList<string> choices, bool required = false) =>
        new(key, label, ServiceSettingKind.Choice, required) { Choices = [.. choices] };
}

/// <summary>
/// One of a host's services, as a host-scoped endpoint sees it (SDK 0.1.2). The port and address have
/// their defaults applied.
/// </summary>
/// <param name="Port">The service's port, else its type's default; null when neither is set.</param>
/// <param name="Address">The service's own address, else the host's (its IP address over its hostname).</param>
/// <param name="Settings">A JSON object, in the type's current settings version.</param>
/// <param name="Available">Whether an enabled plugin provides the service's type. When it doesn't, the settings are as stored.</param>
public sealed record HostService(Guid Id, string Type, string? Label, int? Port, string? Address, JsonElement Settings, bool Available);
