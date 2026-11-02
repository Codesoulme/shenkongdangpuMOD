
using AIWorldPawnshop.Compatibility;

namespace AIWorldPawnshop.Runtime;

/// <summary>
/// Ownership evidence used when the native counter combines the player and
/// client-facing areas into one list.  A non-owned tag is deliberately
/// stronger than a stale inventory membership so customer goods can never be
/// advertised as player goods.
/// </summary>
internal enum CounterItemOwner
{
    Unknown,
    Player,
    Client
}

internal static class CounterOwnershipRules
{
    internal static CounterItemOwner Classify(
        bool inPlayerArea,
        bool inClientArea,
        bool? ownershipTag,
        bool inPlayerOwnedSet)
    {
        if (ownershipTag == false)
            return CounterItemOwner.Client;
        if (ownershipTag == true)
            return CounterItemOwner.Player;
        if (inClientArea)
            return CounterItemOwner.Client;
        if (inPlayerOwnedSet)
            return CounterItemOwner.Player;

        // A location alone is not enough to claim ownership.  In particular,
        // an unreadable tag on a player-side item must not leak into the
        // player_offered_items contract.
        return CounterItemOwner.Unknown;
    }
}

internal sealed class CounterOwnershipSnapshot
{
    public List<GameItem> ClientInventoryItems { get; } = new();
    public List<GameItem> ClientCounterItems { get; } = new();
    public List<GameItem> PlayerCounterItems { get; } = new();
    public HashSet<int> ClientInventoryIds { get; } = new();
    public HashSet<int> ClientCounterIds { get; } = new();
    public HashSet<int> PlayerCounterIds { get; } = new();
    public HashSet<int> PlayerOwnedIds { get; } = new();

    private readonly Dictionary<int, CounterItemOwner> _owners = new();

    // A runtime key can be present in more than one native collection while
    // the counter UI is rebuilding.  Keep an explicit conflict set so an
    // unresolved key cannot later fall through to a weaker inventory test.
    private readonly HashSet<int> _conflictingIds = new();

    public static CounterOwnershipSnapshot Capture(
        PlayerStore? playerStore,
        StoreClientInstance? clientInstance)
    {
        var snapshot = new CounterOwnershipSnapshot();
        snapshot.ReadPlayerOwnedItems(playerStore);
        snapshot.ReadClientInventory(clientInstance);

        try
        {
            var entry = EmporiumEntry.Instance;
            var clientArea = ReadGridItems(entry?.frontInvinvElement);
            var playerArea = ReadGridItems(entry?.backInvinvElementCounter);
            var allCounters = ReadCounterItems(entry);

            var clientAreaIds = ToIds(clientArea);
            var playerAreaIds = ToIds(playerArea);
            var evidence = new Dictionary<int, OwnershipEvidence>();
            var noKeyItems = new List<(GameItem Item, bool InClientArea, bool InPlayerArea)>();

            foreach (var item in clientArea.Concat(playerArea).Concat(allCounters))
            {
                if (item is null)
                    continue;

                var runtimeKey = item.uniqueId;
                var inClientArea = runtimeKey > 0 && clientAreaIds.Contains(runtimeKey);
                var inPlayerArea = runtimeKey > 0 && playerAreaIds.Contains(runtimeKey);
                if (runtimeKey <= 0)
                {
                    noKeyItems.Add((item, inClientArea, inPlayerArea));
                    continue;
                }

                if (!evidence.TryGetValue(runtimeKey, out var itemEvidence))
                {
                    itemEvidence = new OwnershipEvidence(item);
                    evidence.Add(runtimeKey, itemEvidence);
                }

                itemEvidence.InClientArea |= inClientArea;
                itemEvidence.InPlayerArea |= inPlayerArea;
                itemEvidence.InPlayerOwnedSet |= snapshot.PlayerOwnedIds.Contains(runtimeKey);
                itemEvidence.ObserveTag(TryReadOwnership(item));
            }

            // Customer inventory is independent evidence even when the item
            // has already moved from gridInv to the counter.
            foreach (var item in snapshot.ClientInventoryItems)
            {
                var runtimeKey = SafeRuntimeKey(item);
                if (runtimeKey <= 0)
                    continue;
                if (!evidence.TryGetValue(runtimeKey, out var itemEvidence))
                {
                    itemEvidence = new OwnershipEvidence(item);
                    evidence.Add(runtimeKey, itemEvidence);
                }

                itemEvidence.InClientInventory = true;
                itemEvidence.InPlayerOwnedSet |= snapshot.PlayerOwnedIds.Contains(runtimeKey);
                itemEvidence.ObserveTag(TryReadOwnership(item));
            }

            foreach (var pair in evidence)
            {
                var owner = pair.Value.Resolve();
                snapshot._owners[pair.Key] = owner;
                if (owner == CounterItemOwner.Unknown && pair.Value.HasConflict)
                    snapshot._conflictingIds.Add(pair.Key);

                if (owner == CounterItemOwner.Client)
                {
                    AddUnique(snapshot.ClientCounterItems, snapshot.ClientCounterIds, pair.Value.Item);
                    // ClientInventoryIds is retained for fast lookup, but a
                    // conflicting key must never be accepted through it.
                }
                else if (owner == CounterItemOwner.Player)
                {
                    AddUnique(snapshot.PlayerCounterItems, snapshot.PlayerCounterIds, pair.Value.Item);
                }
            }

            // Items without a runtime key cannot participate in an exact
            // exchange, but they can still be classified for context display.
            foreach (var entryWithoutKey in noKeyItems)
            {
                var owner = CounterOwnershipRules.Classify(
                    entryWithoutKey.InPlayerArea,
                    entryWithoutKey.InClientArea,
                    TryReadOwnership(entryWithoutKey.Item),
                    false);
                if (owner == CounterItemOwner.Client)
                    AddUnique(snapshot.ClientCounterItems, snapshot.ClientCounterIds, entryWithoutKey.Item);
                else if (owner == CounterItemOwner.Player)
                    AddUnique(snapshot.PlayerCounterItems, snapshot.PlayerCounterIds, entryWithoutKey.Item);
            }
        }
        catch
        {
            // Counter inventories are unavailable while the store scene is
            // being rebuilt.  The inventory evidence above remains usable.
        }

        return snapshot;
    }

