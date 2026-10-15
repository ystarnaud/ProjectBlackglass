using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blackglass.Accounts.Domain.Ids;

/// <summary>
/// Puts a long owned id on the wire as its canonical Base58 JSON string, never a JSON number (JavaScript loses integers
/// above 2^53). Reading accepts only a canonical string. Apply it to a DTO property:
/// <c>[property: JsonConverter(typeof(Base58IdJsonConverter))] long Id</c>.
/// </summary>
public sealed class Base58IdJsonConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Base58Id.TryParse(reader.GetString(), out var id)
            ? id
            : throw new JsonException("An id must be a canonical Base58 string.");

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Base58Id.Format(value));
}

/// <summary>The same for an optional id: JSON null is null, anything else must be a canonical Base58 string.</summary>
public sealed class NullableBase58IdJsonConverter : JsonConverter<long?>
{
    public override bool HandleNull => true;

    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        return reader.TokenType == JsonTokenType.String && Base58Id.TryParse(reader.GetString(), out var id)
            ? id
            : throw new JsonException("An id must be null or a canonical Base58 string.");
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is { } id) writer.WriteStringValue(Base58Id.Format(id));
        else writer.WriteNullValue();
    }
}
