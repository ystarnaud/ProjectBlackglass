namespace Blackglass
{
    /// <summary>
    /// The development fixture's verbs (hidden information slice, spec section 3.9). The wire carries this byte in
    /// NetFixtureMessage; a verb names no unit and no account. Compiled into every build so the server can always recognise,
    /// count and refuse the message; only Editor and fixture builds can carry one out.
    /// </summary>
    public enum NetFixtureVerb : byte
    {
        None = 0,
        Observe = 1,
        Unobserve = 2,
        MoveHostile = 3,
        Search = 4,
    }
}
