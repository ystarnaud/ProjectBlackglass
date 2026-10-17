using System.Text;
using Blackglass.Accounts.Domain.Attempts;

namespace Blackglass.Accounts.Domain.Tests.Attempts;

public class DeployRequestTests
{
    // "2" is 1, "21" is 58, "NQm6nKp8qFC" is long.MaxValue (Base58Id).
    const string Body = """{"missionTemplate":"procedural-standard","operativeIds":["2","21","NQm6nKp8qFC"]}""";
    const string BodyHash = "0c0c6a1771d4896c1937a4fbaf3fc0a5153741162960aaf18bb44cec87d52dce";
    const string SwappedHash = "e89f719d1883c7b7718277ba605e3978c7ed60aefd8c5b56c7ba08d7fea86043";

    static DeployRequest Parse(string json) => DeployRequest.Parse(Encoding.UTF8.GetBytes(json));

    static string Ids(int count) => string.Join(",", Enumerable.Range(2, count).Select(i => $"\"{Blackglass.Accounts.Domain.Ids.Base58Id.Format(i)}\""));

    public static TheoryData<string, string, DeployRequestError> Refusals => new()
    {
        { "unknown member", """{"missionTemplate":"procedural-standard","operativeIds":["2"],"accountId":"2"}""", DeployRequestError.InvalidBody },
        { "duplicate member", """{"missionTemplate":"procedural-standard","missionTemplate":"procedural-standard","operativeIds":["2"]}""", DeployRequestError.InvalidBody },
        { "duplicate list member", """{"missionTemplate":"procedural-standard","operativeIds":["2"],"operativeIds":["2"]}""", DeployRequestError.InvalidBody },
        { "missing template", """{"operativeIds":["2"]}""", DeployRequestError.InvalidBody },
        { "missing ids", """{"missionTemplate":"procedural-standard"}""", DeployRequestError.InvalidBody },
        { "case-different member name", """{"MissionTemplate":"procedural-standard","operativeIds":["2"]}""", DeployRequestError.InvalidBody },
        { "template not a string", """{"missionTemplate":7,"operativeIds":["2"]}""", DeployRequestError.InvalidBody },
        { "template null", """{"missionTemplate":null,"operativeIds":["2"]}""", DeployRequestError.InvalidBody },
        { "template malformed", """{"missionTemplate":"Procedural_Standard","operativeIds":["2"]}""", DeployRequestError.InvalidBody },
        { "template empty", """{"missionTemplate":"","operativeIds":["2"]}""", DeployRequestError.InvalidBody },
        { "template too long", "{\"missionTemplate\":\"" + new string('a', 65) + "\",\"operativeIds\":[\"2\"]}", DeployRequestError.InvalidBody },
        { "ids not an array", """{"missionTemplate":"procedural-standard","operativeIds":"2"}""", DeployRequestError.InvalidBody },
        { "ids empty", """{"missionTemplate":"procedural-standard","operativeIds":[]}""", DeployRequestError.InvalidBody },
        { "ids oversized", "{\"missionTemplate\":\"procedural-standard\",\"operativeIds\":[" + Ids(17) + "]}", DeployRequestError.InvalidBody },
        { "id a number", """{"missionTemplate":"procedural-standard","operativeIds":[2]}""", DeployRequestError.InvalidBody },
        { "id null", """{"missionTemplate":"procedural-standard","operativeIds":[null]}""", DeployRequestError.InvalidBody },
        { "id with a leading zero digit", """{"missionTemplate":"procedural-standard","operativeIds":["12"]}""", DeployRequestError.InvalidOperativeId },
        { "id zero", """{"missionTemplate":"procedural-standard","operativeIds":["1"]}""", DeployRequestError.InvalidOperativeId },
        { "id outside the alphabet", """{"missionTemplate":"procedural-standard","operativeIds":["0OIl"]}""", DeployRequestError.InvalidOperativeId },
        { "id past long.MaxValue", """{"missionTemplate":"procedural-standard","operativeIds":["NQm6nKp8qFD"]}""", DeployRequestError.InvalidOperativeId },
        { "id empty", """{"missionTemplate":"procedural-standard","operativeIds":[""]}""", DeployRequestError.InvalidOperativeId },
        { "id with whitespace", """{"missionTemplate":"procedural-standard","operativeIds":[" 2"]}""", DeployRequestError.InvalidOperativeId },
        { "duplicate id", """{"missionTemplate":"procedural-standard","operativeIds":["2","21","2"]}""", DeployRequestError.DuplicateOperative },
        { "not an object", """["procedural-standard"]""", DeployRequestError.InvalidBody },
        { "null document", "null", DeployRequestError.InvalidBody },
        { "empty body", "", DeployRequestError.InvalidBody },
        { "not JSON", "deploy please", DeployRequestError.InvalidBody },
        { "trailing comma", """{"missionTemplate":"procedural-standard","operativeIds":["2"],}""", DeployRequestError.InvalidBody },
        { "a comment", """{/*x*/"missionTemplate":"procedural-standard","operativeIds":["2"]}""", DeployRequestError.InvalidBody },
        { "trailing content", """{"missionTemplate":"procedural-standard","operativeIds":["2"]} {}""", DeployRequestError.InvalidBody },
        { "a lone surrogate escape", """{"missionTemplate":"\ud800","operativeIds":["2"]}""", DeployRequestError.InvalidBody },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void DeployRequest_parses_strictly(string label, string json, DeployRequestError expected)
    {
        var refused = Assert.Throws<DeployRequestException>(() => Parse(json));
        Assert.True(expected == refused.Error, $"{label}: expected {expected}, got {refused.Error}");
        // Information-safe: the message names the reason only, never the input.
        Assert.Equal(expected.ToString(), refused.Message);
    }

    [Fact]
    public void DeployRequest_refuses_invalid_utf8()
    {
        byte[] body = [.. Encoding.UTF8.GetBytes("{\"missionTemplate\":\"a"), 0xFF, .. Encoding.UTF8.GetBytes("\",\"operativeIds\":[\"2\"]}")];
        Assert.Equal(DeployRequestError.InvalidBody, Assert.Throws<DeployRequestException>(() => DeployRequest.Parse(body)).Error);
    }

    [Fact]
    public void DeployRequest_accepts_sixteen_ids_in_the_order_given()
    {
        var request = Parse("{\"missionTemplate\":\"procedural-standard\",\"operativeIds\":[" + Ids(16) + "]}");
        Assert.Equal(Enumerable.Range(2, 16).Select(i => (long)i), request.OperativeIds);
    }

    [Fact]
    public void The_canonical_form_and_hash_are_stable()
    {
        var request = Parse(Body);
        Assert.Equal("procedural-standard", request.MissionTemplate);
        Assert.Equal([1L, 58L, long.MaxValue], request.OperativeIds);
        Assert.Equal(Body, Encoding.UTF8.GetString(request.CanonicalJson()));
        Assert.Equal(BodyHash, Convert.ToHexStringLower(request.RequestHash()));

        // Built directly, the same request gives the same bytes.
        var built = new DeployRequest("procedural-standard", [1, 58, long.MaxValue]);
        Assert.Equal(request.CanonicalJson(), built.CanonicalJson());
        Assert.Equal(BodyHash, Convert.ToHexStringLower(built.RequestHash()));
    }

    [Fact]
    public void Equivalent_JSON_with_reordered_properties_or_whitespace_hashes_the_same()
    {
        var variants = new[]
        {
            Body,
            """{"operativeIds":["2","21","NQm6nKp8qFC"],"missionTemplate":"procedural-standard"}""",
            "{\n  \"missionTemplate\" : \"procedural-standard\",\r\n\t\"operativeIds\": [ \"2\", \"21\",\n \"NQm6nKp8qFC\" ]\n}\n",
            // An escaped spelling of the same string is the same value.
            """{"missionTemplate":"procedural-standard","operativeIds":["2","21","NQm6nKp8qFC"]}""",
        };
        foreach (var variant in variants)
        {
            var request = Parse(variant);
            Assert.Equal(Body, Encoding.UTF8.GetString(request.CanonicalJson()));
            Assert.Equal(BodyHash, Convert.ToHexStringLower(request.RequestHash()));
        }
    }

    [Fact]
    public void Order_matters_for_the_hash()
    {
        var swapped = Parse("""{"missionTemplate":"procedural-standard","operativeIds":["21","2","NQm6nKp8qFC"]}""");
        Assert.Equal([58L, 1L, long.MaxValue], swapped.OperativeIds);
        Assert.Equal(SwappedHash, Convert.ToHexStringLower(swapped.RequestHash()));
        Assert.NotEqual(Parse(Body).RequestHash(), swapped.RequestHash());
    }
}
