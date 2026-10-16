namespace Blackglass.Accounts.Persistence.Player;

/// <summary>Why a player-facing roster, inventory or advancement call refused. The routes map these to responses.</summary>
public enum PlayerError { NotProvisioned, OperativeNotFound, VersionMismatch, NoPendingPick, UnknownChoice, InventoryLocked }

public sealed class PlayerException(PlayerError error, string message) : Exception(message)
{
    public PlayerError Error { get; } = error;
}
