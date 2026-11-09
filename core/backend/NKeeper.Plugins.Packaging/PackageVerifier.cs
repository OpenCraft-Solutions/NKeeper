using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NKeeper.Plugins.Packaging.Zip;

namespace NKeeper.Plugins.Packaging;

/// <summary>
/// Verifies a plugin package, step by step, in the order of docs/plugin-security.md §5.1 to §5.5.
/// The first failing step refuses the package with its reason code. It works only on the bytes it
/// is given; pinning, dependencies and file-system checks belong to the plugin host.
/// </summary>
public static partial class PackageVerifier
{
    private static readonly string[] NativeExtensions = [".so", ".dylib", ".exe"];

    /// <summary>Verifies <paramref name="package"/>, which was read from a file named <paramref name="fileName"/>.</summary>
    public static PackageVerification Verify(ReadOnlyMemory<byte> package, string fileName, VerificationContext context)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(context);

        // §5.1 Read. Step 1: a plausible file name.
        if (!FileNamePattern().IsMatch(fileName))
        {
            return PackageVerification.Refused(ReasonCodes.BadName, $"'{fileName}' is not named <id>-<version>{PackageFormat.Extension}.", null);
        }

        // Step 2: size.
        if (package.Length > context.Limits.MaxPackageBytes)
        {
            return PackageVerification.Refused(ReasonCodes.TooLarge, $"The package is {package.Length} bytes; at most {context.Limits.MaxPackageBytes} are allowed.", null);
        }

