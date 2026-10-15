using System.Text.Json;
using System.Text.Json.Serialization;
using Blackglass.Accounts.Domain.Accounts;

namespace Blackglass.Accounts.Domain.Tests.Accounts;

public class AccountIdJsonConverterTests
{
    sealed record Body([property: JsonConverter(typeof(AccountIdJsonConverter))] long AccountId);

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void An_account_id_is_written_as_a_base58_json_string() =>
        Assert.Equal("""{"accountId":"BukQL"}""", JsonSerializer.Serialize(new Body(123456789), Web));

    [Fact]
    public void The_bigint_maximum_stays_exact_as_a_string() =>
        Assert.Equal("""{"accountId":"NQm6nKp8qFC"}""", JsonSerializer.Serialize(new Body(long.MaxValue), Web));

    [Fact]
    public void A_canonical_string_is_read_back() =>
        Assert.Equal(123456789, JsonSerializer.Deserialize<Body>("""{"accountId":"BukQL"}""", Web)!.AccountId);

    [Theory]
    [InlineData("""{"accountId":123456789}""")]      // a JSON number is never an account id
    [InlineData("""{"accountId":null}""")]
    [InlineData("""{"accountId":"1BukQL"}""")]       // non-canonical
    [InlineData("""{"accountId":" BukQL"}""")]
    [InlineData("""{"accountId":"Buk0L"}""")]
    [InlineData("""{"accountId":""}""")]
    [InlineData("""{"accountId":"NQm6nKp8qFD"}""")]  // overflow
    [InlineData("""{"accountId":true}""")]
    public void Anything_else_is_refused(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Body>(json, Web));

    [Fact]
    public void Writing_a_non_positive_id_fails_rather_than_emitting_a_bad_id() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => JsonSerializer.Serialize(new Body(0), Web));
}
