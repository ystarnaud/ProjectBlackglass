using Blackglass.Accounts.Api.Attempts;
using Blackglass.Accounts.Persistence.Attempts;

namespace Blackglass.Accounts.IntegrationTests.Unit;

/// <summary>
/// The status of every admission refusal (spec §16.1 table, Global Constraints "Failures"). The mapping is a switch with no
/// default arm, so a new AdmissionError fails the build until it is mapped; this test also iterates every member.
/// </summary>
public class AttemptRefusalMappingTests
{
    static readonly Dictionary<AdmissionError, int> Expected = new()
    {
        [AdmissionError.UnknownAccount] = 401, // the uniform 401: the session's account always exists
        [AdmissionError.NotProvisioned] = 409,
        [AdmissionError.IdempotencyKeyReused] = 409,
        [AdmissionError.AttemptActive] = 409,
        [AdmissionError.InsufficientEnergy] = 409,
        [AdmissionError.ServiceUnavailable] = 503,
        [AdmissionError.UnknownTemplate] = 422,
        [AdmissionError.UnknownOperative] = 422,
        [AdmissionError.OperativeCount] = 422,
        [AdmissionError.RetiredOperative] = 422,
        [AdmissionError.UnknownItem] = 422,
    };

    [Fact]
    public void Every_AdmissionError_maps_to_its_status()
    {
        var all = Enum.GetValues<AdmissionError>();
        Assert.Equal(all.Order(), Expected.Keys.Order()); // a new member must be given its expected status here too
        foreach (var error in all) Assert.Equal(Expected[error], AttemptEndpoints.StatusFor(error));
    }

    [Fact]
    public void Every_AttemptError_maps_to_its_status()
    {
        // Task 7: an unknown or foreign attempt is 404 (one code for both), a cancel after start is 409 InvalidState.
        var expected = new Dictionary<AttemptError, int> { [AttemptError.NotFound] = 404, [AttemptError.InvalidState] = 409 };
        var all = Enum.GetValues<AttemptError>();
        Assert.Equal(all.Order(), expected.Keys.Order());
        foreach (var error in all) Assert.Equal(expected[error], AttemptEndpoints.StatusFor(error));
    }
}