        // Step 3: the hash that pins this exact package.
        var packageSha256 = Convert.ToHexStringLower(SHA256.HashData(package.Span));
        try
        {
            return VerifyContents(package, fileName, packageSha256, context);
        }
        catch (PackageRejectedException ex)
        {
            return PackageVerification.Refused(ex.ReasonCode, ex.Message, packageSha256);
        }
    }

    private static PackageVerification VerifyContents(ReadOnlyMemory<byte> package, string fileName, string packageSha256, VerificationContext context)
    {
        var limits = context.Limits;

        // §5.2 Structure and limits, before anything is decompressed. Step 4: the directory.
        var entries = ZipReader.ReadDirectory(package.Span, limits.MaxEntries);

        // Step 5: every entry's path, type, flags and method.
        CheckEntries(entries);

        // Step 6: declared limits, metadata caps included.
        CheckDeclaredLimits(entries, limits);

        // Step 7: decompress everything through bounded reads.
        var contents = entries.ToDictionary(e => e.Name, e => ZipReader.ReadData(package, e), StringComparer.Ordinal);

        // §5.3 Signature. Step 8: present.
        if (!contents.TryGetValue(PackageFormat.FileListPath, out var fileListBytes) || !contents.TryGetValue(PackageFormat.SignaturePath, out var signatureBytes))
        {
            throw Refuse(ReasonCodes.NotSigned, "The package has no signed file list.");
        }

        if (!PackageSignature.TryParse(signatureBytes, out var signature, out var rawSignature))
        {
            throw Refuse(ReasonCodes.BadSignature, "The signature file is malformed or uses an unsupported algorithm.");
        }

        // Step 9: a trusted, unrevoked, unexpired key.
        var key = context.Trust.Find(signature.KeyId);
        if (key is null || (key.Kind == TrustedKeyKind.Development && !context.IsDevelopment))
        {
            throw Refuse(ReasonCodes.UntrustedKey, key is null
                ? $"Key {signature.KeyId} is not trusted."
                : $"Key {signature.KeyId} is a development key, and this server isn't running in Development.");
        }

        if (context.Trust.Revocations.KeyIds.Contains(key.KeyId))
        {
            throw Refuse(ReasonCodes.RevokedKey, $"Key {key.KeyId} has been revoked.");
        }

        if (key.IsExpired(context.Time.GetUtcNow()))
        {
            throw Refuse(ReasonCodes.ExpiredKey, $"Key {key.KeyId} expired after {key.NotAfter:yyyy-MM-dd}.");
        }

        // Step 10: the signature over the exact bytes of files.json.
        if (!key.Verify(fileListBytes, rawSignature))
        {
            throw Refuse(ReasonCodes.BadSignature, $"The file list's signature doesn't verify with key {key.KeyId}.");
        }

        // Step 11: only now is the signed list parsed.
        if (!FileList.TryParse(fileListBytes, out var fileList, out var listError))
        {
            throw Refuse(ReasonCodes.BadFileList, $"The signed file list is invalid: {listError}");
        }

        // Step 12: the key may sign this ID.
        if (!key.MaySign(fileList.Id))
        {
            throw Refuse(ReasonCodes.KeyScope, $"Key {key.KeyId} may not sign plugin '{fileList.Id}'.");
        }

        // Step 13: the file name is exactly <id>-<version>.nkp.
        if (fileName != PackageFormat.FileName(fileList.Id, fileList.Version))
        {
            throw Refuse(ReasonCodes.IdentityMismatch, $"The package is signed as {fileList.Id} {fileList.Version}, but the file is named '{fileName}'.");
        }

        // §5.4 Contents. Step 14: exactly the listed files.
        var listed = fileList.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var present = contents.Keys.Where(p => p is not (PackageFormat.FileListPath or PackageFormat.SignaturePath)).ToHashSet(StringComparer.Ordinal);
        if (!listed.SetEquals(present))
        {
            var missing = listed.Except(present).FirstOrDefault();
            throw Refuse(ReasonCodes.FileSet, missing is not null
                ? $"Listed file '{missing}' is missing."
                : $"File '{present.Except(listed).First()}' isn't in the signed list.");
        }

        // Step 15: every file's size and hash.
        foreach (var file in fileList.Files)
        {
            var data = contents[file.Path];
            if (data.LongLength != file.Size || Convert.ToHexStringLower(SHA256.HashData(data)) != file.Sha256)
            {
                throw Refuse(ReasonCodes.HashMismatch, $"File '{file.Path}' doesn't match the signed list.");
            }
        }

        // §5.5 Manifest and compatibility. Step 16.
        if (!contents.TryGetValue(PackageFormat.ManifestPath, out var manifestBytes))
        {
            throw Refuse(ReasonCodes.BadManifest, "The package has no manifest.");
        }

        if (!PluginManifest.TryParse(manifestBytes, present, out var manifest, out var manifestError))
        {
            throw Refuse(ReasonCodes.BadManifest, $"The manifest is invalid: {manifestError}");
        }

        if (manifest.Id != fileList.Id || manifest.Version.Text != fileList.Version)
        {
            throw Refuse(ReasonCodes.IdentityMismatch, $"The manifest describes {manifest.Id} {manifest.Version}, but the package is signed as {fileList.Id} {fileList.Version}.");
        }

        // Step 17: SDK compatibility.
        if (!manifest.SdkVersion.Includes(context.SdkVersion))
        {
            throw Refuse(ReasonCodes.SdkIncompatible, $"The plugin needs SDK {manifest.SdkVersion}; this server implements {context.SdkVersion}.");
        }

        // Step 18: no native code, no shared assemblies.
        CheckCode(contents, context.SharedAssemblies);

        // Step 19: this exact package hasn't been revoked.
        if (context.Trust.Revocations.PackageSha256.Contains(packageSha256))
        {
            throw Refuse(ReasonCodes.RevokedPackage, $"Package {packageSha256} has been revoked.");
        }

        var files = fileList.Files.ToDictionary(f => f.Path, f => (ReadOnlyMemory<byte>)contents[f.Path], StringComparer.Ordinal);
        return PackageVerification.Verified(packageSha256, manifest, key.KeyId, files);
    }

    private static void CheckEntries(IReadOnlyList<ZipEntryInfo> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.Name.EndsWith('/') || !PackagePath.IsValid(entry.Name))
            {
                throw Refuse(ReasonCodes.BadEntry, $"Entry '{entry.Name}' has a forbidden path.");
            }

            // Case-insensitively, so two names can't map to one file on a case-insensitive file system.
            if (!seen.Add(entry.Name))
            {
                throw Refuse(ReasonCodes.BadEntry, $"Entry '{entry.Name}' appears more than once.");
            }

            if ((entry.Flags & (ZipEntryInfo.FlagEncrypted | ZipEntryInfo.FlagStrongEncryption)) != 0)
            {
                throw Refuse(ReasonCodes.BadEntry, $"Entry '{entry.Name}' is encrypted.");
            }

            if ((entry.Flags & ZipEntryInfo.FlagDataDescriptor) != 0 || entry.HasZip64Extra)
            {
                throw Refuse(ReasonCodes.BadEntry, $"Entry '{entry.Name}' uses a data descriptor or ZIP64.");
            }

            if (entry.Method is not (ZipEntryInfo.MethodStored or ZipEntryInfo.MethodDeflate))
            {
                throw Refuse(ReasonCodes.BadEntry, $"Entry '{entry.Name}' uses compression method {entry.Method}; only stored and deflate are allowed.");
            }

            if (!IsRegularFile(entry))
            {
                throw Refuse(ReasonCodes.BadEntry, $"Entry '{entry.Name}' is not a regular file.");
            }
        }
    }

    private static bool IsRegularFile(ZipEntryInfo entry)
    {
        var host = entry.VersionMadeBy >> 8;
        return host switch
        {
            // Unix: the mode is in the high 16 bits; zero (no mode recorded) or a regular file.
            ZipEntryInfo.HostUnix => ((entry.ExternalAttributes >> 16) & 0xF000) is 0 or 0x8000,

            // MS-DOS: no directory attribute.
            ZipEntryInfo.HostMsDos => (entry.ExternalAttributes & 0x10) == 0,
            _ => false,
        };
    }

    private static void CheckDeclaredLimits(IReadOnlyList<ZipEntryInfo> entries, PackageLimits limits)
    {
        long total = 0;
        foreach (var entry in entries)
        {
            total += entry.UncompressedSize;
            if (entry.UncompressedSize > 0
                && (entry.CompressedSize == 0 || entry.UncompressedSize > entry.CompressedSize * limits.MaxCompressionRatio))
            {
                throw Refuse(ReasonCodes.Limits, $"Entry '{entry.Name}' compresses more than {limits.MaxCompressionRatio}:1.");
            }

            var cap = entry.Name switch
            {
                PackageFormat.ManifestPath => limits.MaxManifestBytes,
                PackageFormat.FileListPath => limits.MaxFileListBytes,
                PackageFormat.SignaturePath => limits.MaxSignatureBytes,
                _ => long.MaxValue,
            };
            if (entry.UncompressedSize > cap)
            {
                throw Refuse(ReasonCodes.Limits, $"'{entry.Name}' is larger than its {cap}-byte limit.");
            }
        }

        if (total > limits.MaxTotalUncompressedBytes)
        {
            throw Refuse(ReasonCodes.Limits, $"The package decompresses to {total} bytes; at most {limits.MaxTotalUncompressedBytes} are allowed.");
        }
    }

    private static void CheckCode(Dictionary<string, byte[]> contents, IReadOnlySet<string> shared)
    {
        foreach (var (path, data) in contents)
        {
            var extension = Path.GetExtension(path);
            if (NativeExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                throw Refuse(ReasonCodes.NativeLibrary, $"'{path}' is native code, which package format {PackageFormat.Version} doesn't allow.");
            }

            if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = ManagedAssemblyName(data)
                ?? throw Refuse(ReasonCodes.NativeLibrary, $"'{path}' is not an IL-only managed assembly; native code isn't allowed.");
            if (shared.Contains(name))
            {
                throw Refuse(ReasonCodes.SharedAssembly, $"'{path}' is a copy of '{name}', which the server provides to every plugin.");
            }
        }
    }

    /// <summary>The assembly's metadata name, or null when it isn't a managed assembly.</summary>
    private static string? ManagedAssemblyName(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return null;
            }

            // IL-only: a mixed-mode (C++/CLI) image has metadata too, but carries native code (§5.5 step 18).
            var cor = pe.PEHeaders.CorHeader;
            if (cor is null
                || (cor.Flags & CorFlags.ILOnly) == 0
                || (cor.Flags & CorFlags.NativeEntryPoint) != 0
                || cor.ManagedNativeHeaderDirectory.Size != 0)
            {
                return null;
            }

            var metadata = pe.GetMetadataReader();
            return metadata.IsAssembly ? metadata.GetString(metadata.GetAssemblyDefinition().Name) : null;
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException or OverflowException)
        {
            // Malformed PE or metadata: whatever it is, it is not a managed assembly we would load.
            return null;
        }
    }

    private static PackageRejectedException Refuse(string reasonCode, string message) => new(reasonCode, message);

    // Loose on purpose: the exact <id>-<version> match is checked against the signed file list in step 13.
    [GeneratedRegex(@"^[a-z0-9][a-zA-Z0-9.\-+]*\.nkp$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();
}
