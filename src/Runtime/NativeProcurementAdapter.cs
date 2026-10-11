using System.Security.Cryptography;
using System.Text;
using AIWorldPawnshop.Models;
using Il2Cpp;
using MelonLoader;

namespace AIWorldPawnshop.Runtime;

public sealed class ProcurementApplication
{
    public bool Proposed { get; init; }
    public bool Scheduled { get; init; }
    public string RequestKey { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

internal static class NativeProcurementAdapter
{
    private static readonly HashSet<string> OpenStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "pending",
        "found",
        "not_found",
        "delivered",
        "not_found_returned"
    };

    public static ProcurementApplication ApplyConversation(
        WorldState world,
        ConversationRequest request,
        ConversationResponse response,
        ProcurementResolution resolution)
    {
        if (!resolution.Explicit)
            return Result(false, false, string.Empty, resolution.Reason);
        if (!resolution.Accepted)
            return Result(true, false, string.Empty, resolution.Reason);

        try
        {
            var playerStore = PlayerStore.Instance;
            var instance = playerStore?.currentClientInstance;
            var client = instance?.GetClientBlueprint();
            if (playerStore is null || client is null)
                return Result(true, false, string.Empty, "原版来客已经离开");

            var liveKey = FirstNotBlank(client.identifier, client.displayName);
            var expectedKey = FirstNotBlank(
                request.GameContext.Character.OriginalIdentifier,
                request.GameContext.Character.Key,
                request.CharacterKey);
            if (!liveKey.Equals(expectedKey, StringComparison.OrdinalIgnoreCase))
                return Result(true, false, string.Empty, "回复到达时来客已经改变");

            var intent = client.clientIntent.ToString();
            if (!IsSupportedIntent(intent))
                return Result(true, false, string.Empty, $"原版来客类型 {intent} 不适合附加找货交易");

            var existing = world.ProcurementRequests
                .Where(x =>
                    x.Status.Equals("pending", StringComparison.OrdinalIgnoreCase) ||
                    x.Status.Equals("found", StringComparison.OrdinalIgnoreCase) ||
                    x.Status.Equals("not_found", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(x =>
                    x.OriginalClientIdentifier.Equals(liveKey, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.Category = resolution.Category;
                existing.RequestedDescription = resolution.RequestedDescription;
                existing.PlayerRequestText = request.PlayerText;
                existing.AccessLevel = resolution.AccessLevel;
                existing.MaxRarityTier = resolution.MaxRarityTier;
                existing.AccessSearchModifier = resolution.AccessSearchModifier;
                existing.AccessReason = resolution.AccessReason;
                existing.PromisedPremiumPercent = resolution.PromisedPremiumPercent;
                existing.CooperationAtRequest = Math.Clamp(
                    response.Signals.Cooperation + response.Signals.Trust,
                    -4,
                    4);
                existing.OutcomeSummary =
                    $"玩家把找货要求更新为{resolution.RequestedDescription}，约定第 {existing.DueDay} 天再见。";
                return Result(true, true, existing.RequestKey, "已更新该顾客尚未完成的找货委托");
            }

            NativeReturnVisitAdapter.QueueIfMissing(playerStore, liveKey, resolution.DueInDays);
            var state = new ProcurementRequestState
            {
                CharacterKey = FirstNotBlank(
                    request.GameContext.Character.Key,
                    request.CharacterKey,
                    liveKey),
                CharacterDisplayName = FirstNotBlank(
                    request.GameContext.Character.DisplayName,
                    request.CharacterDisplayName,
                    client.realName,
                    client.displayName,
                    liveKey),
                OriginalClientIdentifier = liveKey,
                FactionKey = FirstNotBlank(
                    request.GameContext.Character.FactionKey,
                    request.FactionKey),
                OriginalIntent = intent,
                Category = resolution.Category,
                RequestedDescription = resolution.RequestedDescription,
                PlayerRequestText = request.PlayerText,
                AccessLevel = resolution.AccessLevel,
                MaxRarityTier = resolution.MaxRarityTier,
                AccessSearchModifier = resolution.AccessSearchModifier,
                AccessReason = resolution.AccessReason,
                CreatedDay = world.CurrentDay,
                DueDay = world.CurrentDay + resolution.DueInDays,
                PromisedPremiumPercent = resolution.PromisedPremiumPercent,
                CooperationAtRequest = Math.Clamp(
                    response.Signals.Cooperation + response.Signals.Trust,
                    -4,
                    4),
                Status = "pending"
            };
            state.OutcomeSummary =
                $"已答应为玩家寻找{state.RequestedDescription}，约定第 {state.DueDay} 天回来。";
            world.ProcurementRequests.Add(state);
            AddFact(
                world,
                "procurement_requested",
                $"{state.CharacterDisplayName}答应寻找{state.RequestedDescription}，约定第 {state.DueDay} 天回访");
            AddCharacterMemory(
                world,
                state,
                $"玩家托我寻找{state.RequestedDescription}，并约好第 {state.DueDay} 天回来",
                0,
                0.75);

            MelonLogger.Msg(
                $"[当铺世界] 已登记找货委托：{state.CharacterDisplayName} / " +
                $"{state.Category} / 第 {state.DueDay} 天");
            return Result(true, true, state.RequestKey, "找货委托已登记并安排原版顾客回访");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[当铺世界] 登记找货委托失败，原版状态保持不变：{ex.Message}");
            return Result(true, false, string.Empty, "原版回访队列无法登记该委托");
        }
    }

    public static void ResolveDueRequests(WorldState world)
    {
        foreach (var request in world.ProcurementRequests.Where(x =>
                     x.Status.Equals("pending", StringComparison.OrdinalIgnoreCase) &&
                     x.DueDay <= world.CurrentDay))
        {
            var plan = NativeProcurementCatalog.Plan(request, world.WorldSeed);
            request.ResolvedDay = world.CurrentDay;
            request.ResolvedItemKey = plan.ItemKey;
            request.ResolvedRarityTier = plan.RarityTier;
            request.ResolvedRarityLabel = plan.Available
                ? ProcurementRarity.Label(plan.RarityTier)
                : string.Empty;
            request.ResolvedLootWeight = plan.LootWeight;
            if (!plan.Available)
            {
                request.SearchChancePercent = 0;
                request.Status = "not_found";
                request.ResolvedItemName = string.Empty;
                request.OutcomeSummary = $"没有找到符合“{request.RequestedDescription}”的可靠货源：{plan.FailureReason}。";
                AddFact(
                    world,
                    "procurement_not_found",
                    $"{request.CharacterDisplayName}{request.OutcomeSummary}");
                MelonLogger.Msg(
                    $"[当铺世界] 找货受渠道限制：{request.CharacterDisplayName} / " +
                    $"{request.Category} / {plan.FailureReason}");
                continue;
            }

            var chance = ComputeSearchChance(world, request);
            var found = StableRoll(world, request) < chance;
            request.SearchChancePercent = chance;
            request.Status = found ? "found" : "not_found";
            request.ResolvedItemName = found ? plan.ItemName : string.Empty;
            request.OutcomeSummary = found
                ? $"找到了{plan.ItemName}（{request.ResolvedRarityLabel}），准备带去当铺。"
                : $"没有找到符合“{request.RequestedDescription}”的可靠货源。";
            AddFact(
                world,
                found ? "procurement_found" : "procurement_not_found",
                $"{request.CharacterDisplayName}{request.OutcomeSummary}");
            MelonLogger.Msg(
                $"[当铺世界] 找货检定：{request.CharacterDisplayName} / " +
                $"{request.Category} / {request.ResolvedRarityLabel} / " +
                $"权重 {request.ResolvedLootWeight:0.###} / {chance}% / " +
                $"{(found ? "找到" : "未找到")}");
        }

        TrimHistory(world);
    }

    public static string PrepareClient(WorldState world, StoreClient? client)
    {
        if (client is null)
            return string.Empty;

        var key = FirstNotBlank(client.identifier, client.displayName);
        var request = FindReturningRequest(world, key);
        if (request is null)
            return string.Empty;

        if (request.Status.Equals("found", StringComparison.OrdinalIgnoreCase))
        {
            var intent = client.clientIntent.ToString();
            if (!IsSupportedIntent(intent))
            {
                request.Status = "cancelled";
                request.OutcomeSummary = $"回访时原版来意变为 {intent}，本次委托没有注入交易流程。";
                return string.Empty;
            }

            request.OriginalIntent = intent;
            if (intent.Equals("BUY", StringComparison.OrdinalIgnoreCase))
                client.clientIntent = StoreClient.ClientIntent.SELLNBUY;
        }

        return request.RequestKey;
    }

    public static bool CompleteClientCreation(
        WorldState world,
        string requestKey,
        StoreClient? client,
        StoreClientInstance? instance)
    {
        if (string.IsNullOrWhiteSpace(requestKey))
            return false;

        var request = world.ProcurementRequests.FirstOrDefault(x =>
            x.RequestKey.Equals(requestKey, StringComparison.OrdinalIgnoreCase));
        if (request is null)
            return false;

        request.ReturnedDay = world.CurrentDay;
        if (request.Status.Equals("not_found", StringComparison.OrdinalIgnoreCase))
        {
            request.Status = "not_found_returned";
            AddCharacterMemory(
                world,
                request,
                $"我按约回来，但没能找到{request.RequestedDescription}",
                0,
                0.65);
            return true;
        }

        if (!request.Status.Equals("found", StringComparison.OrdinalIgnoreCase))
            return false;
        if (client is null || instance?.gridInv is null)
        {
            request.Status = "failed";
            request.OutcomeSummary = "顾客回访实例没有可用物品栏，委托未能交付。";
            return true;
        }

        GameItem? item = null;
        try
        {
            item = NativeProcurementCatalog.CreateItem(request);
            if (item is null || !TryAddToClientInventory(instance.gridInv, item))
            {
                RestoreIntent(client, request.OriginalIntent);
                item?.Destroy();
                request.Status = "failed";
                request.OutcomeSummary =
                    $"找到了{request.RequestedDescription}，但顾客物品栏无法容纳，委托未能交付。";
                MelonLogger.Warning(
                    $"[当铺世界] 委托货物未能放入顾客物品栏：{request.CharacterDisplayName}");
                return true;
            }

            request.Status = "delivered";
            request.ResolvedItemKey = item.identifier ?? string.Empty;
            request.ResolvedItemName = FirstNotBlank(
                Safe(() => item.GetDisplayName(true), string.Empty),
                item.name,
                item.identifier,
                request.RequestedDescription);
            request.DeliveredRuntimeKey = item.uniqueId;
            request.OriginalItemBaseValue = Safe(() => item.GetBaseValue(), item.unitBaseValue);
            if (request.PromisedPremiumPercent > 0)
            {
                var premiumValue = Math.Max(
                    1,
                    (long)Math.Round(
                        request.OriginalItemBaseValue *
                        (1d + request.PromisedPremiumPercent / 100d),
                        MidpointRounding.AwayFromZero));
                item.SetValue(premiumValue);
                request.ItemPremiumApplied = true;
            }
            request.OutcomeSummary =
                $"按约带来了{request.ResolvedItemName}，正在等待玩家查看或购买" +
                (request.ItemPremiumApplied
                    ? $"；约定的高价使这件货的基础报价增加 {request.PromisedPremiumPercent}% 。"
                    : "。"
                );
            AddFact(
                world,
                "procurement_delivered",
                $"{request.CharacterDisplayName}按约带来{request.ResolvedItemName}");
            AddCharacterMemory(
                world,
                request,
                $"我按玩家的委托找到了{request.ResolvedItemName}并带到店里",
                1,
                0.85);
            MelonLogger.Msg(
                $"[当铺世界] 委托货物已加入顾客物品栏：" +
                $"{request.CharacterDisplayName} / {request.ResolvedItemName} / " +
                $"原来意 {request.OriginalIntent} / 当前来意 {client.clientIntent}");
            return true;
        }
        catch (Exception ex)
        {
            RestoreIntent(client, request.OriginalIntent);
            try { item?.Destroy(); } catch { }
            request.Status = "failed";
            request.OutcomeSummary = "创建委托货物时发生错误，委托未能交付。";
            MelonLogger.Error($"[当铺世界] 注入委托货物失败：{ex.Message}");
            return true;
        }
    }

    public static bool RecordItemBought(WorldState world, GameItem? item, int cost)
    {
        if (item is null)
            return false;

        var runtimeKey = Safe(() => item.uniqueId, 0);
        var request = world.ProcurementRequests.FirstOrDefault(x =>
            x.Status.Equals("delivered", StringComparison.OrdinalIgnoreCase) &&
            x.DeliveredRuntimeKey == runtimeKey);
        if (request is null)
            return false;

        request.Status = "fulfilled";
        request.PurchasePrice = Math.Max(0, cost);
        request.OutcomeSummary =
            $"玩家以 {request.PurchasePrice} 买下了委托货物 {request.ResolvedItemName}。";
        AddFact(world, "procurement_fulfilled", request.OutcomeSummary);
        AddCharacterMemory(
            world,
            request,
            $"玩家履行约定，以 {request.PurchasePrice} 买下我找来的{request.ResolvedItemName}",
            1,
            0.95,
            2);
        TrimHistory(world);
        return true;
    }

    public static bool PrepareItemBought(WorldState world, GameItem? item)
    {
        if (item is null)
            return false;

        var runtimeKey = Safe(() => item.uniqueId, 0);
        var request = world.ProcurementRequests.FirstOrDefault(x =>
            x.Status.Equals("delivered", StringComparison.OrdinalIgnoreCase) &&
            x.DeliveredRuntimeKey == runtimeKey &&
            x.ItemPremiumApplied);
        if (request is null)
            return false;

        item.SetValue(Math.Max(1, request.OriginalItemBaseValue));
        request.ItemPremiumApplied = false;
        return true;
    }

    public static bool RecordClientDismissed(WorldState world, StoreClient? client)
    {
        var clientIdentifier = FirstNotBlank(client?.identifier, client?.displayName);
        if (string.IsNullOrWhiteSpace(clientIdentifier))
            return false;

        var changed = false;
        foreach (var request in world.ProcurementRequests.Where(x =>
                     x.OriginalClientIdentifier.Equals(
                         clientIdentifier,
                         StringComparison.OrdinalIgnoreCase) &&
                     x.ReturnedDay == world.CurrentDay))
        {
            if (request.Status.Equals("delivered", StringComparison.OrdinalIgnoreCase))
            {
                request.Status = "declined";
                request.OutcomeSummary =
                    $"玩家没有买下委托寻找的{request.ResolvedItemName}。";
                AddFact(world, "procurement_declined", request.OutcomeSummary);
                AddCharacterMemory(
                    world,
                    request,
                    $"玩家让我找来{request.RequestedDescription}，最后却没有买下",
                    -1,
                    0.9,
                    -2);
                changed = true;
            }
            else if (request.Status.Equals("not_found_returned", StringComparison.OrdinalIgnoreCase))
            {
                request.Status = "failed";
                changed = true;
            }
        }

        if (client is not null && world.ProcurementRequests.Any(x =>
                x.OriginalClientIdentifier.Equals(
                    clientIdentifier,
                    StringComparison.OrdinalIgnoreCase) &&
                x.ReturnedDay == world.CurrentDay &&
                x.OriginalIntent.Equals("BUY", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                client.clientIntent = StoreClient.ClientIntent.BUY;
            }
            catch
            {
                // The native visitor may already be disposed; the next blueprint is unaffected.
            }
        }

        if (changed)
            TrimHistory(world);
        return changed;
    }

    public static bool IsDeliveredItem(WorldState world, int runtimeKey) =>
        world.ProcurementRequests.Any(x =>
            x.DeliveredRuntimeKey == runtimeKey &&
            x.Status.Equals("delivered", StringComparison.OrdinalIgnoreCase));

    private static ProcurementRequestState? FindReturningRequest(
        WorldState world,
        string clientIdentifier) =>
        world.ProcurementRequests
            .Where(x =>
                x.DueDay <= world.CurrentDay &&
                (x.Status.Equals("found", StringComparison.OrdinalIgnoreCase) ||
                 x.Status.Equals("not_found", StringComparison.OrdinalIgnoreCase)) &&
                x.OriginalClientIdentifier.Equals(
                    clientIdentifier,
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.DueDay)
            .FirstOrDefault();

    private static int ComputeSearchChance(
        WorldState world,
        ProcurementRequestState request)
    {
        var chance = request.Category switch
        {
            "junk" => 85,
            "food" => 78,
            "household" => 72,
            "material" => 68,
            "medical" => 58,
            "tool" => 52,
            "weapon" => 44,
            "module" => 38,
            "access_card" => 24,
            _ => 50
        };

        if (world.Characters.TryGetValue(request.CharacterKey, out var character))
            chance += Math.Clamp(character.Relationship / 7, -14, 14);

        if (!string.IsNullOrWhiteSpace(request.FactionKey) &&
            world.Factions.TryGetValue(request.FactionKey, out var faction))
        {
            chance += Math.Clamp(faction.Reputation / 10, -10, 10);
        }

        chance += request.AccessSearchModifier;
        chance -= ProcurementRarity.Penalty(request.ResolvedRarityTier);
        chance += Math.Clamp(request.DueDay - request.CreatedDay - 1, 0, 2) * 8;
        chance += request.CooperationAtRequest * 3;
        chance += Math.Clamp(request.PromisedPremiumPercent / 3, 0, 12);

        var supplyEffect = world.ActiveEvents
            .Where(x => x.ItemGroup.Equals(request.Category, StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.SupplyModifier);
        supplyEffect += world.ActiveModifications
            .Where(x => x.TargetKey.Equals(request.Category, StringComparison.OrdinalIgnoreCase) &&
                        x.Field.Equals("supply_modifier", StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Value);
        supplyEffect += world.ActiveModifications
            .Where(x => x.TargetKey.Equals(request.Category, StringComparison.OrdinalIgnoreCase) &&
                        x.Field.Equals("demand_modifier", StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Value * 0.5);
        chance += (int)Math.Round(Math.Clamp(supplyEffect, -0.25, 0.25) * 100d);
        return Math.Clamp(chance, 10, 95);
    }

    private static int StableRoll(WorldState world, ProcurementRequestState request)
    {
        var input = Encoding.UTF8.GetBytes(
            $"{world.WorldSeed}:{request.RequestKey}:{request.DueDay}:{request.Category}");
        var hash = SHA256.HashData(input);
        var value = ((uint)hash[0] << 24) |
                    ((uint)hash[1] << 16) |
                    ((uint)hash[2] << 8) |
                    hash[3];
        return (int)(value % 100);
    }

    internal static bool TryAddToClientInventory(GameGridInventory inventory, GameItem item)
    {
        var slot = inventory.TryFindOneValidInventorySlot(item, false);
        if (slot is not null)
            slot.AcceptUnchecked();
        else
        {
            var forcedItems = new Il2CppSystem.Collections.Generic.List<GameItem>();
            forcedItems.Add(item);
            ((GameInventory)inventory).UncheckedAcceptAll(forcedItems);
        }

        inventory.Validate();
        var items = inventory.items;
        if (items is null)
            return false;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not null && items[i].uniqueId == item.uniqueId)
                return true;
        }
        return false;
    }

    private static void RestoreIntent(StoreClient? client, string originalIntent)
    {
        if (client is null ||
            !originalIntent.Equals("BUY", StringComparison.OrdinalIgnoreCase))
            return;
        client.clientIntent = StoreClient.ClientIntent.BUY;
    }

    private static bool IsSupportedIntent(string intent) =>
        intent.Equals("BUY", StringComparison.OrdinalIgnoreCase) ||
        intent.Equals("SELL", StringComparison.OrdinalIgnoreCase) ||
        intent.Equals("SELLNBUY", StringComparison.OrdinalIgnoreCase);

    private static void AddCharacterMemory(
        WorldState world,
        ProcurementRequestState request,
        string summary,
        int valence,
        double importance,
        int relationshipDelta = 0)
    {
        if (world.Characters.TryGetValue(request.CharacterKey, out var character))
        {
            if (!character.Memories.Any(x =>
                    x.Summary.Equals(summary, StringComparison.OrdinalIgnoreCase)))
            {
                character.Memories.Add(new MemoryRecord
                {
                    Kind = "procurement",
                    Summary = summary,
                    CreatedDay = world.CurrentDay,
                    Importance = Math.Clamp(importance, 0, 1),
                    Valence = Math.Sign(valence)
                });
            }

            character.Relationship = Math.Clamp(
                character.Relationship + relationshipDelta,
                -100,
                100);
            TrimMemories(character.Memories);
        }

        if (string.IsNullOrWhiteSpace(request.FactionKey))
            return;

        if (!world.Factions.TryGetValue(request.FactionKey, out var faction))
        {
            faction = new FactionState { FactionKey = request.FactionKey };
            world.Factions[request.FactionKey] = faction;
        }

        var factionSummary = $"阵营成员{request.CharacterDisplayName}的经历：{summary}";
        if (!faction.Memories.Any(x =>
                x.Summary.Equals(factionSummary, StringComparison.OrdinalIgnoreCase)))
        {
            faction.Memories.Add(new MemoryRecord
            {
                Kind = "procurement",
                Summary = factionSummary,
                CreatedDay = world.CurrentDay,
                Importance = Math.Clamp(importance * 0.8, 0, 1),
                Valence = Math.Sign(valence)
            });
        }

        var factionDelta = Math.Sign(relationshipDelta);
        faction.Reputation = Math.Clamp(faction.Reputation + factionDelta, -100, 100);
        if (factionDelta != 0)
        {
            faction.RecentChanges.Add(factionDelta);
            if (faction.RecentChanges.Count > 12)
                faction.RecentChanges.RemoveRange(0, faction.RecentChanges.Count - 12);
        }
        TrimMemories(faction.Memories);
    }

    private static void TrimMemories(List<MemoryRecord> memories)
    {
        if (memories.Count <= 12)
            return;

        var retained = memories
            .OrderByDescending(x => x.Importance)
            .ThenByDescending(x => x.CreatedDay)
            .Take(12)
            .ToList();
        memories.Clear();
        memories.AddRange(retained);
    }

    private static void AddFact(WorldState world, string kind, string summary)
    {
        world.Facts.Add(new FactRecord
        {
            Day = world.CurrentDay,
            Kind = kind,
            Summary = summary,
            Visibility = "actor"
        });
        if (world.Facts.Count > 200)
            world.Facts.RemoveRange(0, world.Facts.Count - 200);
    }

    private static void TrimHistory(WorldState world)
    {
        if (world.ProcurementRequests.Count <= 50)
            return;

        var keep = world.ProcurementRequests
            .OrderByDescending(x => OpenStatuses.Contains(x.Status))
            .ThenByDescending(x => x.CreatedDay)
            .Take(50)
            .ToHashSet();
        world.ProcurementRequests.RemoveAll(x => !keep.Contains(x));
    }

    private static ProcurementApplication Result(
        bool proposed,
        bool scheduled,
        string requestKey,
        string message) => new()
    {
        Proposed = proposed,
        Scheduled = scheduled,
        RequestKey = requestKey,
        Message = message
    };

    private static string FirstNotBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static T Safe<T>(Func<T> getter, T fallback)
    {
        try { return getter(); }
        catch { return fallback; }
    }
}
