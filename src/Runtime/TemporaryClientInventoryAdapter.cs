using System.Security.Cryptography;
using System.Text;
using AIWorldPawnshop.Models;
using Il2Cpp;
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
        if (client is null || instance?.gridInv is null ||
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
            if (item is null || !NativeProcurementAdapter.TryAddToClientInventory(instance.gridInv, item))
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
            var client = instance?.GetClientBlueprint() ?? instance?.storeClient;
            if (playerStore is null || client is null || instance?.gridInv?.items is null)
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
            if (state is null || state.RuntimeItemKeys.Count == 0)
                return Result(true, false, "这名买家本次没有可展示的临时随身物");

            GameItem? first = null;
            for (var i = 0; i < instance.gridInv.items.Count; i++)
            {
                var candidate = instance.gridInv.items[i];
                if (candidate is not null && state.RuntimeItemKeys.Contains(candidate.uniqueId))
                {
                    first = candidate;
                    break;
                }
            }
            if (first is null)
                return Result(true, false, "随身物已经不在顾客物品栏");

            var itemKey = first.uniqueId;
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
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
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
}
