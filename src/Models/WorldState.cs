namespace AIWorldPawnshop.Models;

public sealed class WorldState
{
    public string SchemaVersion { get; set; } = ModConstants.SchemaVersion;
    public string RulesVersion { get; set; } = "0.3";
    public string WorldId { get; set; } = Guid.NewGuid().ToString("N");
    public int CurrentDay { get; set; }
    public int WorldSeed { get; set; } = Random.Shared.Next();
    /// <summary>
    /// Monotonic revision for extension-side snapshots. It changes only after
    /// a world mutation is accepted, so stale async responses can be rejected.
    /// </summary>
    public long StateRevision { get; set; }
    public Dictionary<string, FactionState> Factions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CharacterState> Characters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<WorldEventState> ActiveEvents { get; set; } = new();
    public List<WorldModificationState> ActiveModifications { get; set; } = new();
    public List<FactRecord> Facts { get; set; } = new();
    public List<NewsRecord> News { get; set; } = new();
    public List<ProcurementRequestState> ProcurementRequests { get; set; } = new();
    public List<ReturnVisitState> ReturnVisits { get; set; } = new();
    public List<CarriedInventoryState> CarriedInventories { get; set; } = new();
    public List<ThirdPartyTaskState> ThirdPartyTasks { get; set; } = new();
    public string LastDailyResolutionHash { get; set; } = string.Empty;

    public long TouchRevision()
    {
        if (StateRevision < 0)
            StateRevision = 0;
        return ++StateRevision;
    }
}

public sealed class FactionState
{
    public string FactionKey { get; set; } = string.Empty;
    public int Reputation { get; set; }
    public List<int> RecentChanges { get; set; } = new();
    public List<MemoryRecord> Memories { get; set; } = new();
}

public sealed class CharacterState
{
    public string CharacterKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    // Persist the native identifier separately from the Mod's canonical key.
    // Future-client queues accept the former, while conversation state uses the latter.
    public string OriginalIdentifier { get; set; } = string.Empty;
    public string OriginalRealName { get; set; } = string.Empty;
    public string FactionKey { get; set; } = string.Empty;
    public bool IsNamedSpecialCharacter { get; set; }
    public bool CanReturn { get; set; }
    public bool CanReturnAsBuyer { get; set; }
    public int Relationship { get; set; }
    public int InteractionCount { get; set; }
    public int? LastInteractionDay { get; set; }
    public List<MemoryRecord> Memories { get; set; } = new();
}

public sealed class MemoryRecord
{
    public string MemoryId { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "information";
    public string Summary { get; set; } = string.Empty;
    public int CreatedDay { get; set; }
    public double Importance { get; set; }
    public int Valence { get; set; }
}

public sealed class WorldEventState
{
    public string EventKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string ItemGroup { get; set; } = string.Empty;
    public int StartDay { get; set; }
    public int DurationDays { get; set; }
    public double PriceModifier { get; set; }
    public double DemandModifier { get; set; }
    public double SupplyModifier { get; set; }
    public int NewsImportance { get; set; }
}

public sealed class WorldModificationState
{
    public string ModificationId { get; set; } = Guid.NewGuid().ToString("N");
    public string TargetKey { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public double Value { get; set; }
    public int StartDay { get; set; }
    public int DurationDays { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public string Source { get; set; } = "gm";
    public string ExecutorKey { get; set; } = "world.modify";
    public double? OriginalValue { get; set; }
    public bool Restored { get; set; }
}

public sealed class FactRecord
{
    public string FactId { get; set; } = Guid.NewGuid().ToString("N");
    public int Day { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Visibility { get; set; } = "private";
}

public sealed class NewsRecord
{
    public string NewsId { get; set; } = Guid.NewGuid().ToString("N");
    public int Day { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public int Importance { get; set; }
    public string SourceEventKey { get; set; } = string.Empty;
}

public sealed class ProcurementRequestState
{
    public string RequestKey { get; set; } = Guid.NewGuid().ToString("N");
    public string CharacterKey { get; set; } = string.Empty;
    public string CharacterDisplayName { get; set; } = string.Empty;
    public string OriginalClientIdentifier { get; set; } = string.Empty;
    public string FactionKey { get; set; } = string.Empty;
    public string OriginalIntent { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string RequestedDescription { get; set; } = string.Empty;
    public string PlayerRequestText { get; set; } = string.Empty;
    public int AccessLevel { get; set; } = 2;
    public int MaxRarityTier { get; set; } = 2;
    public int AccessSearchModifier { get; set; }
    public string AccessReason { get; set; } = "旧版委托按稳定渠道兼容";
    public int CreatedDay { get; set; }
    public int DueDay { get; set; }
    public int PromisedPremiumPercent { get; set; }
    public int CooperationAtRequest { get; set; }
    public int? SearchChancePercent { get; set; }
    public string Status { get; set; } = "pending";
    public int? ResolvedDay { get; set; }
    public int? ReturnedDay { get; set; }
    public string ResolvedItemKey { get; set; } = string.Empty;
    public string ResolvedItemName { get; set; } = string.Empty;
    public int ResolvedRarityTier { get; set; }
    public string ResolvedRarityLabel { get; set; } = string.Empty;
    public float ResolvedLootWeight { get; set; }
    public int? DeliveredRuntimeKey { get; set; }
    public long OriginalItemBaseValue { get; set; }
    public bool ItemPremiumApplied { get; set; }
    public int? PurchasePrice { get; set; }
    public string OutcomeSummary { get; set; } = string.Empty;
}

public sealed class ReturnVisitState
{
    public string VisitKey { get; set; } = Guid.NewGuid().ToString("N");
    public string CharacterKey { get; set; } = string.Empty;
    public string CharacterDisplayName { get; set; } = string.Empty;
    public string OriginalClientIdentifier { get; set; } = string.Empty;
    public int CreatedDay { get; set; }
    public int DueDay { get; set; }
    public int QueueDelayDays { get; set; } = 1;
    public int DeferralCount { get; set; }
    public string PlayerInvitation { get; set; } = string.Empty;
    public string Status { get; set; } = "scheduled";
    public int? ReturnedDay { get; set; }
    public string OutcomeSummary { get; set; } = string.Empty;
}

public sealed class CarriedInventoryState
{
    public string CharacterKey { get; set; } = string.Empty;
    public string OriginalClientIdentifier { get; set; } = string.Empty;
    public int VisitDay { get; set; }
    public string OriginalIntent { get; set; } = string.Empty;
    public bool SellingUnlocked { get; set; }
    public List<int> RuntimeItemKeys { get; set; } = new();
}

public sealed class ThirdPartyTaskState
{
    public string TaskKey { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceCharacterKey { get; set; } = string.Empty;
    public string TargetCharacterKey { get; set; } = string.Empty;
    public string TargetNativeIdentifier { get; set; } = string.Empty;
    public string TargetDisplayName { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public int CreatedDay { get; set; }
    public int DueDay { get; set; }
    public string Status { get; set; } = "proposed";
    public string OutcomeSummary { get; set; } = string.Empty;
    public bool NativeQueueScheduled { get; set; }
    public bool NativeQueueOwned { get; set; }
    public int? NativeQueueDay { get; set; }
    public int? ArrivedDay { get; set; }
}
