namespace Blackglass.Accounts.Persistence.Energy;

/// <summary>Why an operator energy verb refused, with nothing written.</summary>
public enum EnergyError { NoWallet, IdempotencyKeyReused, BalanceLimit, InvalidAmount, InvalidPolicy }

public sealed class EnergyException(EnergyError error, string message) : Exception(message)
{
    public EnergyError Error { get; } = error;
}
