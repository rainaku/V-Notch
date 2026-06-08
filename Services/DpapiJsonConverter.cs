using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VNotch.Services;

/// <summary>
/// JSON converter that keeps a string property as plaintext in memory but encrypts it at rest
/// via <see cref="DataProtection"/> (Windows DPAPI). Apply with
/// <c>[JsonConverter(typeof(DpapiJsonConverter))]</c> on the property.
/// </summary>
public sealed class DpapiJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? stored = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
        return DataProtection.Unprotect(stored);
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(DataProtection.Protect(value));
    }
}
