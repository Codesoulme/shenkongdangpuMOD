using AIWorldPawnshop.Models;
using Il2Cpp;
using MelonLoader;

namespace AIWorldPawnshop.Runtime;

internal sealed class WalletTransferApplication
{
    public bool Proposed { get; init; }
    public bool Applied { get; init; }
    public int Amount { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Executes an explicit customer-to-player budget transfer through the game's
/// own money methods. The adapter never creates money or edits private fields.
/// </summary>
internal static class NativeWalletAdapter
{
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

        return ApplyNow(request, proposal);
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

            var beforeBudget = client.GetBudget();
            var beforeCash = playerStore.GetCash();
            if (proposal.Amount > beforeBudget)
                return Result(true, false, proposal.Amount, $"顾客预算不足（当前 {beforeBudget} 信用点）");

            client.ModBudget(-proposal.Amount);
            var afterBudget = client.GetBudget();
            if (afterBudget != beforeBudget - proposal.Amount)
            {
                client.ModBudget(beforeBudget - afterBudget);
                return Result(true, false, proposal.Amount, "顾客预算扣除校验失败");
            }

            playerStore.ModCash(proposal.Amount, false);
            var afterCash = playerStore.GetCash();
            if (afterCash != beforeCash + proposal.Amount)
            {
                playerStore.ModCash(beforeCash - afterCash, false);
                client.ModBudget(proposal.Amount);
                return Result(true, false, proposal.Amount, "玩家现金增加校验失败，转账已回滚");
            }

            var message = $"已从顾客预算扣除 {proposal.Amount} 信用点并加入玩家现金";
            MelonLogger.Msg($"[当铺世界] {message}：{liveKey}，预算 {beforeBudget}->{afterBudget}，现金 {beforeCash}->{afterCash}");
            return Result(true, true, proposal.Amount, message);
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[当铺世界] 原版预算转账失败：{ex.Message}");
            return Result(true, false, proposal.Amount, "原版预算转账失败");
        }
    }

    private static WalletTransferApplication Result(
        bool proposed,
        bool applied,
        int amount,
        string? message
        ) => new()
        {
            Proposed = proposed,
            Applied = applied,
            Amount = amount,
            Message = string.IsNullOrWhiteSpace(message) ? "未提供转账理由" : message
        };

    private static string FirstNotBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
