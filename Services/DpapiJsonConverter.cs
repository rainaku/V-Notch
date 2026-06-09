using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VNotch.Services;

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
