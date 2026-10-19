using System;

// The mission process's /server/v1 bodies, shaped exactly as the backend writes and parses them (ProcessEndpoints.cs,
// MissionRequests.cs, ReadyReport.cs, LoadoutSnapshot.ToJson). [Serializable] classes with public camelCase fields, arrays and
// no nullables, so JsonUtility (in the caller: Core references no engine) reads and writes them. They carry backend data as
// sent: Base58 ids, catalogue ids and timestamps as strings. They are never Unity's runtime state; mapping them is Stage 12's,
// at one boundary. No DTO overrides ToString, so nothing prints a credential or the seed by accident.
namespace Blackglass.Backend.Core
{
    /// <summary>POST /server/v1/session/register: RegisterRequest.Parse.</summary>
    [Serializable]
    public sealed class SessionRegisterRequest
    {
        public string buildRevision;
        public string catalogueVersion;
    }

    /// <summary>The register answer (ProcessEndpoints.RegisterResponse). The only DTO with the seed: the mission's generator input.</summary>
    [Serializable]
    public sealed class SessionRegisterResponse
    {
        public string processSessionId;
        public int seed;
        public MissionTemplateDto missionTemplate;
        public string catalogueVersion;
        public RegisterParticipantDto[] participants;
        public LaunchDeadlineDto deadlines;
    }

    [Serializable]
    public sealed class MissionTemplateDto
    {
        public string id;
        public int version;
    }

    [Serializable]
    public sealed class RegisterParticipantDto
    {
        public string accountId;
        public LoadoutSnapshotDto loadout;
    }

    [Serializable]
    public sealed class LaunchDeadlineDto
    {
        public string launch;
    }

    /// <summary>A participant's admission snapshot (LoadoutSnapshot.ToJson).</summary>
    [Serializable]
    public sealed class LoadoutSnapshotDto
    {
        public string accountId;
        public long inventoryVersion;
        public string catalogueVersion;
        public SnapshotOperativeDto[] operatives;
    }

    [Serializable]
    public sealed class SnapshotOperativeDto
    {
        public int slot;
        public string operativeId;
        public string definitionId;
        public int experience;
        public int rank;
        public string[] choices;
        public SnapshotBagDto bag;
        public EquipmentSlotsDto equipment;
    }

    [Serializable]
    public sealed class SnapshotBagDto
    {
        public SnapshotInstanceDto[] instances;
        public SnapshotStackDto[] stacks;
    }

    [Serializable]
    public sealed class SnapshotInstanceDto
    {
        public string itemId;
        public string definitionId;
    }

    [Serializable]
    public sealed class SnapshotStackDto
    {
        public string definitionId;
        public int quantity;
    }

    /// <summary>
    /// Three slots, each an item id or JSON null. JsonUtility has no null string and reads null as "", so code reads the
    /// accessors, which give null for an empty slot.
    /// </summary>
    [Serializable]
    public sealed class EquipmentSlotsDto
    {
        public string weapon;
        public string armor;
        public string utility;

        public string Weapon => string.IsNullOrEmpty(weapon) ? null : weapon;
        public string Armor => string.IsNullOrEmpty(armor) ? null : armor;
        public string Utility => string.IsNullOrEmpty(utility) ? null : utility;
    }

    /// <summary>POST /server/v1/session/ready: ReadyReport.Parse.</summary>
    [Serializable]
    public sealed class ReadyRequest
    {
        public string processSessionId;
        public string layoutHash;
        public int lootTableVersion;
        public string lootHash;
        public ReadyObjectiveDto[] objectives;
        public PoolLineDto[] lootPool;
    }

    [Serializable]
    public sealed class ReadyObjectiveDto
    {
        public string objectiveId;
        public bool required;
    }

    [Serializable]
    public sealed class PoolLineDto
    {
        public int containerIndex;
        public int line;
        public string definitionId;
        public int quantity;
    }

    [Serializable]
    public sealed class ReadyResponse
    {
        public string joinDeadline;
    }

    /// <summary>POST /server/v1/session/start: StartRequest.Parse.</summary>
    [Serializable]
    public sealed class StartRequest
    {
        public string processSessionId;
    }

    [Serializable]
    public sealed class StartResponse
    {
        public bool started;
        public string startedAt;
    }

    /// <summary>POST /server/v1/tickets/redeem: RedeemRequest.Parse. The ticket is a credential: never logged.</summary>
    [Serializable]
    public sealed class RedeemRequest
    {
        public string processSessionId;
        public string ticket;
    }

    [Serializable]
    public sealed class RedeemResponse
    {
        public string accountId;
        public string[] operativeIds;
    }

    /// <summary>POST /server/v1/session/heartbeat: ProcessHeartbeatRequest.Parse.</summary>
    [Serializable]
    public sealed class ProcessHeartbeatRequest
    {
        public string processSessionId;
        public string phase;
        public ParticipantConnectionDto[] participants;
    }

    [Serializable]
    public sealed class ParticipantConnectionDto
    {
        public string accountId;
        public bool connected;
    }

    /// <summary>POST .../participants/{account}/recorded: RecordedRequest.Parse (sent only once the result is durable).</summary>
    [Serializable]
    public sealed class RecordedRequest
    {
        public string manifestHash;
    }

    /// <summary>A participant or attempt state answer (ProcessEndpoints.StateDto).</summary>
    [Serializable]
    public sealed class StateResponse
    {
        public string state;
    }

    /// <summary>POST /server/v1/session/failed: FailedRequest.Parse.</summary>
    [Serializable]
    public sealed class FailedRequest
    {
        public string processSessionId;
        public string reason;
    }
}
