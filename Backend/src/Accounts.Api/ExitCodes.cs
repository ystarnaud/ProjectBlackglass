namespace Blackglass.Accounts.Api;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Failure = 1;
    public const int SchemaMismatch = 2;
    public const int SchemaNotCurrent = 3;
    public const int InvalidCatalogue = 4;
    public const int Usage = 64;
    public const int NotFound = 65;
    public const int Conflict = 66;
}
