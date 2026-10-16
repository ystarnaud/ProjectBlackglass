using Blackglass.Accounts.Domain.Inventory;

namespace Blackglass.Accounts.Persistence.Player;

/// <summary>
/// An inventory batch was refused by the rules: <see cref="Index"/> is the zero-based position of the first refused
/// operation and <see cref="Code"/> why. Nothing was written and the version did not change (owner ruling S5-D1). The route
/// answers 422 <c>{code, index}</c>; the message names only the index and the code, never an id.
/// </summary>
public sealed class InventoryRefusedException(int index, InventoryRefusal code)
    : Exception($"Inventory operation {index} was refused: {code}.")
{
    public int Index { get; } = index;
    public InventoryRefusal Code { get; } = code;
}
