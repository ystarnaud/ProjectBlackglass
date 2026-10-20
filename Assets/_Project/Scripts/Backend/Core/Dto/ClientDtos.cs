using System;

// The player client's /v1 bodies (AuthEndpoints.cs, PlayerDtos.cs, AttemptDtos.cs, DeployRequest.cs, AdmissionStore's 201
// body), for Stage 16. Same rules as ProcessDtos.cs. A JSON null in a number or object member reads as its default with
// JsonUtility, so presence is never inferred from these (the backend's status codes say it, e.g. 409 NotProvisioned).
// Not here: the inventory-operations request, whose polymorphic operations JsonUtility can't write without members the
// backend refuses (Stage 16 writes it with CanonicalJsonWriter nodes).
namespace Blackglass.Backend.Core
{
    /// <summary>POST /v1/auth/dev-login (development only).</summary>
    [Serializable]
    public sealed class DevLoginRequest
    {
        public string accountKey;
    }

    /// <summary>The access token is a credential: never logged.</summary>
    [Serializable]
    public sealed class DevLoginResponse
    {
        public string accessToken;
        public string expiresAt;
        public string accountId;
    }

    /// <summary>GET /v1/me.</summary>
    [Serializable]
    public sealed class MeResponse
    {
        public string accountId;
        public long inventoryVersion;
        public EnergyDto energy;
        public string activeAttemptId;
        public InsuranceStatusDto insurance;
    }

    [Serializable]
    public sealed class EnergyDto
    {
        public int balance;
        public int cap;
        public string nextRegenAt;
    }

    [Serializable]
    public sealed class InsuranceStatusDto
    {
        public bool active;
        public string policyId;
        public string expiresAt;
    }

    /// <summary>GET /v1/operatives.</summary>
    [Serializable]
    public sealed class RosterResponse
    {
        public long version;
        public RosterOperativeDto[] operatives;
    }

    [Serializable]
    public sealed class RosterOperativeDto
    {
        public string operativeId;
        public string definitionId;
        public bool definitionKnown;
        public int experience;
        public int rank;
        public int pendingPicks;
        public string[] choices;
    }

    /// <summary>GET /v1/inventory.</summary>
    [Serializable]
    public sealed class InventoryResponse
    {
        public long version;
        public bool locked;
        public InventoryLocationDto stash;
        public InventoryBagDto[] operatives;
    }

    [Serializable]
    public sealed class InventoryLocationDto
    {
        public InventoryInstanceDto[] instances;
        public InventoryStackDto[] stacks;
    }

    [Serializable]
    public sealed class InventoryInstanceDto
    {
        public string itemId;
        public string definitionId;
        public bool definitionKnown;
    }

    [Serializable]
    public sealed class InventoryStackDto
    {
        public string definitionId;
        public bool definitionKnown;
        public int quantity;
    }

    [Serializable]
    public sealed class InventoryBagDto
    {
        public string operativeId;
        public InventoryLocationDto bag;
        public EquipmentSlotsDto equipment;
    }

    /// <summary>POST /v1/attempts (with an Idempotency-Key header).</summary>
    [Serializable]
    public sealed class DeployRequest
    {
        public string missionTemplate;
        public string[] operativeIds;
    }

    [Serializable]
    public sealed class DeployResponse
    {
        public string attemptId;
        public string state;
        public DeployEnergyDto energy;
        public DeployInsuranceDto insurance;
    }

    [Serializable]
    public sealed class DeployEnergyDto
    {
        public int cost;
        public int balanceAfter;
    }

    [Serializable]
    public sealed class DeployInsuranceDto
    {
        public bool covered;
        public string policyId;
        public int policyVersion;
    }

    /// <summary>POST /v1/attempts/{attemptId}/tickets. The ticket is a credential, shown once: never logged.</summary>
    [Serializable]
    public sealed class TicketResponse
    {
        public string ticket;
        public string ticketExpiresAt;
    }
}
