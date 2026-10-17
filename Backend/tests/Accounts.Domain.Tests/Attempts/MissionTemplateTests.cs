using Blackglass.Accounts.Domain.Attempts;

namespace Blackglass.Accounts.Domain.Tests.Attempts;

public class MissionTemplateTests
{
    [Fact]
    public void MissionTemplate_accepts_the_seeded_template_and_a_zero_cost()
    {
        var seeded = MissionTemplate.Create("procedural-standard", 1, 10, 1, 3);
        Assert.Equal(new MissionTemplate("procedural-standard", 1, 10, 1, 3), seeded);
        Assert.Equal(0, MissionTemplate.Create("training.free", 2, 0, 1, 1).EnergyCost);
        Assert.Equal(16, MissionTemplate.Create(new string('a', 64), 1, 0, 16, 16).MaxOperatives);
    }

    [Theory]
    [InlineData("Procedural", 1, 10, 1, 3)]   // uppercase
    [InlineData("procedural_standard", 1, 10, 1, 3)]
    [InlineData("procedural..standard", 1, 10, 1, 3)]
    [InlineData("-procedural", 1, 10, 1, 3)]
    [InlineData("procedural\n", 1, 10, 1, 3)]
    [InlineData("", 1, 10, 1, 3)]
    [InlineData("procedural-standard", 0, 10, 1, 3)]   // version below 1
    [InlineData("procedural-standard", 1, -1, 1, 3)]   // negative cost
    [InlineData("procedural-standard", 1, 10, 0, 3)]   // min below 1
    [InlineData("procedural-standard", 1, 10, 3, 2)]   // max below min
    [InlineData("procedural-standard", 1, 10, 1, 17)]  // max above 16
    public void MissionTemplate_refuses_invalid_values(string id, int version, int cost, int min, int max)
    {
        Assert.ThrowsAny<ArgumentException>(() => MissionTemplate.Create(id, version, cost, min, max));
        Assert.ThrowsAny<ArgumentException>(() => new MissionTemplate(id, version, cost, min, max).Validate());
    }

    [Fact]
    public void MissionTemplate_refuses_an_id_longer_than_64() =>
        Assert.ThrowsAny<ArgumentException>(() => MissionTemplate.Create(new string('a', 65), 1, 10, 1, 3));
}
