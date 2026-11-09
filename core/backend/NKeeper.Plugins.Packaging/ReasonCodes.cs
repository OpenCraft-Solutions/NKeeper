namespace NKeeper.Plugins.Packaging;

/// <summary>
/// Why a package was refused. Each code names one step of docs/plugin-security.md §5, and the
/// verifier stops at the first step that fails, so a package has exactly one reason.
/// </summary>
public static class ReasonCodes
{
    /// <summary>§5.1: the file name isn't a plausible <c>&lt;id&gt;-&lt;version&gt;.nkp</c>.</summary>
    public const string BadName = "bad_name";

    /// <summary>§5.1: the package is larger than the limit.</summary>
    public const string TooLarge = "too_large";

    /// <summary>§5.2: the ZIP structure is malformed, inconsistent, or its data doesn't match.</summary>
    public const string BadZip = "bad_zip";

    /// <summary>§5.2: an entry has a forbidden path, type, flag or compression method.</summary>
    public const string BadEntry = "bad_entry";

    /// <summary>§5.2: a size, count or compression-ratio limit is exceeded.</summary>
    public const string Limits = "limits";

    /// <summary>§5.3: the file list or its signature is missing.</summary>
    public const string NotSigned = "unsigned";

    /// <summary>§5.3: the signing key isn't trusted here (unknown, or a development key outside Development).</summary>
    public const string UntrustedKey = "untrusted_key";

    /// <summary>§5.3: the signing key has been revoked.</summary>
    public const string RevokedKey = "revoked_key";

    /// <summary>§5.3: the signing key's validity has ended.</summary>
    public const string ExpiredKey = "expired_key";

    /// <summary>§5.3: the signature is malformed or doesn't verify.</summary>
    public const string BadSignature = "bad_signature";

    /// <summary>§5.3: the signed file list is malformed.</summary>
    public const string BadFileList = "bad_file_list";

    /// <summary>§5.3: the key may not sign this plugin ID.</summary>
    public const string KeyScope = "key_scope";

    /// <summary>§5.3, §5.5: the file name, file list and manifest disagree about the plugin's ID or version.</summary>
    public const string IdentityMismatch = "identity_mismatch";

    /// <summary>§5.4: the package doesn't contain exactly the listed files.</summary>
    public const string FileSet = "file_set";

    /// <summary>§5.4: a file's size or SHA-256 doesn't match the signed list.</summary>
    public const string HashMismatch = "hash_mismatch";

    /// <summary>§5.5: the manifest is malformed or invalid.</summary>
    public const string BadManifest = "bad_manifest";

    /// <summary>§5.5: the plugin isn't compatible with this server's SDK version.</summary>
    public const string SdkIncompatible = "sdk_incompatible";

    /// <summary>§5.5: the package contains native code, which format version 1 forbids.</summary>
    public const string NativeLibrary = "native_library";

    /// <summary>§5.5: the package contains its own copy of an assembly the server shares with plugins.</summary>
    public const string SharedAssembly = "shared_assembly";

    /// <summary>§5.5: this exact package has been revoked.</summary>
    public const string RevokedPackage = "revoked_package";
}
