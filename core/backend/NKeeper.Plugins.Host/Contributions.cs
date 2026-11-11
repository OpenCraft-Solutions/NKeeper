using System.Text.RegularExpressions;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.Host;

/// <summary>A service type, with the plugin that contributed it.</summary>
public sealed record ContributedServiceType(string PluginId, ServiceType Type);

/// <summary>A session type, with the plugin that contributed it.</summary>
public sealed record ContributedSessionType(string PluginId, SessionType Type);

/// <summary>A host action, with the plugin that contributed it.</summary>
public sealed record ContributedHostAction(string PluginId, HostAction Action);

/// <summary>Collects one plugin's contributions while its <see cref="IPlugin.Contribute"/> runs.</summary>
internal sealed class ContributionCollector : IPluginContributions
{
    public List<ServiceType> ServiceTypes { get; } = [];

    public void AddServiceType(ServiceType serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ServiceTypes.Add(serviceType);
    }

    public List<SessionType> SessionTypes { get; } = [];

    public void AddSessionType(SessionType sessionType)
    {
        ArgumentNullException.ThrowIfNull(sessionType);
        SessionTypes.Add(sessionType);
    }

    public List<HostAction> HostActions { get; } = [];

    public void AddHostAction(HostAction hostAction)
    {
        ArgumentNullException.ThrowIfNull(hostAction);
        HostActions.Add(hostAction);
    }
}

/// <summary>The rules a contribution must follow (docs/architecture.md §7.4). Each returns a problem, or null.</summary>
public static partial class ContributionRules
{
    public const int MaxTypeIdLength = 32;
    public const int MaxLabelLength = 100;
    public const int MaxTextLength = 4000;

    public static string? Check(ServiceType type)
    {
        if (type.Id is null || !TypeId().IsMatch(type.Id))
        {
            return $"The service type ID '{type.Id}' isn't lower-case letters, digits and '-', starting with a letter, up to {MaxTypeIdLength} characters.";
        }

        if (string.IsNullOrWhiteSpace(type.Label) || type.Label.Length > MaxLabelLength)
        {
            return $"Service type '{type.Id}' needs a label of at most {MaxLabelLength} characters.";
        }

        if (type.DefaultPort is < 1 or > 65535)
        {
            return $"Service type '{type.Id}' has a default port outside 1 to 65535.";
        }

        if (type.SettingsVersion < 1)
        {
            return $"Service type '{type.Id}' has settings version {type.SettingsVersion}; versions start at 1.";
        }

        if (type.SettingsVersion > 1 && type.Upgrade is null)
        {
            return $"Service type '{type.Id}' is at settings version {type.SettingsVersion} but has no Upgrade for older settings.";
        }

        if (type.Settings is null)
        {
            return $"Service type '{type.Id}' has no settings list; use an empty one for none.";
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in type.Settings)
        {
            if (setting is null)
            {
                return $"Service type '{type.Id}' has a null setting.";
            }

            if (setting.Key is null || !SettingKey().IsMatch(setting.Key))
            {
                return $"Service type '{type.Id}' has a setting key '{setting.Key}' that isn't a camel-case identifier.";
            }

            if (!keys.Add(setting.Key))
            {
                return $"Service type '{type.Id}' has two settings named '{setting.Key}'.";
            }

            if (string.IsNullOrWhiteSpace(setting.Label) || setting.Label.Length > MaxLabelLength)
            {
                return $"Setting '{setting.Key}' of service type '{type.Id}' needs a label of at most {MaxLabelLength} characters.";
            }

            var problem = setting.Kind switch
            {
                ServiceSettingKind.Text when setting.MaxLength is not (>= 1 and <= MaxTextLength) => $"needs a maximum length from 1 to {MaxTextLength}",
                ServiceSettingKind.Number when setting.Minimum > setting.Maximum => "has a minimum above its maximum",
                ServiceSettingKind.Choice when setting.Choices.Count == 0 => "has no choices",
                ServiceSettingKind.Choice when setting.Choices.Any(string.IsNullOrEmpty) => "has an empty choice",
                ServiceSettingKind.Choice when setting.Choices.Distinct(StringComparer.Ordinal).Count() != setting.Choices.Count => "lists a choice twice",
                ServiceSettingKind.Text or ServiceSettingKind.Number or ServiceSettingKind.Switch or ServiceSettingKind.Choice => null,
                _ => "is of an unknown kind",
            };
            if (problem is not null)
            {
                return $"Setting '{setting.Key}' of service type '{type.Id}' {problem}.";
            }
        }

        return null;
    }

    public static string? Check(SessionType type)
    {
        if (type.Id is null || !TypeId().IsMatch(type.Id))
        {
            return $"The session type ID '{type.Id}' isn't lower-case letters, digits and '-', starting with a letter, up to {MaxTypeIdLength} characters.";
        }

        if (string.IsNullOrWhiteSpace(type.Label) || type.Label.Length > MaxLabelLength)
        {
            return $"Session type '{type.Id}' needs a label of at most {MaxLabelLength} characters.";
        }

        if (type.Handler is null || !type.Handler.IsClass || type.Handler.IsAbstract || !typeof(IStreamSessionHandler).IsAssignableFrom(type.Handler))
        {
            return $"Session type '{type.Id}' has a handler that isn't a concrete {nameof(IStreamSessionHandler)}.";
        }

        if (type.ServiceTypes is null || type.ServiceTypes.Count == 0)
        {
            return $"Session type '{type.Id}' names no service types to open on.";
        }

        foreach (var serviceType in type.ServiceTypes)
        {
            if (serviceType is null || !TypeId().IsMatch(serviceType))
            {
                return $"Session type '{type.Id}' names a service type '{serviceType}' that isn't a valid ID.";
            }
        }

        return null;
    }

    public const int MaxActionIdLength = 64;

    /// <param name="sessionTypes">The session types the same plugin contributes.</param>
    public static string? Check(HostAction action, IReadOnlyCollection<SessionType> sessionTypes)
    {
        if (action.Id is null || action.Id.Length > MaxActionIdLength || !ActionId().IsMatch(action.Id))
        {
            return $"The host action ID '{action.Id}' isn't segments of lower-case letters, digits and '-', each starting with a letter, joined by '.', up to {MaxActionIdLength} characters.";
        }

        if (string.IsNullOrWhiteSpace(action.Label) || action.Label.Length > MaxLabelLength)
        {
            return $"Host action '{action.Id}' needs a label of at most {MaxLabelLength} characters.";
        }

        if (!sessionTypes.Any(t => t.Id == action.SessionType))
        {
            return $"Host action '{action.Id}' opens session type '{action.SessionType}', which the plugin doesn't contribute.";
        }

        return Enum.IsDefined(action.Credential) ? null : $"Host action '{action.Id}' has an unknown credential mode.";
    }

    // \z, not $: in .NET, $ also matches before a final newline.
    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,31}\z")]
    private static partial Regex TypeId();

    [GeneratedRegex(@"^[a-z][a-z0-9-]*(?:\.[a-z][a-z0-9-]*)*\z")]
    private static partial Regex ActionId();

    [GeneratedRegex(@"^[a-z][A-Za-z0-9]{0,63}\z")]
    private static partial Regex SettingKey();
}
