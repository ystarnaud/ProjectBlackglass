using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blackglass.Accounts.Api.Auth;
using Blackglass.Accounts.Api.Player;
using Blackglass.Accounts.Domain.Accounts;
using Blackglass.Accounts.Domain.Ids;

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
        Assert.Contains(requests, t => t == typeof(PickChoiceRequest)); // the player routes are audited too
        Assert.Contains(requests, t => t == typeof(InventoryOperationsRequest));
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
    public void No_player_type_carries_an_account_and_every_owned_id_is_a_long_behind_a_base58_converter()
    {
        var types = typeof(PickChoiceRequest).Assembly.GetTypes().Where(t => t.Namespace == typeof(PickChoiceRequest).Namespace && t.IsPublic).ToList();
        Assert.Contains(typeof(RosterResponse), types);
        Assert.Contains(typeof(InventoryResponse), types);
        Assert.Contains(typeof(PickChoiceResponse), types);
        Assert.Contains(typeof(InventoryOperationsResponse), types);
        Assert.Contains(typeof(OperationDto), types);
        foreach (var p in types.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)))
        {
            Assert.DoesNotContain("Account", p.Name, StringComparison.OrdinalIgnoreCase);
            if (p.PropertyType != typeof(long) && p.PropertyType != typeof(long?)) continue;
            if (p.Name == "Version" || p.Name == "ExpectedVersion") continue; // counters, not ids
            Assert.Contains(p.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType,
                new[] { typeof(Base58IdJsonConverter), typeof(NullableBase58IdJsonConverter) });
        }
    }

    [Fact]
    public void Player_responses_serialize_owned_ids_as_base58_strings_never_numbers()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const long id = 9007199254740993; // 2^53 + 1
        var operative = new OperativeDto(id, "def", true, 0, 1, 0, []);
        var location = new LocationDto([new InstanceDto(id + 1, "item", true)], []);
        var text = JsonSerializer.Serialize(new InventoryResponse(1, false, location, [new BagDto(id, location, new EquipmentDto(id + 1, null, null))]), web)
            + JsonSerializer.Serialize(new RosterResponse(1, [operative]), web);
        Assert.Contains($"\"operativeId\":\"{Base58Id.Format(id)}\"", text);
        Assert.Contains($"\"itemId\":\"{Base58Id.Format(id + 1)}\"", text);
        Assert.Contains($"\"weapon\":\"{Base58Id.Format(id + 1)}\",\"armor\":null,\"utility\":null", text);
    }

    [Fact]
    public void The_inventory_edit_request_takes_ids_as_strings_and_no_number_is_an_id()
    {
        // Ids arrive as Base58 strings (parsed and refused with the operation's index), never as JSON numbers.
        var operation = typeof(OperationDto).GetProperties(BindingFlags.Public | BindingFlags.Instance).ToDictionary(p => p.Name, p => p.PropertyType);
        Assert.Equal(typeof(string), operation["OperativeId"]);
        Assert.Equal(typeof(string), operation["ItemId"]);
        Assert.Equal(typeof(string), operation["DefinitionId"]);
        Assert.Equal(typeof(int?), operation["Quantity"]);
        Assert.Equal(["ExpectedVersion", "Operations"], typeof(InventoryOperationsRequest).GetProperties().Select(p => p.Name).Order().ToArray());

        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const long id = 9007199254740993;
        var location = new LocationDto([new InstanceDto(id, "item", true)], []);
        var inventory = new InventoryResponse(5, false, location, []);
        var text = JsonSerializer.Serialize(new InventoryOperationsResponse(5, inventory), web);
        Assert.Equal($"{{\"version\":5,\"inventory\":{JsonSerializer.Serialize(inventory, web)}}}", text);
        Assert.Contains($"\"itemId\":\"{Base58Id.Format(id)}\"", text);
    }

    [Fact]
    public void Responses_serialize_account_ids_as_base58_strings()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const long id = 9007199254740993; // 2^53 + 1: a JSON number would lose it in JavaScript
        var expected = $"\"accountId\":\"{AccountId.Format(id)}\"";
        Assert.Contains(expected, JsonSerializer.Serialize(new AuthEndpoints.MeResponse(id, null, null), web));
        Assert.Contains(expected, JsonSerializer.Serialize(new AuthEndpoints.LoginResponse("at1_x", DateTimeOffset.UnixEpoch, id), web));
        Assert.Contains(expected, JsonSerializer.Serialize(new AdminEndpoints.CreateAccountResponse(id, Guid.Empty, "dk1_x"), web));
    }

    [Fact]
    public void Me_energy_serializes_nextRegenAt_as_UTC_ISO_8601_with_Z()
    {
        // System.Text.Json writes a UTC-kind DateTime with 'Z' (a DateTimeOffset would write "+00:00"), trimming trailing
        // zero fraction digits: whole microseconds give at most six.
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var at = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        string Me(DateTime? next) => JsonSerializer.Serialize(new AuthEndpoints.MeResponse(1, 3, new AuthEndpoints.EnergyDto(10, 50, next)), web);
        Assert.Equal($"{{\"accountId\":\"{AccountId.Format(1)}\",\"inventoryVersion\":3,\"energy\":{{\"balance\":10,\"cap\":50,\"nextRegenAt\":\"2026-10-16T12:00:00.123456Z\"}}}}",
            Me(at.AddTicks(1_234_560)));
        Assert.Contains("\"nextRegenAt\":\"2026-10-16T12:00:00.00001Z\"", Me(at.AddTicks(100)));
        Assert.Contains("\"nextRegenAt\":\"2026-10-16T12:00:00Z\"", Me(at));
        Assert.Contains("\"nextRegenAt\":null", Me(null));
        Assert.EndsWith("\"energy\":null}", JsonSerializer.Serialize(new AuthEndpoints.MeResponse(1, null, null), web));
    }
}
