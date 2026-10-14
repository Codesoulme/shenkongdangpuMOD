using AIWorldPawnshop.Models;
using Il2Cpp;

namespace AIWorldPawnshop.Runtime;

internal static class ConversationActionResolver
{
    internal static string NaturalBarterFailureReply => "抱歉，这次交换我没法确认，还是算了。";

    public static void Normalize(ConversationRequest request, ConversationResponse response)
    {
        if (IsRefusalReply(response.NpcReply))
        {
            response.TradeProposal = new TradeProposal();
            response.ProcurementProposal = new ProcurementProposal();
            response.ReturnVisitProposal = new ReturnVisitProposal();
            response.CarriedInventoryProposal = new CarriedInventoryProposal();
            response.BarterProposal = new BarterProposal();
            response.WalletTransfer = new WalletTransferProposal();
            response.ThirdPartyTask = new ThirdPartyTaskProposal();
            response.WorldModifications = new List<WorldModificationProposal>();
            return;
        }

        ResolveReturnVisit(request, response);
        ResolveCarriedInventory(request, response);
        if (response.BarterProposal.Explicit ||
            LooksLikeBarterIntent(request.PlayerText))
            ResolveBarter(request, response);
        ResolveWalletTransfer(request, response);
        ResolveBarterValue(request, response);
        NormalizeWholesaleReply(request, response);
    }

    public static bool ShouldDismissStoreClient(ConversationRequest request, ConversationResponse response)
    {
        if (!request.Scene.Equals("store", StringComparison.OrdinalIgnoreCase))
            return false;
        return ContainsDeparture(
                   response.NpcReply,
            "我走了", "我先走", "我得走", "我要走", "先走了", "走了", "离开", "离店", "不买了", "不谈了", "不聊了", "告辞", "再见", "下次再来",
            "没什么好谈", "不想再谈", "到此为止", "滚开", "滚出去", "请出去", "别烦") ||
               (response.Signals is not null &&
                (response.Signals.Offense >= 2 || response.Signals.Threat >= 2) &&
                ContainsAny(response.NpcReply, "不想谈", "别再", "别来", "结束"));
    }

    public static bool ShouldHangUpPhone(ConversationRequest request, ConversationResponse response)
    {
        if (!request.Scene.Equals("phone", StringComparison.OrdinalIgnoreCase))
            return false;
        return ContainsDeparture(response.NpcReply, "挂了", "先挂", "结束通话", "通话结束", "下次再聊", "不打扰了", "再见", "拜拜");
    }

    public static void EnsureNaturalExitReply(
        ConversationRequest request,
        ConversationResponse response)
    {
        var isStoreExit = ShouldDismissStoreClient(request, response);
        var isPhoneExit = ShouldHangUpPhone(request, response);
        if (!isStoreExit && !isPhoneExit)
            return;

        var reply = (response.NpcReply ?? string.Empty).Trim();
        var closing = isPhoneExit
            ? "那我先挂了，下次再聊。"
            : "那我先走了，回头再来。";
        if (string.IsNullOrWhiteSpace(reply))
        {
            response.NpcReply = closing;
            return;
        }

        if (!ContainsAny(reply,
                "再见", "回头", "下次", "先走", "告辞", "拜拜", "挂了", "结束通话", "不打扰了"))
        {
            response.NpcReply = $"{reply.TrimEnd('。', '！', '!', '？', '?')}。{closing}";
        }
    }

