namespace Blackglass
{
    /// <summary>Why an ability cannot be used. Checked in this order; the first that applies is the reason.</summary>
    public enum AbilityFailure
    {
        None,
        CasterDead,
        UnknownAbility,
        NoTarget,
        TargetDead,
        WrongSide,
        NoPosition,
        OnCooldown,
        OutOfRange,
        NoLineOfSight,
    }

    public static class AbilityFailureText
    {
        /// <summary>Short lower-case text for the debug HUD, e.g. "out of range".</summary>
        public static string Describe(this AbilityFailure failure)
        {
            switch (failure)
            {
                case AbilityFailure.None:
                    return "ready";
                case AbilityFailure.CasterDead:
                    return "caster is down";
                case AbilityFailure.UnknownAbility:
                    return "unit does not have this ability";
                case AbilityFailure.NoTarget:
                    return "no target";
                case AbilityFailure.TargetDead:
                    return "target is down";
                case AbilityFailure.WrongSide:
                    return "wrong side for this ability";
                case AbilityFailure.NoPosition:
                    return "no position";
                case AbilityFailure.OnCooldown:
                    return "on cooldown";
                case AbilityFailure.OutOfRange:
                    return "out of range";
                case AbilityFailure.NoLineOfSight:
                    return "no line of sight";
                default:
                    return failure.ToString();
            }
        }
    }
}
