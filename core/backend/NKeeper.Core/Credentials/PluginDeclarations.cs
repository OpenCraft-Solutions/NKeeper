namespace NKeeper.Core.Credentials;

/// <summary>What plugins' signed manifests declare that core's credential handling needs to know (plugin-security.md §12.3).</summary>
public interface IPluginDeclarations
{
    /// <summary>Why the plugin needs a credential's plaintext, if its manifest declares it (SDK 1.10); otherwise null.</summary>
    string? CredentialRelease(string pluginId);
}

/// <summary>No plugin declares anything: no plaintext is ever released. The server replaces it with the loaded manifests.</summary>
internal sealed class NoPluginDeclarations : IPluginDeclarations
{
    public string? CredentialRelease(string pluginId) => null;
}
