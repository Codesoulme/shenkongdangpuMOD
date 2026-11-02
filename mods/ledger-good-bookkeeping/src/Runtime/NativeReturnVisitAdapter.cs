using AIWorldPawnshop.Models;
using AIWorldPawnshop.Compatibility;
using MelonLoader;

namespace AIWorldPawnshop.Runtime;

internal sealed class ReturnVisitApplication
{
    public bool Proposed { get; init; }
    public bool Scheduled { get; init; }
    public string Message { get; init; } = string.Empty;
}

internal sealed record NativeQueueResult(
    bool Scheduled,
    bool AlreadyQueued,
    bool Conflict,
    int? ExistingDay,
    string Message);

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
            var client = FormalGameAccess.GetClient(instance);
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

            var queue = TryQueue(playerStore, liveKey, delay);
            if (!queue.Scheduled && !queue.AlreadyQueued)
                return Result(true, false, queue.Message);
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

    public static bool RecordThirdPartyArrival(WorldState world, StoreClient? client)
    {
        var nativeKey = FirstNotBlank(client?.identifier, client?.displayName);
        if (string.IsNullOrWhiteSpace(nativeKey))
            return false;

        var task = world.ThirdPartyTasks
            .Where(x => (x.Status.Equals("accepted", StringComparison.OrdinalIgnoreCase) ||
                        x.Status.Equals("proposed", StringComparison.OrdinalIgnoreCase)) &&
                        x.DueDay <= world.CurrentDay &&
                        x.TargetNativeIdentifier.Equals(nativeKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.DueDay)
            .FirstOrDefault();
        if (task is null)
            return false;

        task.Status = "arrived";
        task.ArrivedDay = world.CurrentDay;
        task.OutcomeSummary = $"目标人物已在第 {world.CurrentDay} 天按联系安排到店，等待其本人决定。";
        world.Facts.Add(new FactRecord
        {
            Day = world.CurrentDay,
            Kind = "third_party_arrived",
            Summary = $"{task.TargetDisplayName}已按联系安排到店",
            Visibility = "actor"
        });
        MelonLogger.Msg($"[当铺世界] 第三方联系目标已到场：{task.TargetDisplayName}");
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
            var queue = TryQueue(playerStore, visit.OriginalClientIdentifier, 1);
            if (!queue.Scheduled && !queue.AlreadyQueued)
            {
                visit.OutcomeSummary =
                    $"原版来客队列暂未确认顺延：{queue.Message}";
                changed = true;
                continue;
            }
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
        return TryQueue(playerStore, clientKey, delayDays).Scheduled;
    }

    public static NativeQueueResult TryQueue(PlayerStore playerStore, string clientKey, int delayDays)
    {
        if (string.IsNullOrWhiteSpace(clientKey))
            return new(false, false, true, null, "原版来客标识为空");

        var ids = playerStore.futurStoreClientIdQueue;
        var days = playerStore.futurStoreClientDayQueue;
        if (ids is null || days is null || ids.Count != days.Count)
            return new(false, false, true, null, "原版来客队列不可安全读写");

        for (var i = 0; i < ids.Count; i++)
        {
            if (!string.Equals(ids[i], clientKey, StringComparison.OrdinalIgnoreCase))
                continue;

            if (days[i] == delayDays)
                return new(false, true, false, days[i], "原版队列已登记该人物和日期");

            return new(false, false, true, days[i],
                $"原版队列已有该人物，但日期为第 {days[i]} 天");
        }

        playerStore.QueueFuturClient(clientKey, delayDays);
        ids = playerStore.futurStoreClientIdQueue;
        days = playerStore.futurStoreClientDayQueue;
        if (ids is null || days is null || ids.Count != days.Count)
            return new(false, false, true, null, "原版来客队列写入后无法校验");
        for (var i = 0; i < ids.Count; i++)
        {
            if (string.Equals(ids[i], clientKey, StringComparison.OrdinalIgnoreCase) &&
                days[i] == delayDays)
                return new(true, false, false, delayDays, "已写入并校验原版未来来客队列");
        }

        return new(false, false, true, null, "原版来客队列没有确认本次写入");
    }

    public static bool TryRemoveQueuedClient(
        PlayerStore playerStore,
        string clientKey,
        int expectedDay)
    {
        var ids = playerStore.futurStoreClientIdQueue;
        var days = playerStore.futurStoreClientDayQueue;
        if (ids is null || days is null)
            return false;

        for (var i = ids.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(ids[i], clientKey, StringComparison.OrdinalIgnoreCase) ||
                i >= days.Count || days[i] != expectedDay)
                continue;

            ids.RemoveAt(i);
            days.RemoveAt(i);
            return true;
        }

        return false;
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