    public CounterItemOwner Resolve(GameItem? item)
    {
        if (item is null)
            return CounterItemOwner.Unknown;

        int runtimeKey;
        try
        {
            runtimeKey = item.uniqueId;
        }
        catch
        {
            // An IL2CPP object can be destroyed between context capture and
            // negotiation.  Treat it as unverifiable instead of aborting the
            // whole conversation context build.
            return CounterItemOwner.Unknown;
        }
        if (runtimeKey > 0)
        {
            if (_owners.TryGetValue(runtimeKey, out var owner))
                return owner;
            if (_conflictingIds.Contains(runtimeKey))
                return CounterItemOwner.Unknown;
            if (ClientInventoryIds.Contains(runtimeKey))
                return CounterItemOwner.Client;
            if (PlayerOwnedIds.Contains(runtimeKey))
                return CounterItemOwner.Player;
        }

        return CounterOwnershipRules.Classify(
            inPlayerArea: false,
            inClientArea: false,
            ownershipTag: TryReadOwnership(item),
            inPlayerOwnedSet: runtimeKey > 0 && PlayerOwnedIds.Contains(runtimeKey));
    }

    public bool IsClientItem(GameItem? item) => Resolve(item) == CounterItemOwner.Client;

    public bool IsPlayerItem(GameItem? item) => Resolve(item) == CounterItemOwner.Player;

    /// <summary>
    /// Returns true when independent native sources disagree about the same
    /// runtime object.  Callers must not use a UI fallback to turn this state
    /// into a guessed owner.
    /// </summary>
    public bool IsConflicted(GameItem? item)
    {
        var runtimeKey = SafeRuntimeKey(item);
        return runtimeKey > 0 && _conflictingIds.Contains(runtimeKey);
    }

    /// <summary>
    /// Checks a runtime key without manufacturing a GameItem wrapper.  The
    /// conversation contract only exposes keys, while the native adapter can
    /// still use the item collections below when it needs the live object.
    /// Unknown keys intentionally return false so callers fail closed.
    /// </summary>
    public bool IsClientRuntimeKey(int runtimeKey) =>
        runtimeKey > 0 &&
        !_conflictingIds.Contains(runtimeKey) &&
        ((_owners.TryGetValue(runtimeKey, out var owner) && owner == CounterItemOwner.Client) ||
         (!_owners.ContainsKey(runtimeKey) &&
          (ClientInventoryIds.Contains(runtimeKey) || ClientCounterIds.Contains(runtimeKey))));

    public bool IsPlayerRuntimeKey(int runtimeKey) =>
        runtimeKey > 0 &&
        !_conflictingIds.Contains(runtimeKey) &&
        ((_owners.TryGetValue(runtimeKey, out var owner) && owner == CounterItemOwner.Player) ||
         (!_owners.ContainsKey(runtimeKey) &&
          (PlayerCounterIds.Contains(runtimeKey) || PlayerOwnedIds.Contains(runtimeKey))));

