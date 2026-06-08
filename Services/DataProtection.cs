using System;
using System.Security.Cryptography;
using System.Text;

namespace VNotch.Services;

/// <summary>
/// Thin wrapper over Windows DPAPI (<see cref="ProtectedData"/>, CurrentUser scope) for
/// protecting small secrets (e.g. the user's YouTube Data API key) at rest in settings.json.
///
/// Encrypted values are stored as <c>"enc:" + Base64(ciphertext)</c>. Values without the
/// <see cref="Prefix"/> are treated as legacy plaintext and returned as-is, so existing
/// settings keep working and get encrypted on the next save (transparent migration).
/// </summary>
public static class DataProtection
{
    private const string Prefix = "enc:";

    /// <summary>Encrypts a plaintext value for storage. Empty/null returns an empty string unchanged.</summary>
    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(plaintext);
            byte[] encrypted = ProtectedData.Protect(data, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            // If protection fails we fall back to storing plaintext rather than losing the value.
            RuntimeLog.Warn("DPAPI", $"Protect failed, storing plaintext: {ex.Message}");
            return plaintext;
        }
    }

    /// <summary>
    /// Decrypts a stored value. Legacy plaintext (no <see cref="Prefix"/>) is returned unchanged so
    /// previously-saved keys still load. Returns the raw input if decryption fails.
    /// </summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored; // legacy plaintext

        try
        {
            byte[] encrypted = Convert.FromBase64String(stored.Substring(Prefix.Length));
            byte[] data = ProtectedData.Unprotect(encrypted, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("DPAPI", $"Unprotect failed: {ex.Message}");
            return "";
        }
    }
}
