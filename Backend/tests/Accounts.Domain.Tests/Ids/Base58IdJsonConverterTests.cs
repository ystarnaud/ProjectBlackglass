using System.Text.Json;
using System.Text.Json.Serialization;
using Blackglass.Accounts.Domain.Ids;

namespace Blackglass.Accounts.Domain.Tests.Ids;

public class Base58IdJsonConverterTests
{
    sealed record Body([property: JsonConverter(typeof(Base58IdJsonConverter))] long Id);

    sealed record NullableBody([property: JsonConverter(typeof(NullableBase58IdJsonConverter))] long? Id);

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Writes_a_string_and_reads_only_a_canonical_string()
    {
        Assert.Equal("""{"id":"BukQL"}""", JsonSerializer.Serialize(new Body(123456789), Web));
        Assert.Equal("""{"id":"NQm6nKp8qFC"}""", JsonSerializer.Serialize(new Body(long.MaxValue), Web));
        Assert.Equal(123456789, JsonSerializer.Deserialize<Body>("""{"id":"BukQL"}""", Web)!.Id);
    }

    [Theory]
    [InlineData("""{"id":123456789}""")]
    [InlineData("""{"id":null}""")]
    [InlineData("""{"id":true}""")]
    [InlineData("""{"id":"1BukQL"}""")]
    [InlineData("""{"id":" BukQL"}""")]
    [InlineData("""{"id":"Buk0L"}""")]
    [InlineData("""{"id":""}""")]
    [InlineData("""{"id":"NQm6nKp8qFD"}""")]
    public void Anything_else_is_refused(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Body>(json, Web));

    [Fact]
    public void Writing_a_non_positive_id_fails() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => JsonSerializer.Serialize(new Body(0), Web));

    [Fact]
    public void Nullable_writes_null_and_reads_null()
    {
        Assert.Equal("""{"id":null}""", JsonSerializer.Serialize(new NullableBody(null), Web));
        Assert.Null(JsonSerializer.Deserialize<NullableBody>("""{"id":null}""", Web)!.Id);
        Assert.Equal("""{"id":"BukQL"}""", JsonSerializer.Serialize(new NullableBody(123456789), Web));
        Assert.Equal(123456789L, JsonSerializer.Deserialize<NullableBody>("""{"id":"BukQL"}""", Web)!.Id);
    }

    [Theory]
    [InlineData("""{"id":123456789}""")]
    [InlineData("""{"id":"1BukQL"}""")]
    [InlineData("""{"id":""}""")]
    public void Nullable_still_refuses_non_canonical_values(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<NullableBody>(json, Web));
}
