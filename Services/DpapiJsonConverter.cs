using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VNotch.Services;

public sealed class DpapiJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? stored = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();

        // Empty or null → nothing to decrypt.
        if (string.IsNullOrEmpty(stored))
            return "";

        // Properly encrypted value → decrypt normally.
        if (DataProtection.IsEncrypted(stored))
        {
            try
            {
                return DataProtection.Unprotect(stored);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("DPAPI-CONVERTER", $"Failed to decrypt stored value: {ex.Message}");
                return "";
            }
        }

        // SettingsMigrator must encrypt legacy data before this converter receives
        // it. Accepting it here would leave a plaintext key in memory and make a
        // later, unrelated save capable of persisting it again.
        throw new System.Security.Cryptography.CryptographicException(
            "A plaintext API key must be migrated before it can be loaded.");
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.WriteStringValue("");
            return;
        }

        // Protect will throw if DPAPI fails — this is intentional so the caller
        // knows the value was NOT persisted and can abort the save.
        writer.WriteStringValue(DataProtection.Protect(value));
    }
}
