using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blackglass.Accounts.Api.Auth;
using Blackglass.Accounts.Domain.Accounts;

namespace Blackglass.Accounts.IntegrationTests.Unit;

/// <summary>
/// Spec §4.1 and §4.2: the account comes from the token only, so no client request type carries an account id; and
/// every account id a response carries is a long behind AccountIdJsonConverter, so it leaves as a Base58 string.
/// </summary>
public class DtoAuditTests
{
    static IEnumerable<Type> ApiTypes(string suffix) => typeof(AuthEndpoints).Assembly.GetTypes()
        .Where(t => t.Name.EndsWith(suffix, StringComparison.Ordinal) && !t.IsAbstract);

    [Fact]
    public void No_client_request_type_has_an_account_field()
    {
        var requests = ApiTypes("Request").Where(t => t.DeclaringType != typeof(AdminEndpoints)).ToList();
        Assert.NotEmpty(requests);
        foreach (var type in requests)
            Assert.DoesNotContain(type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                p => p.Name.Contains("Account", StringComparison.OrdinalIgnoreCase) && !p.Name.Equals("AccountKey", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_response_account_id_is_a_long_behind_the_base58_converter()
    {
        var properties = ApiTypes("Response").SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.Name.EndsWith("AccountId", StringComparison.Ordinal)).ToList();
        Assert.True(properties.Count >= 3, "expected the login, me and admin-create responses");
        foreach (var p in properties)
        {
            Assert.Equal(typeof(long), p.PropertyType);
            Assert.Equal(typeof(AccountIdJsonConverter), p.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType);
        }
    }

    [Fact]
    public void Responses_serialize_account_ids_as_base58_strings()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const long id = 9007199254740993; // 2^53 + 1: a JSON number would lose it in JavaScript
        var expected = $"\"accountId\":\"{AccountId.Format(id)}\"";
        Assert.Contains(expected, JsonSerializer.Serialize(new AuthEndpoints.MeResponse(id), web));
        Assert.Contains(expected, JsonSerializer.Serialize(new AuthEndpoints.LoginResponse("at1_x", DateTimeOffset.UnixEpoch, id), web));
        Assert.Contains(expected, JsonSerializer.Serialize(new AdminEndpoints.CreateAccountResponse(id, Guid.Empty, "dk1_x"), web));
    }
}
