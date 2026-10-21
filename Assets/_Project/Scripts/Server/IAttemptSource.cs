using Blackglass.Backend.Core;

namespace Blackglass
{
    /// <summary>
    /// Where the server's attempt comes from (Stage 12): the attempt id and the backend's register answer, before any validation
    /// (AttemptStart does that). Stage 12 has only the fixture source (Editor and fixture builds); Stage 13 adds the HTTP source.
    /// </summary>
    public interface IAttemptSource
    {
        /// <summary>False with a one-line reason (never a credential or the seed) when there is no usable answer.</summary>
        bool TryLoad(out string attemptId, out SessionRegisterResponse response, out string problem);
    }
}
