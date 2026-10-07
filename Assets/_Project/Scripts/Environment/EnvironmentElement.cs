namespace Blackglass
{
    /// <summary>
    /// What a generated piece of environment is, as gameplay and the layout see it. The generator and planner speak only in
    /// these; an <see cref="EnvironmentTheme"/> decides which prefab represents each one.
    /// </summary>
    public enum EnvironmentElement
    {
        // Values are serialized in theme assets: never renumber or reuse them. Add new elements at the end.
        Floor = 0,
        WallStraight = 1,
        WallCorner = 2,
        WallEnd = 3,
        WallJunction = 4,
        DoorFrame = 5,
        LowCover = 6,
        LowCoverLong = 7,
        Pillar = 8,
        Crate = 9,
        Terminal = 10,
        LightFixture = 11,
    }
}
