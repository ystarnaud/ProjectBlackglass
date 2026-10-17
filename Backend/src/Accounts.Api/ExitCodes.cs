namespace Blackglass.Accounts.Api;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Failure = 1;
    public const int SchemaMismatch = 2;
    public const int SchemaNotCurrent = 3;
    public const int InvalidCatalogue = 4;
    /// <summary>The catalogue would change the identity kind of an item id a registered catalogue uses (S9-B20.2).</summary>
    public const int CatalogueIdentityChanged = 5;
    public const int Usage = 64;
    public const int NotFound = 65;
    public const int Conflict = 66;
    /// <summary>The admin CLI asked for confirmation and the operator didn't type the Base58 account id.</summary>
    public const int NotConfirmed = 67;
    /// <summary>The database can't be reached (sysexits EX_UNAVAILABLE).</summary>
    public const int Unavailable = 69;
    /// <summary>A lock or statement timeout: nothing was changed; retry (sysexits EX_TEMPFAIL).</summary>
    public const int Busy = 75;
}
