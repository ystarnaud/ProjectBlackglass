namespace Blackglass.Accounts.Persistence.Attempts;

public enum MissionAdminError
{
    /// <summary>A template id, cost or team size MissionTemplate refuses (checked before any connection).</summary>
    InvalidTemplate,
    /// <summary>A host id or capacity out of bounds (checked before any connection).</summary>
    InvalidHost,
    /// <summary>The minted version was taken by an insert that bypassed the per-template lock (23505); never retried.</summary>
    TemplateVersionConflict,
    /// <summary>A host with that id already exists.</summary>
    HostExists,
    /// <summary>No development host fixture has that id (an unknown id and an agent host are the same answer).</summary>
    UnknownDevHost,
}

/// <summary>A refused mission template or development host operation; nothing was written.</summary>
public sealed class MissionAdminException(MissionAdminError error, string message) : Exception(message)
{
    public MissionAdminError Error { get; } = error;
}
