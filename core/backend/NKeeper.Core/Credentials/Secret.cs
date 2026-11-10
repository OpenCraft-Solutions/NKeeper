using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace NKeeper.Core.Credentials;

/// <summary>
/// A decrypted secret inside core (docs/architecture.md §7.5). It prints as <c>***</c>, refuses to be
/// serialized, and clears its buffer when disposed. Plugins never get one.
/// </summary>
[JsonConverter(typeof(SecretJsonConverter))]
public sealed class Secret : IDisposable
{
    private char[]? _value;

    public Secret(ReadOnlySpan<char> value) => _value = value.ToArray();

    /// <returns>A secret, or null for an empty or missing value.</returns>
    public static Secret? From(string? value) => string.IsNullOrEmpty(value) ? null : new Secret(value);

    /// <summary>The plaintext, for core's own protocol code. Never give it to a plugin.</summary>
    /// <exception cref="ObjectDisposedException">The secret was disposed.</exception>
    public string Reveal() => new(_value ?? throw new ObjectDisposedException(nameof(Secret)));

    public override string ToString() => "***";

    public void Dispose()
    {
        if (_value is not null)
        {
            Array.Clear(_value);
            _value = null;
        }
    }
}

public sealed class SecretJsonConverter : JsonConverter<Secret>
{
    public override Secret Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new JsonException("Secrets can't be read from JSON.");

    public override void Write(Utf8JsonWriter writer, Secret value, JsonSerializerOptions options) =>
        throw new JsonException("Secrets can't be written as JSON.");
}

/// <summary>Encrypts credential secrets with the key ring, bound to the credential's ID, so a ciphertext copied to another row won't decrypt.</summary>
public sealed class CredentialProtector(IDataProtectionProvider provider)
{
    private const string Purpose = "NKeeper.Credentials.v1";

    public byte[] Protect(Guid credentialId, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            return For(credentialId).Protect(bytes);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    /// <exception cref="System.Security.Cryptography.CryptographicException">The ciphertext isn't this credential's.</exception>
    public Secret Unprotect(Guid credentialId, byte[] protectedSecret)
    {
        var bytes = For(credentialId).Unprotect(protectedSecret);
        var chars = Encoding.UTF8.GetChars(bytes);
        try
        {
            return new Secret(chars);
        }
        finally
        {
            Array.Clear(bytes);
            Array.Clear(chars);
        }
    }

    private IDataProtector For(Guid credentialId) => provider.CreateProtector(Purpose, credentialId.ToString("D"));
}
