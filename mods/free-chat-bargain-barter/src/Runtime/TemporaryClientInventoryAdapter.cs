using System.Security.Cryptography;
using System.Text;
using AIWorldPawnshop.Compatibility;
using AIWorldPawnshop.Models;
using MelonLoader;

namespace AIWorldPawnshop.Runtime;

internal sealed class CarriedInventoryApplication
{
    public bool Proposed { get; init; }
    public bool Applied { get; init; }
    public string Message { get; init; } = string.Empty;
}

internal static class TemporaryClientInventoryAdapter
{
    public static bool InjectForBuyer(
        WorldState world,
        StoreClient? client,
        StoreClientInstance? instance)
    {
        var inventory = FormalGameAccess.GetInventory(instance);
        if (client is null || inventory is null ||
            client.clientIntent != StoreClient.ClientIntent.BUY)
            return false;

        var clientKey = FirstNotBlank(client.identifier, client.displayName);
        if (string.IsNullOrWhiteSpace(clientKey))
            return false;
        if (world.CarriedInventories.Any(x =>
                x.VisitDay == world.CurrentDay &&
                x.OriginalClientIdentifier.Equals(clientKey, StringComparison.OrdinalIgnoreCase)))
            return false;

        var context = NativeConversationContextBuilder.Build(
            "store",
            clientKey,
            FirstNotBlank(client.realName, client.displayName, clientKey),
            world,
            client,
            instance);
        var decisions = ProcurementAccessPolicy.EvaluateAll(context)
            .Where(x => x.Allowed)
            .OrderByDescending(x => x.AccessLevel)
            .ThenBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (decisions.Count == 0)
            return false;

        var countRoll = StableInt($"{world.WorldSeed}:{world.CurrentDay}:{clientKey}:count", 100);
        var count = countRoll < 35 ? 0 : countRoll < 80 ? 1 : 2;
        if (count == 0)
            return false;

        var state = new CarriedInventoryState
        {
            CharacterKey = clientKey,
            OriginalClientIdentifier = clientKey,
            VisitDay = world.CurrentDay,
            OriginalIntent = "BUY"
        };
        var usedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var available = decisions.Where(x => !usedCategories.Contains(x.Category)).ToList();
            if (available.Count == 0)
                available = decisions;
            var decision = available[StableInt(
                $"{world.WorldSeed}:{world.CurrentDay}:{clientKey}:category:{i}",
                available.Count)];
            usedCategories.Add(decision.Category);

            var request = new ProcurementRequestState
            {
                RequestKey = $"carried:{world.CurrentDay}:{clientKey}:{i}",
                Category = decision.Category,
                RequestedDescription = decision.Category,
                PlayerRequestText = string.Empty,
                MaxRarityTier = decision.MaxRarityTier,
                DueDay = world.CurrentDay
            };
            var plan = NativeProcurementCatalog.Plan(request, world.WorldSeed);
            if (!plan.Available)
                continue;
            request.ResolvedItemKey = plan.ItemKey;
            var item = NativeProcurementCatalog.CreateItem(request);
            if (item is null || !NativeProcurementAdapter.TryAddToClientInventory(inventory, item))
            {
                try { item?.Destroy(); } catch { }
                continue;
            }
            state.RuntimeItemKeys.Add(item.uniqueId);
        }

