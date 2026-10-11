using AIWorldPawnshop.Models;
using Il2Cpp;
using MelonLoader;

namespace AIWorldPawnshop.Runtime;

internal sealed class ReturnVisitApplication
{
    public bool Proposed { get; init; }
    public bool Scheduled { get; init; }
    public string Message { get; init; } = string.Empty;
}

internal static class NativeReturnVisitAdapter
{
    public static ReturnVisitApplication ApplyConversation(
        WorldState world,
        ConversationRequest request,
        ConversationResponse response)
    {
        var proposal = response.ReturnVisitProposal;
        if (!proposal.Explicit)
            return Result(false, false, proposal.Reason);
        if (!proposal.Accepted)
            return Result(true, false, proposal.Reason);

        try
        {
            var playerStore = PlayerStore.Instance;
            var instance = playerStore?.currentClientInstance;
            var client = instance?.GetClientBlueprint() ?? instance?.storeClient;
            if (playerStore is null || client is null)
                return Result(true, false, "原版来客已经离开");

            var liveKey = FirstNotBlank(client.identifier, client.displayName);
            var expectedKey = FirstNotBlank(
                request.GameContext.Character.OriginalIdentifier,
                request.GameContext.Character.Key,
                request.CharacterKey);
            if (string.IsNullOrWhiteSpace(liveKey) ||
                !liveKey.Equals(expectedKey, StringComparison.OrdinalIgnoreCase))
                return Result(true, false, "回复到达时来客已经改变");

            var delay = Math.Clamp(proposal.DueInDays, 1, 3);
            var dueDay = world.CurrentDay + delay;
            var existing = world.ReturnVisits.FirstOrDefault(x =>
                x.OriginalClientIdentifier.Equals(liveKey, StringComparison.OrdinalIgnoreCase) &&
                x.DueDay == dueDay &&
                IsOpen(x.Status));
            if (existing is not null)
                return Result(true, true, "该顾客同一天的回访已经登记");

            QueueIfMissing(playerStore, liveKey, delay);
            world.ReturnVisits.Add(new ReturnVisitState
            {
                CharacterKey = FirstNotBlank(request.GameContext.Character.Key, request.CharacterKey, liveKey),
                CharacterDisplayName = FirstNotBlank(
                    request.GameContext.Character.DisplayName,
                    request.CharacterDisplayName,
                    client.realName,
                    client.displayName,
                    liveKey),
                OriginalClientIdentifier = liveKey,
                CreatedDay = world.CurrentDay,
                DueDay = dueDay,
                QueueDelayDays = delay,
                PlayerInvitation = request.PlayerText,
                Status = "scheduled",
                OutcomeSummary = $"已接受邀请，计划第 {dueDay} 天按原版来客规则回访。"
            });
            world.Facts.Add(new FactRecord
            {
                Day = world.CurrentDay,
                Kind = "return_visit_scheduled",
                Summary = $"{request.CharacterDisplayName}答应第 {dueDay} 天再来",
                Visibility = "actor"
            });
            MelonLogger.Msg($"[当铺世界] 已登记软回访：{liveKey} / 第 {dueDay} 天");
            return Result(true, true, "已加入原版未来来客队列，不替换当天来客");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[当铺世界] 登记回访失败：{ex.Message}");
            return Result(true, false, "原版未来来客队列无法登记");
        }
    }

    public static bool RecordArrival(WorldState world, StoreClient? client)
    {
        var key = FirstNotBlank(client?.identifier, client?.displayName);
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var visit = world.ReturnVisits
            .Where(x => IsOpen(x.Status) &&
                        x.DueDay <= world.CurrentDay &&
                        x.OriginalClientIdentifier.Equals(key, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.DueDay)
            .FirstOrDefault();
        if (visit is null)
            return false;

        visit.Status = "arrived";
        visit.ReturnedDay = world.CurrentDay;
        visit.OutcomeSummary = $"顾客已在第 {world.CurrentDay} 天真实到店。";
        world.Facts.Add(new FactRecord
        {
            Day = world.CurrentDay,
            Kind = "return_visit_arrived",
            Summary = $"{visit.CharacterDisplayName}按约回到当铺",
            Visibility = "actor"
        });
        MelonLogger.Msg($"[当铺世界] 软回访已到场：{visit.CharacterDisplayName}");
        return true;
    }

    public static bool DeferMissedVisits(WorldState world)
    {
        var changed = false;
        foreach (var visit in world.ReturnVisits.Where(x => IsOpen(x.Status) && x.DueDay < world.CurrentDay))
        {
            if (visit.DeferralCount >= 1)
            {
                visit.Status = "missed";
                visit.OutcomeSummary = "原版来客容量连续两次未安排到店，本次邀请结束。";
                changed = true;
                continue;
            }

            var playerStore = PlayerStore.Instance;
            if (playerStore is null)
                continue;
            QueueIfMissing(playerStore, visit.OriginalClientIdentifier, 1);
            visit.DeferralCount++;
            visit.QueueDelayDays = 1;
            visit.DueDay = world.CurrentDay + 1;
            visit.Status = "deferred";
            visit.OutcomeSummary = $"当天来客容量未安排到店，已按原版队列顺延到第 {visit.DueDay} 天。";
            changed = true;
        }
        return changed;
    }

    public static bool QueueIfMissing(PlayerStore playerStore, string clientKey, int delayDays)
    {
        var ids = playerStore.futurStoreClientIdQueue;
        var days = playerStore.futurStoreClientDayQueue;
        if (ids is not null)
        {
            for (var i = 0; i < ids.Count; i++)
            {
                if (!string.Equals(ids[i], clientKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (days is null || i >= days.Count || days[i] == delayDays)
                    return false;
            }
        }

        playerStore.QueueFuturClient(clientKey, delayDays);
        return true;
    }

    private static bool IsOpen(string status) =>
        status.Equals("scheduled", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("deferred", StringComparison.OrdinalIgnoreCase);

    private static ReturnVisitApplication Result(bool proposed, bool scheduled, string message) => new()
    {
        Proposed = proposed,
        Scheduled = scheduled,
        Message = message
    };

    private static string FirstNotBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
