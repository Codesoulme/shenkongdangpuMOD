using AIWorldPawnshop.Models;
using Il2Cpp;
using MelonLoader;

namespace AIWorldPawnshop.Runtime;

internal sealed class WalletTransferApplication
{
    public bool Proposed { get; init; }
    public bool Applied { get; init; }
    public bool Deferred { get; init; }
    public int Amount { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Executes an explicit customer-to-player budget transfer through the game's
/// own money methods. Mixed barter payments are held until native acceptance.
/// </summary>
internal static class NativeWalletAdapter
{
    private sealed class PendingTransfer
    {
        public string ClientKey { get; init; } = string.Empty;
        public int Amount { get; init; }
    }

    private static PendingTransfer? _pending;

    public static WalletTransferApplication ApplyConversation(
        ConversationRequest request,
        ConversationResponse response)
    {
        var proposal = response.WalletTransfer;
        if (!proposal.Explicit)
            return Result(false, false, 0, proposal.Reason);
        if (!proposal.Accepted)
            return Result(true, false, proposal.Amount, proposal.Reason);
        if (!request.Scene.Equals("store", StringComparison.OrdinalIgnoreCase))
            return Result(true, false, proposal.Amount, "电话场景不能进行店内预算转账");
        if (!proposal.Source.Equals("client_budget", StringComparison.OrdinalIgnoreCase))
            return Result(true, false, proposal.Amount, "转账来源不在 GM 白名单");
        if (proposal.Amount <= 0)
            return Result(true, false, proposal.Amount, "转账金额必须为正数");

        // A mixed barter is only a proposal at this point. Do not deduct the
        // budget or add cash until the player accepts the native offer.
        if (response.BarterProposal.Explicit && response.BarterProposal.Accepted)
            return QueuePending(request, proposal);

        return ApplyNow(request, proposal);
    }

    public static bool CommitPending(string clientKey)
    {
        var pending = _pending;
        if (pending is null ||
            !pending.ClientKey.Equals(clientKey, StringComparison.OrdinalIgnoreCase))
            return false;

        // Consume the reservation before applying it. A failed native callback
        // must not leave a second click able to charge the customer twice.
        _pending = null;
        try
        {
            var playerStore = PlayerStore.Instance;
            var instance = playerStore?.currentClientInstance;
            var client = instance?.GetClientBlueprint() ?? instance?.storeClient;
            var liveKey = FirstNotBlank(client?.identifier, client?.displayName);
            if (playerStore is null || client is null ||
                !liveKey.Equals(pending.ClientKey, StringComparison.OrdinalIgnoreCase))
            {
                MelonLogger.Warning("[当铺世界] 交换确认后无法确认顾客，补款未执行");
                return false;
            }

            if (!client.useClientBudget)
            {
                MelonLogger.Warning("[当铺世界] 交换确认后顾客预算已不可用，补款未执行");
                return false;
            }

            return ApplyAmount(playerStore, client, liveKey, pending.Amount).Applied;
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[当铺世界] 交换确认后补款失败：{ex.Message}");
            return false;
        }
    }

    public static bool CanCommitPending(string clientKey, int expectedAmount)
    {
        var pending = _pending;
        if (pending is null || expectedAmount <= 0 || pending.Amount != expectedAmount ||
            !pending.ClientKey.Equals(clientKey, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var playerStore = PlayerStore.Instance;
            var instance = playerStore?.currentClientInstance;
            var client = instance?.GetClientBlueprint() ?? instance?.storeClient;
            var liveKey = FirstNotBlank(client?.identifier, client?.displayName);
            return client is not null && client.useClientBudget &&
                   liveKey.Equals(clientKey, StringComparison.OrdinalIgnoreCase) &&
                   client.GetBudget() >= expectedAmount;
        }
        catch
        {
            return false;
        }
    }

    public static void CancelPending(string? clientKey = null)
    {
        if (_pending is null ||
            (clientKey is not null &&
             !_pending.ClientKey.Equals(clientKey, StringComparison.OrdinalIgnoreCase)))
            return;

        _pending = null;
    }

    private static WalletTransferApplication QueuePending(
        ConversationRequest request,
        WalletTransferProposal proposal)
    {
        try
        {
            var playerStore = PlayerStore.Instance;
            var instance = playerStore?.currentClientInstance;
            var client = instance?.GetClientBlueprint() ?? instance?.storeClient;
            if (playerStore is null || client is null)
                return Result(true, false, proposal.Amount, "原版来客已经离开");

            var expectedKey = FirstNotBlank(
                request.GameContext.Character.OriginalIdentifier,
                request.GameContext.Character.Key,
                request.CharacterKey);
            var liveKey = FirstNotBlank(client.identifier, client.displayName);
            if (!liveKey.Equals(expectedKey, StringComparison.OrdinalIgnoreCase))
                return Result(true, false, proposal.Amount, "回复到达时来客已经改变");
            if (!client.useClientBudget)
                return Result(true, false, proposal.Amount, "当前顾客没有启用原版预算");

            var currentBudget = client.GetBudget();
            if (proposal.Amount > currentBudget)
                return Result(true, false, proposal.Amount, $"顾客预算不足（当前 {currentBudget} 信用点）");

            if (_pending is not null)
            {
                if (!_pending.ClientKey.Equals(liveKey, StringComparison.OrdinalIgnoreCase))
                    return Result(true, false, proposal.Amount, "上一笔交换补款仍在等待确认");
                if (_pending.Amount != proposal.Amount)
                    return Result(true, false, proposal.Amount, "同一笔交换的补款金额发生变化");
            }
            else
            {
                _pending = new PendingTransfer
                {
                    ClientKey = liveKey,
                    Amount = proposal.Amount
                };
            }

            return Result(
                true,
                false,
                proposal.Amount,
                $"交换确认后补付 {proposal.Amount} 信用点",
                deferred: true);
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[当铺世界] 登记交换补款失败：{ex.Message}");
            return Result(true, false, proposal.Amount, "交换补款登记失败");
        }
    }

    private static WalletTransferApplication ApplyNow(
        ConversationRequest request,
        WalletTransferProposal proposal)
    {
        try
        {
            var playerStore = PlayerStore.Instance;
            var instance = playerStore?.currentClientInstance;
            var client = instance?.GetClientBlueprint() ?? instance?.storeClient;
            if (playerStore is null || client is null)
                return Result(true, false, proposal.Amount, "原版来客已经离开");

            var expectedKey = FirstNotBlank(
                request.GameContext.Character.OriginalIdentifier,
                request.GameContext.Character.Key,
                request.CharacterKey);
            var liveKey = FirstNotBlank(client.identifier, client.displayName);
            if (!liveKey.Equals(expectedKey, StringComparison.OrdinalIgnoreCase))
                return Result(true, false, proposal.Amount, "回复到达时来客已经改变");
            if (!client.useClientBudget)
                return Result(true, false, proposal.Amount, "当前顾客没有启用原版预算");

            return ApplyAmount(playerStore, client, liveKey, proposal.Amount);
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[当铺世界] 原版预算转账失败：{ex.Message}");
            return Result(true, false, proposal.Amount, "原版预算转账失败");
        }
    }

    private static WalletTransferApplication ApplyAmount(
        PlayerStore playerStore,
        StoreClient client,
        string liveKey,
        int amount)
    {
        var beforeBudget = client.GetBudget();
        var beforeCash = playerStore.GetCash();
        if (amount > beforeBudget)
            return Result(true, false, amount, $"顾客预算不足（当前 {beforeBudget} 信用点）");

        client.ModBudget(-amount);
        var afterBudget = client.GetBudget();
        if (afterBudget != beforeBudget - amount)
        {
            client.ModBudget(beforeBudget - afterBudget);
            return Result(true, false, amount, "顾客预算扣除校验失败");
        }

        playerStore.ModCash(amount, false);
        var afterCash = playerStore.GetCash();
        if (afterCash != beforeCash + amount)
        {
            playerStore.ModCash(beforeCash - afterCash, false);
            client.ModBudget(amount);
            return Result(true, false, amount, "玩家现金增加校验失败，转账已回滚");
        }

        var message = $"已从顾客预算扣除 {amount} 信用点并加入玩家现金";
        MelonLogger.Msg(
            $"[当铺世界] {message}：{liveKey}，预算 {beforeBudget}->{afterBudget}，现金 {beforeCash}->{afterCash}");
        return Result(true, true, amount, message);
    }

    private static WalletTransferApplication Result(
        bool proposed,
        bool applied,
        int amount,
        string? message,
        bool deferred = false) => new()
        {
            Proposed = proposed,
            Applied = applied,
            Deferred = deferred,
            Amount = amount,
            Message = string.IsNullOrWhiteSpace(message) ? "未提供转账理由" : message
        };

    private static string FirstNotBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
