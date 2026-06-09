using System;
using System.Security.Cryptography;
using System.Text;

namespace VNotch.Services;

public static class DataProtection
{
    private const string Prefix = "enc:";

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
            RuntimeLog.Warn("DPAPI", $"Protect failed, storing plaintext: {ex.Message}");
            return plaintext;
        }
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;

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