    private void ReadPlayerOwnedItems(PlayerStore? playerStore)
    {
        try
        {
            // PlayerStore.FindAllItem(bool) performs a global native type scan
            // in this game build.  Its scan includes GameGridInventory and
            // GameSlotInventory, which are Il2Cpp data wrappers rather than
            // UnityEngine.Object components.  Calling it from a conversation
            // therefore floods the game log and can destabilize CoreCLR.
            // The counter snapshot already contains the authoritative player
            // area; use that direct grid instead of asking the game to rescan
            // every inventory object.
            var entry = EmporiumEntry.Instance;
            var counterItems = ReadGridItems(entry?.backInvinvElementCounter);
            if (counterItems.Count == 0)
                return;

            foreach (var item in counterItems)
            {
                if (item is null || SafeRuntimeKey(item) <= 0)
                    continue;

                // A customer item explicitly marked non-owned remains
                // customer-owned even when it was temporarily placed on the
                // player-side counter.
                // An unreadable ownership flag is not evidence of player
                // ownership.  Keep it unknown so a customer item cannot be
                // promoted into the player's exchange candidates.
                if (TryReadOwnership(item) == true)
                    PlayerOwnedIds.Add(SafeRuntimeKey(item));
            }
        }
        catch
        {
            // Direct ownership tags and the counter-area evidence below still
            // provide a useful fallback during scene rebuilds.
        }
    }

    private void ReadClientInventory(StoreClientInstance? clientInstance)
    {
        try
        {
            var items = FormalGameAccess.GetItems(FormalGameAccess.GetInventory(clientInstance));

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item is null)
                    continue;
                AddUnique(ClientInventoryItems, ClientInventoryIds, item);
            }
        }
        catch
        {
            // A client can be dismissed while a conversation request is
            // being assembled.
        }
    }

    private static List<GameItem> ReadGridItems(GameGridInventory? inventory)
    {
        var result = new List<GameItem>();
        try
        {
            var items = FormalGameAccess.GetItems(inventory);

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item is not null)
                    AddUnique(result, null, item);
            }
        }
        catch
        {
            // Optional during scene transitions.
        }

        return result;
    }

    private static List<GameItem> ReadCounterItems(EmporiumEntry? entry)
    {
        var result = new List<GameItem>();
        try
        {
            var items = entry?.GetCountersItems();
            if (items is null)
                return result;

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item is not null)
                    AddUnique(result, null, item);
            }
        }
        catch
        {
            // Optional during scene transitions.
        }

        return result;
    }

    private static HashSet<int> ToIds(IEnumerable<GameItem> items)
    {
        var ids = new HashSet<int>();
        foreach (var item in items)
        {
            if (item is not null && item.uniqueId > 0)
                ids.Add(item.uniqueId);
        }

        return ids;
    }

    private static bool? TryReadOwnership(GameItem item)
    {
        try { return GeneralHelper.IsItemOwned(item); }
        catch { return null; }
    }

    private static int SafeRuntimeKey(GameItem? item)
    {
        try { return item?.uniqueId ?? 0; }
        catch { return 0; }
    }

    private sealed class OwnershipEvidence
    {
        public OwnershipEvidence(GameItem item)
        {
            Item = item;
        }

        public GameItem Item { get; }
        public bool InClientArea { get; set; }
        public bool InPlayerArea { get; set; }
        public bool InClientInventory { get; set; }
        public bool InPlayerOwnedSet { get; set; }
        public bool TagConflict { get; private set; }
        public bool? Tag { get; private set; }

        public bool HasConflict =>
            TagConflict ||
            (InPlayerOwnedSet && (InClientInventory || InClientArea || Tag == false)) ||
            (Tag == true && (InClientInventory || InClientArea));

        public void ObserveTag(bool? tag)
        {
            if (!tag.HasValue)
                return;
            if (Tag.HasValue && Tag.Value != tag.Value)
                TagConflict = true;
            else
                Tag = tag;
        }

        public CounterItemOwner Resolve()
        {
            if (HasConflict)
                return CounterItemOwner.Unknown;

            // An explicit native ownership tag is stronger than a transient
            // counter location, provided no independent evidence disagrees.
            if (Tag == false || InClientInventory || InClientArea)
                return CounterItemOwner.Client;
            if (Tag == true || InPlayerOwnedSet)
                return CounterItemOwner.Player;
            return CounterItemOwner.Unknown;
        }
    }

    private static void AddUnique(
        List<GameItem> target,
        HashSet<int>? ids,
        GameItem item)
    {
        if (item is null)
            return;

        if (item.uniqueId > 0)
        {
            if (ids is not null && !ids.Add(item.uniqueId))
                return;
            if (target.Any(x => x is not null && x.uniqueId == item.uniqueId))
                return;
        }
        else if (target.Contains(item))
        {
            return;
        }

        target.Add(item);
    }
}