        if (state.RuntimeItemKeys.Count == 0)
            return false;
        world.CarriedInventories.Add(state);
        MelonLogger.Msg(
            $"[当铺世界] 已生成买家临时随身物：{clientKey} / {state.RuntimeItemKeys.Count} 件，原来意仍为 BUY");
        return true;
    }

    public static CarriedInventoryApplication ApplyConversation(
        WorldState world,
        ConversationRequest request,
        ConversationResponse response)
    {
        var proposal = response.CarriedInventoryProposal;
        if (!proposal.Explicit)
            return Result(false, false, proposal.Reason);
        if (!proposal.Accepted)
            return Result(true, false, proposal.Reason);

        try
        {
            var playerStore = PlayerStore.Instance;
            var instance = playerStore?.currentClientInstance;
            var client = FormalGameAccess.GetClient(instance);
            var inventory = FormalGameAccess.GetInventory(instance);
            if (playerStore is null || client is null || inventory is null)
                return Result(true, false, "原版来客已经离开");

            var clientKey = FirstNotBlank(client.identifier, client.displayName);
            var expectedKey = FirstNotBlank(
                request.GameContext.Character.OriginalIdentifier,
                request.GameContext.Character.Key,
                request.CharacterKey);
            if (string.IsNullOrWhiteSpace(clientKey) ||
                !clientKey.Equals(expectedKey, StringComparison.OrdinalIgnoreCase))
                return Result(true, false, "回复到达时来客已经改变");

            var state = world.CarriedInventories.LastOrDefault(x =>
                x.VisitDay == world.CurrentDay &&
                x.OriginalClientIdentifier.Equals(clientKey, StringComparison.OrdinalIgnoreCase));
            var ownership = CounterOwnershipSnapshot.Capture(playerStore, instance);
            var candidates = new List<GameItem>();
            var carriedItems = FormalGameAccess.GetItems(inventory);
            for (var i = 0; i < carriedItems.Count; i++)
                AddUnique(candidates, carriedItems[i]);
            foreach (var item in ownership.ClientCounterItems)
                AddUnique(candidates, item);

            var proposalKey = proposal.ItemRuntimeKey.GetValueOrDefault();
            GameItem? first = proposalKey > 0
                ? candidates.FirstOrDefault(item => SafeRuntimeKey(item) == proposalKey)
                : null;
            if (first is null &&
                (!string.IsNullOrWhiteSpace(proposal.ItemName) ||
                 !string.IsNullOrWhiteSpace(proposal.ItemKey)))
            {
                var matches = candidates
                    .Where(item => MatchesProposal(item, proposal))
                    .ToList();
                if (matches.Count == 1)
                    first = matches[0];
            }

            if (first is null && state is not null)
            {
                var generated = candidates
                    .Where(item => state.RuntimeItemKeys.Contains(SafeRuntimeKey(item)))
                    .ToList();
                if (generated.Count == 1)
                    first = generated[0];
            }
            if (first is null && candidates.Count == 1)
                first = candidates[0];
            if (first is null)
                return Result(true, false, candidates.Count == 0
                    ? "当前顾客没有可验证的随身物品"
                    : "顾客随身物品无法唯一对应本次展示请求");

            var itemKey = first.uniqueId;
            if (ownership.ClientCounterIds.Contains(itemKey))
            {
                MelonLogger.Msg($"[当铺世界] 顾客随身物已经在顾客侧柜台：{clientKey} / {itemKey}");
                return Result(true, true, "顾客随身物已经摆在顾客侧柜台");
            }

            // Keep a visit-scoped restoration record for original customer
            // inventory too; previously only Mod-generated items had one.
            state ??= new CarriedInventoryState
            {
                CharacterKey = FirstNotBlank(request.CharacterKey, clientKey),
                OriginalClientIdentifier = clientKey,
                VisitDay = world.CurrentDay,
                OriginalIntent = client.clientIntent.ToString()
            };
            if (!world.CarriedInventories.Contains(state))
                world.CarriedInventories.Add(state);

            var originalIntent = client.clientIntent;
            var wasSellingUnlocked = state.SellingUnlocked;
            try
            {
                if (originalIntent == StoreClient.ClientIntent.BUY)
                    client.clientIntent = StoreClient.ClientIntent.SELLNBUY;
                playerStore.AddDirectSellingItemToTable(first, false, false, true, 0);
                state.SellingUnlocked = true;
            }
            catch
            {
                state.SellingUnlocked = wasSellingUnlocked;
                try { client.clientIntent = originalIntent; }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[当铺世界] 随身物摆放失败后无法恢复顾客来意：{ex.Message}");
                }
                throw;
            }

            try
            {
                playerStore.RefreshCounterItem();
                NegociationUIManager.Instance?.RefreshUI();
            }
            catch (Exception ex)
            {
                // The item is already on the counter; keep the valid trade state.
                MelonLogger.Warning($"[当铺世界] 随身物已摆出，但界面刷新失败：{ex.Message}");
            }

            var verified = CounterOwnershipSnapshot.Capture(playerStore, instance)
                .ClientCounterIds.Contains(itemKey);
            if (!verified)
            {
                MelonLogger.Warning($"[当铺世界] 随身物调用原版摆放后仍未在顾客侧柜台确认：{clientKey} / {itemKey}");
                return Result(true, false, "原版柜台没有确认这件随身物已经摆出");
            }
            MelonLogger.Msg($"[当铺世界] 顾客同意展示随身物：{clientKey} / {itemKey}");
            return Result(true, true, "已切换为原版买卖双向流程并摆出一件随身物");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[当铺世界] 展示临时随身物失败：{ex.Message}");
            return Result(true, false, "原版交易界面无法展示随身物");
        }
    }

    public static bool IsGeneratedItem(WorldState world, int runtimeKey) =>
        world.CarriedInventories.Any(x => x.RuntimeItemKeys.Contains(runtimeKey));

    public static bool RestoreBuyerIntent(WorldState world, StoreClient? client)
    {
        var clientKey = FirstNotBlank(client?.identifier, client?.displayName);
        var state = world.CarriedInventories.LastOrDefault(x =>
            x.VisitDay == world.CurrentDay &&
            x.OriginalClientIdentifier.Equals(clientKey, StringComparison.OrdinalIgnoreCase));
        if (state is null || !state.OriginalIntent.Equals("BUY", StringComparison.OrdinalIgnoreCase) || client is null)
            return false;
        try { client.clientIntent = StoreClient.ClientIntent.BUY; } catch { }
        return true;
    }

    private static int StableInt(string seed, int exclusiveMax)
    {
        if (exclusiveMax <= 1)
            return 0;
        var hash = RuntimeCompat.HashSha256(Encoding.UTF8.GetBytes(seed));
        var value = ((uint)hash[0] << 24) | ((uint)hash[1] << 16) | ((uint)hash[2] << 8) | hash[3];
        return (int)(value % (uint)exclusiveMax);
    }

    private static CarriedInventoryApplication Result(bool proposed, bool applied, string message) => new()
    {
        Proposed = proposed,
        Applied = applied,
        Message = message
    };

    private static string FirstNotBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static void AddUnique(List<GameItem> items, GameItem? candidate)
    {
        if (candidate is null)
            return;
        var key = SafeRuntimeKey(candidate);
        if (key > 0)
        {
            if (items.Any(item => SafeRuntimeKey(item) == key))
                return;
        }
        else if (items.Contains(candidate))
        {
            return;
        }
        items.Add(candidate);
    }

    private static bool MatchesProposal(GameItem item, CarriedInventoryProposal proposal)
    {
        var labels = new[]
        {
            Safe(() => item.GetDisplayName(true), string.Empty),
            item.name,
            item.identifier
        };
        return labels.Any(label => SameItemText(label, proposal.ItemName)) ||
               labels.Any(label => SameItemText(label, proposal.ItemKey));
    }

    private static bool SameItemText(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        var a = left.Trim();
        var b = right.Trim();
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
               a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
               b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private static int SafeRuntimeKey(GameItem? item)
    {
        try { return item?.uniqueId ?? 0; }
        catch { return 0; }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }
}
