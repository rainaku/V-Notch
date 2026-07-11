using System;
using System.Security.Cryptography;
using System.Text;

namespace VNotch.Services;

public static class DataProtection
{
    private const string Prefix = "enc:";
    internal static Func<byte[], byte[]> ProtectBytes { get; set; } = data =>
        ProtectedData.Protect(data, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);

    /// <summary>
    /// Encrypts the given plaintext using Windows DPAPI (CurrentUser scope).
    /// Returns a string with the "enc:" prefix followed by the Base64-encoded ciphertext.
    /// </summary>
    /// <param name="plaintext">The plaintext to encrypt.</param>
    /// <returns>The encrypted string with "enc:" prefix.</returns>
    /// <exception cref="CryptographicException">Thrown when DPAPI encryption fails.</exception>
    /// <exception cref="ArgumentNullException">Thrown when plaintext is null.</exception>
    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(plaintext);
            byte[] encrypted = ProtectBytes(data);
            return Prefix + Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            // Never log the value that was submitted for protection.
            RuntimeLog.Warn("DPAPI", "Protect failed.");
            throw new CryptographicException("DPAPI encryption failed.", ex);
        }
    }

    /// <summary>
    /// Decrypts a string that was previously encrypted with <see cref="Protect"/>.
    /// The string must have the "enc:" prefix.
    /// </summary>
    /// <param name="stored">The encrypted string with "enc:" prefix.</param>
    /// <returns>The decrypted plaintext, or an empty string if input is null/empty.</returns>
    /// <exception cref="CryptographicException">Thrown when decryption fails (corrupt data, wrong user, etc.).</exception>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Data is not DPAPI-encrypted (missing 'enc:' prefix). Use TryMigrateFromPlaintext for legacy data.");

        try
        {
            byte[] encrypted = Convert.FromBase64String(stored.Substring(Prefix.Length));
            byte[] data = ProtectedData.Unprotect(encrypted, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("DPAPI encrypted data is not valid Base64.", ex);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("DPAPI", "Unprotect failed.");
            throw new CryptographicException("DPAPI decryption failed.", ex);
        }
    }

    /// <summary>
    /// Checks whether a stored string has the DPAPI "enc:" prefix.
    /// </summary>
    public static bool IsEncrypted(string? stored) =>
        !string.IsNullOrEmpty(stored) && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Attempts to encrypt a legacy plaintext value. If encryption succeeds, returns
    /// the encrypted string. If encryption fails, returns null — the caller should
    /// NOT persist the plaintext.
    /// </summary>
    public static string? TryProtect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        try
        {
            return Protect(plaintext);
        }
        catch
        {
            return null;
        }
    }
}
