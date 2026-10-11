namespace Blackglass
{
    /// <summary>The kind of surface an impact lands on; picks the impact effect and sound. Values are serialized in profiles: never renumber.</summary>
    public enum ImpactSurfaceKind
    {
        Default = 0,
        Concrete = 1,
        Metal = 2,
        Character = 3,
    }
}
