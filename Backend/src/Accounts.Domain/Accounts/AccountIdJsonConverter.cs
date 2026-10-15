using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blackglass.Accounts.Domain.Accounts;

/// <summary>
/// Puts a long account id on the wire as its canonical Base58 JSON string, never a JSON number (JavaScript loses
/// integers above 2^53). Reading accepts only a canonical string. Apply it to every DTO property that carries an account
/// id: <c>[property: JsonConverter(typeof(AccountIdJsonConverter))] long AccountId</c>.
/// </summary>
public sealed class AccountIdJsonConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && AccountId.TryParse(reader.GetString(), out var id)
            ? id
            : throw new JsonException("An account id must be a canonical Base58 string.");

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteStringValue(AccountId.Format(value));
}
