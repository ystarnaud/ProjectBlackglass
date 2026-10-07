namespace Blackglass
{
    /// <summary>
    /// What a generated piece of environment is, as gameplay and the layout see it. The generator and planner speak only in
    /// these; an <see cref="EnvironmentTheme"/> decides which prefab represents each one.
    /// </summary>
    public enum EnvironmentElement
    {
        Floor,
        WallStraight,
        WallCorner,
        WallEnd,
        WallJunction,
        DoorFrame,
        LowCover,
        LowCoverLong,
        Pillar,
        Crate,
        Terminal,
        LightFixture,
    }
}
