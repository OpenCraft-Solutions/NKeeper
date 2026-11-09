namespace NKeeper.Plugins.Packaging;

/// <summary>Fixed names and values of package format version 1 (docs/plugin-security.md §3).</summary>
public static class PackageFormat
{
    /// <summary>The only package format version this build reads and writes.</summary>
    public const int Version = 1;

    /// <summary>A package's file extension.</summary>
    public const string Extension = ".nkp";

    /// <summary>The manifest's path in a package.</summary>
    public const string ManifestPath = "nkeeper-plugin.json";

    /// <summary>The signed file list's path in a package.</summary>
    public const string FileListPath = "files.json";

    /// <summary>The signature's path in a package.</summary>
    public const string SignaturePath = "files.json.sig";

    /// <summary>The only signature algorithm: ECDSA on P-256 with SHA-256, IEEE P1363 encoding.</summary>
    public const string SignatureAlgorithm = "ECDSA-P256-SHA256";

    /// <summary>The folder a package's backend assemblies live in.</summary>
    public const string BackendFolder = "backend/";

    /// <summary>The folder a package's frontend files live in.</summary>
    public const string FrontendFolder = "frontend/";

    /// <summary>The package file name for a plugin version: <c>&lt;id&gt;-&lt;version&gt;.nkp</c>.</summary>
    public static string FileName(string id, string version) => $"{id}-{version}{Extension}";
}