    private static void ResolveReturnVisit(ConversationRequest request, ConversationResponse response)
    {
        var proposal = response.ReturnVisitProposal;
        if (response.ProcurementProposal.Explicit)
        {
            proposal.Explicit = false;
            proposal.Accepted = false;
            proposal.Reason = "找货委托已包含未来到店，不重复登记普通回访";
            return;
        }
        proposal.DueInDays = Math.Clamp(proposal.DueInDays <= 0 ? ParseDueDays(request.PlayerText) : proposal.DueInDays, 1, 3);
        if (!request.Scene.Equals("store", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(request.GameContext.Character.OriginalIdentifier))
            return;

        var playerText = request.PlayerText ?? string.Empty;
        var reply = response.NpcReply ?? string.Empty;
        var future = ContainsAny(playerText, "明天", "后天", "下次", "过几天", "改天");
        var visit = ContainsAny(playerText, "再来", "回来", "来找我", "来店", "到店", "来一趟");
        var procurement = ContainsAny(playerText, "找", "寻找", "带来", "带点", "弄到") &&
                          ContainsAny(playerText, "货", "东西", "物品", "材料", "水", "食物", "药", "武器", "工具", "模块", "门禁");
        var explicitVisit = future && visit && !procurement;
        if (!proposal.Explicit && explicitVisit)
            proposal.Explicit = true;
        if (proposal.Explicit && !proposal.Accepted && IsAcceptedReply(reply))
            proposal.Accepted = true;
        if (proposal.Explicit && string.IsNullOrWhiteSpace(proposal.Reason))
            proposal.Reason = proposal.Accepted ? "人物明确接受未来回访邀请" : "人物没有明确接受回访";
    }

    private static void ResolveCarriedInventory(ConversationRequest request, ConversationResponse response)
    {
        var proposal = response.CarriedInventoryProposal;
        if (!request.Scene.Equals("store", StringComparison.OrdinalIgnoreCase))
            return;

        var playerText = request.PlayerText ?? string.Empty;
        var reply = response.NpcReply ?? string.Empty;
        var asksPossessions = ContainsAny(playerText, "身上", "随身", "带着", "带的", "还有什么", "别的东西") &&
                              ContainsAny(playerText, "卖", "拿出来", "看看", "交易", "收");
        if (!proposal.Explicit && asksPossessions)
            proposal.Explicit = true;
        if (proposal.Explicit && !proposal.Accepted && IsAcceptedReply(reply))
            proposal.Accepted = true;
        if (proposal.Accepted)
        {
            var candidates = request.GameContext.Trade.ClientCarriedItems
                .Where(IsConfirmedCarriedItem)
                .GroupBy(item => item.RuntimeKey)
                .Select(group => group.First())
                .ToList();
            if (candidates.Count == 0)
            {
                proposal.Accepted = false;
                proposal.Reason = "当前原版上下文没有可验证的顾客随身物品";
            }
            else
            {
                var selected = SelectCarriedItem(candidates, proposal);
                if (selected is null)
                {
                    proposal.Accepted = false;
                    proposal.Reason = candidates.Count > 1
                        ? "顾客有多件随身物品，但没有唯一指定要展示哪一件"
                        : "模型指定的随身物品不在当前顾客库存中";
                }
                else
                {
                    proposal.ItemRuntimeKey = selected.RuntimeKey;
                    proposal.ItemKey = selected.ItemKey;
                    proposal.ItemName = selected.DisplayName;
                }
            }
        }
        if (proposal.Explicit && string.IsNullOrWhiteSpace(proposal.Reason))
            proposal.Reason = proposal.Accepted ? "人物同意展示随身货物" : "人物没有同意出售随身货物";
    }

    private static bool IsConfirmedCarriedItem(NativeItemContext item) =>
        item.RuntimeKey > 0 &&
        string.Equals(item.Owner, "client", StringComparison.OrdinalIgnoreCase);

    private static NativeItemContext? SelectCarriedItem(
        IReadOnlyList<NativeItemContext> candidates,
        CarriedInventoryProposal proposal)
    {
        if (proposal.ItemRuntimeKey is > 0)
            return candidates.SingleOrDefault(item => item.RuntimeKey == proposal.ItemRuntimeKey.Value);

        var hasName = !string.IsNullOrWhiteSpace(proposal.ItemName);
        var hasKey = !string.IsNullOrWhiteSpace(proposal.ItemKey);
        if (hasName || hasKey)
        {
            var matches = candidates
                .Where(item =>
                    (hasName && SameItemText(item.DisplayName, proposal.ItemName)) ||
                    (hasKey && (SameItemText(item.ItemKey, proposal.ItemKey) ||
                                SameItemText(item.Identifier, proposal.ItemKey))))
                .ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        return candidates.Count == 1 ? candidates[0] : null;
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

    private static bool LooksLikeBarterIntent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return ContainsAny(text, "交换", "以物易物", "换你", "换这个", "换那个", "跟你换", "给你换") ||
               (text.Contains("换", StringComparison.Ordinal) &&
                ContainsAny(text, "拿", "用", "跟你", "给你"));
    }

    private static void ResolveBarter(ConversationRequest request, ConversationResponse response)
    {
        var proposal = response.BarterProposal;
        if (!request.Scene.Equals("store", StringComparison.OrdinalIgnoreCase))
            return;

        var playerText = request.PlayerText ?? string.Empty;
        var reply = response.NpcReply ?? string.Empty;
        var barterIntent = proposal.Explicit || LooksLikeBarterIntent(playerText);
        if (!barterIntent)
            return;
        if (!proposal.Explicit)
            proposal.Explicit = true;
        if (proposal.Explicit && !proposal.Accepted && IsAcceptedReply(reply))
            proposal.Accepted = true;

        CounterOwnershipSnapshot ownership;
        try
        {
            var playerStore = PlayerStore.Instance;
            ownership = CounterOwnershipSnapshot.Capture(
                playerStore,
                playerStore?.currentClientInstance);
        }
        catch
        {
            // Normalization can run during a native scene transition.  An
            // empty snapshot keeps automatic item selection fail-closed; the
            // live barter adapter will revalidate explicit keys later.
            ownership = new CounterOwnershipSnapshot();
        }
        var carried = request.GameContext.Trade.ClientCarriedItems
            .Where(item => IsConfirmedClientItem(item, ownership))
            .ToList();
        if (proposal.ClientItemRuntimeKey is null && carried.Count == 1)
        {
            proposal.ClientItemRuntimeKey = carried[0].RuntimeKey;
            proposal.ClientItemName = carried[0].DisplayName;
        }

        var currentItem = request.GameContext.Trade.Item;
        var offeredPlayerItems = request.GameContext.Trade.PlayerOfferedItems
            .Where(item => IsConfirmedPlayerItem(item, ownership))
            .ToList();
        if (string.IsNullOrWhiteSpace(proposal.RequiredPlayerItemKey) &&
            string.IsNullOrWhiteSpace(proposal.RequiredPlayerItemName) &&
            proposal.RequiredPlayerItemRuntimeKey is null &&
            proposal.RequiredPlayerItemRuntimeKeys.Count == 0 &&
            offeredPlayerItems.Count == 1)
        {
            proposal.RequiredPlayerItemRuntimeKey = offeredPlayerItems[0].RuntimeKey > 0
                ? offeredPlayerItems[0].RuntimeKey
                : null;
            proposal.RequiredPlayerItemKey = offeredPlayerItems[0].ItemKey;
            proposal.RequiredPlayerItemName = offeredPlayerItems[0].DisplayName;
        }

        if (currentItem is not null &&
            IsConfirmedPlayerItem(currentItem, ownership))
        {
            if (string.IsNullOrWhiteSpace(proposal.RequiredPlayerItemKey))
                proposal.RequiredPlayerItemKey = currentItem.ItemKey;
            if (string.IsNullOrWhiteSpace(proposal.RequiredPlayerItemName))
                proposal.RequiredPlayerItemName = currentItem.DisplayName;
            if (proposal.RequiredPlayerItemRuntimeKey is null &&
                proposal.RequiredPlayerItemRuntimeKeys.Count == 0 &&
                currentItem.RuntimeKey > 0)
                proposal.RequiredPlayerItemRuntimeKey = currentItem.RuntimeKey;
        }

        if (proposal.RequiredPlayerItemKeys.Count == 0 && !string.IsNullOrWhiteSpace(proposal.RequiredPlayerItemKey))
            proposal.RequiredPlayerItemKeys.Add(proposal.RequiredPlayerItemKey);
        if (proposal.RequiredPlayerItemNames.Count == 0 && !string.IsNullOrWhiteSpace(proposal.RequiredPlayerItemName))
            proposal.RequiredPlayerItemNames.Add(proposal.RequiredPlayerItemName);

        if (proposal.Explicit && string.IsNullOrWhiteSpace(proposal.Reason))
            proposal.Reason = proposal.Accepted ? "人物同意按指定物品交换" : "人物没有同意交换";
    }

    private static bool IsConfirmedClientItem(
        NativeItemContext item,
        CounterOwnershipSnapshot ownership) =>
        string.Equals(item.Owner, "client", StringComparison.OrdinalIgnoreCase) &&
        ownership.IsClientRuntimeKey(item.RuntimeKey);

    private static bool IsConfirmedPlayerItem(
        NativeItemContext item,
        CounterOwnershipSnapshot ownership) =>
        string.Equals(item.Owner, "player", StringComparison.OrdinalIgnoreCase) &&
        ownership.IsPlayerRuntimeKey(item.RuntimeKey);

    private static void ResolveWalletTransfer(ConversationRequest request, ConversationResponse response)
    {
        var proposal = response.WalletTransfer;
        if (!request.Scene.Equals("store", StringComparison.OrdinalIgnoreCase))
        {
            response.WalletTransfer = new WalletTransferProposal();
            return;
        }

        proposal.Source = string.IsNullOrWhiteSpace(proposal.Source)
            ? "client_budget"
            : proposal.Source.Trim().ToLowerInvariant();
        if (!proposal.Source.Equals("client_budget", StringComparison.OrdinalIgnoreCase) || proposal.Amount <= 0)
        {
            proposal.Explicit = false;
            proposal.Accepted = false;
            return;
        }

        var playerText = request.PlayerText ?? string.Empty;
        var reply = response.NpcReply ?? string.Empty;
        var mentionsTransfer = ContainsAny(playerText, "补", "差", "转给我", "给我", "付我", "信用点") &&
                               (playerText.Any(char.IsDigit) || ContainsAny(playerText, "一", "两", "二", "三", "四", "五"));
        if (!proposal.Explicit && mentionsTransfer && IsAcceptedReply(reply))
        {
            proposal.Explicit = true;
            proposal.Accepted = true;
        }
        if (proposal.Explicit && string.IsNullOrWhiteSpace(proposal.Reason))
            proposal.Reason = proposal.Accepted ? "人物明确同意从预算补付信用点" : "人物没有明确同意补付信用点";
    }

    private static void ResolveBarterValue(
        ConversationRequest request,
        ConversationResponse response)
    {
        var proposal = response.BarterProposal;
        if (!proposal.Explicit || !proposal.Accepted)
            return;

        var check = BarterValuePolicy.Evaluate(request, response);
        if (!check.Allowed)
        {
            proposal.Accepted = false;
            proposal.Reason = check.Message;
            if (response.WalletTransfer.Explicit && response.WalletTransfer.Accepted)
                response.WalletTransfer = new WalletTransferProposal();
            response.NpcReply = "这笔交换的价值我接受不了，换个条件再谈。";
            return;
        }

        proposal.ValueAssessment.ClientTotalValue = check.ClientTotalValue;
        proposal.ValueAssessment.PlayerTotalValue = check.PlayerTotalValue;
        if (string.IsNullOrWhiteSpace(proposal.ValueAssessment.Reason))
            proposal.ValueAssessment.Reason = check.Message;
    }

    private static void NormalizeWholesaleReply(
        ConversationRequest request,
        ConversationResponse response)
    {
        var trade = request.GameContext.Trade;
        if (!trade.IsOfferWholesale ||
            string.IsNullOrWhiteSpace(response.NpcReply))
            return;

        var expectedDiscount = Math.Clamp(trade.WholesaleDiscountPercent, 0, 100);
        var expectedFold = 100 - expectedDiscount;
        var reply = response.NpcReply;
        var ratioMismatch =
            (TryReadFoldPercent(reply, out var fold) && fold != expectedFold) ||
            (TryReadPercent(reply, out var percent) && percent != expectedDiscount);
        var amountMismatch =
            trade.FinalOfferValue is > 0 &&
            TryReadCreditAmount(reply, out var amount) &&
            amount != trade.FinalOfferValue.Value;
        if (ratioMismatch || amountMismatch)
        {
            response.NpcReply = "按当前收银台的原版批发报价算，具体金额以收银台显示为准。";
        }
    }

    private static bool TryReadCreditAmount(string value, out long amount)
    {
        amount = 0;
        var marker = value.IndexOf("信用点", StringComparison.Ordinal);
        if (marker < 0)
            marker = value.IndexOf("点", StringComparison.Ordinal);
        if (marker <= 0)
            return false;

        var end = marker;
        var start = end - 1;
        while (start >= 0 && char.IsDigit(value[start]))
            start--;
        return start < end - 1 &&
               long.TryParse(value[(start + 1)..end], out amount);
    }

    private static bool TryReadPercent(string value, out int percent)
    {
        percent = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%')
                continue;
            var end = i;
            var start = end - 1;
            while (start >= 0 && char.IsDigit(value[start]))
                start--;
            if (start == end - 1)
                continue;
            if (int.TryParse(value[(start + 1)..end], out var parsed))
            {
                percent = parsed;
                return true;
            }
        }
        return false;
    }

    private static bool TryReadFoldPercent(string value, out int fold)
    {
        fold = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '折')
                continue;

            var end = i;
            var start = end - 1;
            while (start >= 0 && (char.IsDigit(value[start]) || IsChineseDigit(value[start])))
                start--;
            if (start == end - 1)
                continue;

            var token = value[(start + 1)..end];
            if (int.TryParse(token, out var parsed))
            {
                fold = parsed <= 10 ? parsed * 10 : parsed;
                return true;
            }

            var digits = token.Select(ToDigit).ToArray();
            if (digits.All(x => x >= 0))
            {
                fold = digits.Length == 1 ? digits[0] * 10 : digits[^2] * 10 + digits[^1];
                return true;
            }
        }
        return false;
    }

    private static bool IsChineseDigit(char value) =>
        value is '零' or '〇' or '一' or '二' or '两' or '三' or '四' or '五' or '六' or '七' or '八' or '九';

    private static int ToDigit(char value) => value switch
    {
        '零' or '〇' => 0,
        '一' => 1,
        '二' or '两' => 2,
        '三' => 3,
        '四' => 4,
        '五' => 5,
        '六' => 6,
        '七' => 7,
        '八' => 8,
        '九' => 9,
        _ => -1
    };

    private static bool IsAcceptedReply(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsRefusalReply(value))
            return false;

        var reply = value.TrimStart();
        var openingAgreement = new[]
        {
            "好的", "好吧", "好啊", "好，", "好。", "行吧", "行啊", "行，", "行。"
        }.Any(prefix => reply.StartsWith(prefix, StringComparison.Ordinal));
        return reply is "好" or "行" ||
               openingAgreement ||
               ContainsAny(reply, "可以", "答应", "没问题", "成交", "就这么办", "拿出来", "给你看");
    }

    private static bool IsRefusalReply(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.TrimStart().StartsWith("不好", StringComparison.Ordinal) ||
         ContainsAny(value,
             "不行", "不能", "不可以", "不太行", "不卖", "不换", "不答应",
             "不想来", "不愿来", "不想卖", "不愿卖", "不想换", "不愿换",
             "不想给", "不愿给", "不想付", "不愿付", "不要换", "不要卖",
             "行不通", "没法", "没办法", "办不到", "做不到", "无法",
             "答应不了", "可以考虑", "考虑一下", "再考虑", "拒绝", "别想",
             "没门", "算了", "免了", "恐怕"));

    private static bool ContainsDeparture(string value, params string[] terms)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            ContainsAny(value, "我不走", "我不想走", "我不离开", "我不想离开", "别走", "不要挂", "不想挂", "别挂", "先别结束", "不要结束"))
            return false;
        return ContainsAny(value, terms);
    }

    private static int ParseDueDays(string value)
    {
        if (value.Contains("后天", StringComparison.Ordinal))
            return 2;
        if (value.Contains("三天", StringComparison.Ordinal) || value.Contains("3天", StringComparison.Ordinal))
            return 3;
        return 1;
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
