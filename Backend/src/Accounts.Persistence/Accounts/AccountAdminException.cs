namespace Blackglass.Accounts.Persistence.Accounts;

public enum AccountAdminError { NotFound, HandleTaken, InvalidHandle, InvalidDisplayName, InvalidOperator }

public sealed class AccountAdminException(AccountAdminError error, string message) : Exception(message)
{
    public AccountAdminError Error { get; } = error;
}
