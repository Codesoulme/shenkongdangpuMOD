using AIWorldPawnshop.Models;
using Il2Cpp;
using MelonLoader;

namespace AIWorldPawnshop.Runtime;

public sealed class NativeTradeApplication
{
    public bool Proposed { get; init; }
    public bool Invoked { get; init; }
    public bool Accepted { get; init; }
    public string Kind { get; init; } = "none";
    public int Percent { get; init; }
    public string Scope { get; init; } = BargainScopes.Unspecified;
    public long? PreviousOfferValue { get; init; }
    public long? FinalOfferValue { get; init; }
    public string Message { get; init; } = string.Empty;
}

internal static class NativeTradeAdapter
{
    public static void RefreshCurrentUi()
    {
        try
        {
            PlayerStore.Instance?.RefreshCounterItem();
            NegociationUIManager.Instance?.RefreshUI();
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[当铺世界] 原版报价界面刷新失败：{ex.Message}");
        }
    }

    public static NativeTradeApplication Apply(
        ConversationRequest request,
        ConversationResponse response)
    {
        if (!request.Scene.Equals("store", StringComparison.OrdinalIgnoreCase))
            return Result(false, false, response.TradeProposal, "电话交谈不能修改店内交易");

        try
        {
            var playerStore = PlayerStore.Instance;
            var instance = playerStore?.currentClientInstance;
            var client = instance?.GetClientBlueprint() ?? instance?.storeClient;
            var bargain = BargainUIManager.Instance;
            var negotiation = NegociationUIManager.Instance;
            var item = negotiation?.currentNegociatedItem ?? playerStore?.currentNegociatedItem;
            if (client is null || bargain is null || item is null)
                return Result(false, false, response.TradeProposal, "原版来客或议价界面已结束");

            var liveClientKey = FirstNotBlank(client.identifier, client.displayName);
            BargainAgreementRegistry.TrackClient(client);
            var expectedClientKey = FirstNotBlank(
                request.GameContext.Character.OriginalIdentifier,
                request.GameContext.Character.Key,
                request.CharacterKey);
            if (!string.Equals(liveClientKey, expectedClientKey, StringComparison.OrdinalIgnoreCase))
                return Result(false, false, response.TradeProposal, "回复期间来客已经改变");

            var snapshotItems = SnapshotRelevantItems(
                request.GameContext.Trade.Direction,
                playerStore,
                negotiation,
                item);
            var proposal = TradeProposalResolver.Resolve(
                request.PlayerText,
                request.GameContext.Trade,
                response.TradeProposal,
                snapshotItems.Count);
            MelonLogger.Msg(
                $"[当铺世界] 自由交谈议价解析：回合 {request.TurnId}，" +
                $"阶段={request.GameContext.Trade.Phase}，原版报价={request.GameContext.Trade.FinalOfferValue?.ToString() ?? "未知"}，" +
                $"原话=\"{request.PlayerText.Trim()}\"，结果={proposal.Kind}/{proposal.Percent}%" +
                $"，目标={proposal.TargetAmount?.ToString() ?? "无"}，原因={proposal.Reason}");
            var pending = BargainAgreementRegistry.GetPending(liveClientKey);
            IReadOnlyCollection<int> sourceSnapshotIds = snapshotItems
                .Select(candidate => candidate.uniqueId)
                .ToList();
            int? requestedCount = null;

            if (pending is not null && (!proposal.Explicit || proposal.Kind == "none"))
            {
                var clarifiedScope = BargainScopeResolver.ResolveClarification(request.PlayerText);
                if (clarifiedScope is null)
                {
                    response.NpcReply = BuildClarificationQuestion();
                    response.TradeProposal = new TradeProposal
                    {
                        Kind = pending.Proposal.Kind,
                        Percent = pending.Proposal.Percent,
                        TargetAmount = pending.Proposal.TargetAmount,
                        RevokesPriorPriceChange = pending.Proposal.RevokesPriorPriceChange,
                        Explicit = false,
                        Reason = "等待玩家确认价格约定是否包含后续新增商品",
                        Scope = BargainScopes.Ambiguous,
                        ScopeReason = "上一轮的范围确认尚未回答"
                    };
                    return Result(true, false, response.TradeProposal, response.NpcReply);
                }

                proposal = new TradeProposal
                {
                    Kind = pending.Proposal.Kind,
                    Percent = pending.Proposal.Percent,
                    TargetAmount = pending.Proposal.TargetAmount,
                    RevokesPriorPriceChange = pending.Proposal.RevokesPriorPriceChange,
                    Explicit = true,
                    Reason = pending.Proposal.Reason,
                    Scope = clarifiedScope,
                    ScopeReason = "玩家已补充确认价格约定范围"
                };
                sourceSnapshotIds = pending.SnapshotItemIds;
            }
            else if (proposal.Explicit)
            {
                BargainAgreementRegistry.ClearPending();
                var scopeDecision = BargainScopeResolver.Resolve(
                    request.PlayerText,
                    snapshotItems.Count);
                requestedCount = scopeDecision.RequestedCount;
                if (scopeDecision.NeedsClarification)
                {
                    var mentionedIds = SelectMentionedItemIds(
                        snapshotItems,
                        request.PlayerText);
                    var pendingTargetIds = mentionedIds is { Count: > 0 }
                        ? mentionedIds.ToList()
                        : sourceSnapshotIds.ToList();
                    BargainAgreementRegistry.SetPending(new PendingBargainClarification
                    {
                        ClientKey = liveClientKey,
                        Direction = request.GameContext.Trade.Direction,
                        PlayerText = request.PlayerText,
                        Proposal = new TradeProposalSnapshot
                        {
                            Kind = proposal.Kind,
                            Percent = proposal.Percent,
                            TargetAmount = proposal.TargetAmount,
                            RevokesPriorPriceChange = proposal.RevokesPriorPriceChange,
                            Reason = proposal.Reason
                        },
                        CurrentItemId = item.uniqueId,
                        SnapshotItemIds = pendingTargetIds
                    });
                    proposal.Scope = BargainScopes.Ambiguous;
                    proposal.ScopeReason = scopeDecision.Reason;
                    response.TradeProposal = proposal;
                    response.NpcReply = BuildClarificationQuestion();
                    return Result(true, false, proposal, response.NpcReply);
                }
            }
            else if (proposal.RevokesPriorPriceChange &&
                     proposal.Scope == BargainScopes.Ambiguous)
            {
                BargainAgreementRegistry.SetPending(new PendingBargainClarification
                {
                    ClientKey = liveClientKey,
                    Direction = request.GameContext.Trade.Direction,
                    PlayerText = request.PlayerText,
                    Proposal = new TradeProposalSnapshot
                    {
                        RevokesPriorPriceChange = true,
                        Reason = proposal.Reason
                    },
                    CurrentItemId = item.uniqueId,
                    SnapshotItemIds = sourceSnapshotIds.ToList()
                });
                response.TradeProposal = proposal;
                response.NpcReply = BuildClarificationQuestion();
                return Result(true, false, proposal, response.NpcReply);
            }

            response.TradeProposal = proposal;
            if ((!proposal.Explicit || proposal.Kind == "none") &&
                !proposal.RevokesPriorPriceChange)
            {
                MelonLogger.Msg(
                    $"[当铺世界] 自由交谈议价未执行：回合 {request.TurnId}，" +
                    $"原版交易可用={request.GameContext.Trade.Available}，原因={proposal.Reason}");
                return Result(false, false, proposal, proposal.Reason);
            }

            var expectedItemKey = request.GameContext.Trade.Item?.RuntimeKey;
            if (pending is null &&
                (expectedItemKey is null || item.uniqueId != expectedItemKey.Value))
                return Result(true, false, proposal, "回复期间柜台物品已经改变");

            var trackedClient = bargain.trackedClient;
            if (trackedClient is not null)
            {
                var trackedKey = FirstNotBlank(trackedClient.identifier, trackedClient.displayName);
                if (!string.Equals(trackedKey, liveClientKey, StringComparison.OrdinalIgnoreCase))
                    return Result(true, false, proposal, "原版议价器当前跟踪的是另一位来客");
            }

            var allAffectedItems = proposal.RevokesPriorPriceChange
                ? SnapshotRelevantItems(
                    request.GameContext.Trade.Direction,
                    playerStore,
                    negotiation,
                    item)
                : SnapshotAffectedUniverse(
                request.GameContext.Trade.Direction,
                playerStore,
                item);
            var selectableItems = ResolveItemsById(
                allAffectedItems,
                sourceSnapshotIds);
            var matchingIds = SelectMentionedItemIds(
                selectableItems,
                request.PlayerText);
            if (requestedCount is > 0 &&
                ((matchingIds is { Count: > 0 } && matchingIds.Count != requestedCount.Value) ||
                 (matchingIds is null && sourceSnapshotIds.Count != requestedCount.Value)))
            {
                response.NpcReply =
                    $"我没法确认你指的{requestedCount.Value}件具体是哪几件。请点明商品名称，或者把它们单独放到柜台上。";
                return Result(true, false, proposal, response.NpcReply);
            }
            var targetIds = BargainScopeResolver.SelectTargetIds(
                proposal.Scope,
                sourceSnapshotIds,
                pending?.CurrentItemId ?? item.uniqueId,
                matchingIds,
                requestedCount);
            if (targetIds.Count == 0)
            {
                response.NpcReply = "我没看出你说的是哪件商品。请点明商品名称，或者说“当前这些”。";
                return Result(true, false, proposal, response.NpcReply);
            }

            if (proposal.RevokesPriorPriceChange)
            {
                var revoked = BargainAgreementRegistry.Revoke(
                    liveClientKey,
                    request.GameContext.Trade.Direction,
                    targetIds,
                    proposal.Scope == BargainScopes.AllCurrentAndFuture);
                if (revoked.Count == 0)
                    return Result(true, false, proposal, "没有找到可撤回的对话价格约定");

                foreach (var agreement in revoked)
                {
                    foreach (var candidate in allAffectedItems)
                    {
                        if (agreement.ItemIds.Contains(candidate.uniqueId))
                            candidate.RemoveItemFeatureByID(agreement.FeatureId);
                    }
                }

                playerStore?.RefreshCounterItem();
                (NegociationUIManager.Instance ?? negotiation)?.RefreshUI();
                MelonLogger.Msg(
                    $"[当铺世界] 已撤回对话价格约定：回合 {request.TurnId}，" +
                    $"商品数={revoked.Sum(agreement => agreement.ItemIds.Count)}");
                return Result(true, false, proposal, "已撤回此前的对话价格约定");
            }

            var canInvoke = CanInvoke(bargain, proposal.Kind);
            MelonLogger.Msg(
                $"[当铺世界] 原版议价接口校验：回合 {request.TurnId}，" +
                $"类型={proposal.Kind}，允许调用={canInvoke}");
            if (!canInvoke)
                return Result(true, false, proposal, "原版规则当前不允许这项议价");

            var templateFeature = BargainAgreementRegistry.CreateFeature(
                proposal.Kind,
                proposal.Percent);
            var featureId = templateFeature?.identifier ?? string.Empty;
            if (string.IsNullOrWhiteSpace(featureId))
                return Result(true, false, proposal, "无法识别原版议价特征，交易保持不变");

            var offerItem = allAffectedItems
                .FirstOrDefault(candidate => targetIds.Contains(candidate.uniqueId)) ?? item;
            var previousOffer = ReadOfferValue(negotiation, offerItem);
            var previousBudget = client.useClientBudget ? client.clientBudget : (int?)null;
            Invoke(bargain, proposal.Kind, proposal.Percent);
            var finalOffer = ReadOfferValue(
                NegociationUIManager.Instance ?? negotiation,
                offerItem);
            var accepted = WasAccepted(bargain, proposal.Kind, proposal.Percent) ||
                           OfferMovedInExpectedDirection(
                               proposal.Kind,
                               previousOffer,
                               finalOffer);
            MelonLogger.Msg(
                $"[当铺世界] 原版议价执行结果：回合 {request.TurnId}，" +
                $"报价 {previousOffer?.ToString() ?? "未知"} -> {finalOffer?.ToString() ?? "未知"}，" +
                $"接受={accepted}");
            if (accepted)
            {
                NarrowAppliedFeature(
                    allAffectedItems,
                    targetIds,
                    featureId,
                    proposal.Kind,
                    proposal.Percent);
                if (proposal.Scope != BargainScopes.AllCurrentAndFuture &&
                    previousBudget is not null)
                {
                    client.OverrideBudget(previousBudget.Value);
                }

                var targetItems = allAffectedItems
                    .Where(candidate => targetIds.Contains(candidate.uniqueId))
                    .ToList();
                BargainAgreementRegistry.Register(new BargainAgreement
                {
                    ClientKey = liveClientKey,
                    Direction = request.GameContext.Trade.Direction,
                    Kind = proposal.Kind,
                    Scope = proposal.Scope,
                    FeatureId = featureId,
                    Percent = proposal.Percent,
                    IncludesFuture = proposal.Scope == BargainScopes.AllCurrentAndFuture,
                    DisplayLine = BuildAgreementDisplay(
                        proposal,
                        targetItems,
                        request.PlayerText)
                }.Also(agreement => agreement.ItemIds.UnionWith(targetIds)));
                playerStore?.RefreshCounterItem();
                (NegociationUIManager.Instance ?? negotiation)?.RefreshUI();
            }
            var message = BuildResolutionMessage(accepted, previousOffer, finalOffer);
            MelonLogger.Msg(
                $"[当铺世界] 自由交谈议价已由原版规则处理：{proposal.Kind} " +
                $"{proposal.Percent}%，{message}");
            return Result(
                true,
                true,
                proposal,
                message,
                accepted,
                previousOffer,
                finalOffer);
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[当铺世界] 原版议价调用失败，交易保持不变：{ex.Message}");
            return Result(
                true,
                false,
                response.TradeProposal,
                "原版议价调用失败，交易保持不变");
        }
    }

    private static bool CanInvoke(BargainUIManager bargain, string kind) => kind switch
    {
        "offer_discount" => bargain.CanOfferDiscount(),
        "ask_markup" => bargain.CanOfferMarkup(),
        "offer_buying_markup" => bargain.CanOfferBuyingMarkup(),
        "ask_buying_discount" => bargain.CanDemandBuyingDiscount(),
        _ => false
    };

    private static void Invoke(BargainUIManager bargain, string kind, int percent)
    {
        switch (kind)
        {
            case "offer_discount":
                bargain.OfferDiscount(percent);
                break;
            case "ask_markup":
                bargain.OfferMarkup(percent);
                break;
            case "offer_buying_markup":
                bargain.OfferBuyingMarkup(percent);
                break;
            case "ask_buying_discount":
                bargain.DemandBuyingDiscount(percent);
                break;
            default:
                throw new InvalidOperationException($"Unsupported bargain kind: {kind}");
        }
    }

    private static bool WasAccepted(BargainUIManager bargain, string kind, int percent) =>
        kind switch
        {
            "offer_discount" =>
                bargain.DiscountOffered && bargain.DiscountPercent == percent,
            "ask_markup" =>
                bargain.MarkupOffered && bargain.MarkupPercent == percent &&
                bargain.markupApplied,
            "offer_buying_markup" =>
                bargain.BuyingMarkupOffered && bargain.BuyingMarkupPercent == percent,
            "ask_buying_discount" =>
                bargain.BuyingDiscountOffered && bargain.BuyingDiscountPercent == percent &&
                bargain.buyingDiscountApplied,
            _ => false
        };

    private static bool OfferMovedInExpectedDirection(
        string kind,
        long? previousOffer,
        long? finalOffer)
    {
        if (previousOffer is null || finalOffer is null)
            return false;

        return kind switch
        {
            "offer_discount" or "ask_buying_discount" => finalOffer < previousOffer,
            "ask_markup" or "offer_buying_markup" => finalOffer > previousOffer,
            _ => false
        };
    }

    private static long? ReadOfferValue(NegociationUIManager? negotiation, GameItem item)
    {
        try
        {
            return negotiation is not null
                ? negotiation.GetFinalOfferValue(item)
                : item.GetNegociatedValue();
        }
        catch
        {
            try
            {
                return item.GetNegociatedValue();
            }
            catch
            {
                return null;
            }
        }
    }

    private static string BuildResolutionMessage(
        bool accepted,
        long? previousOffer,
        long? finalOffer)
    {
        var outcome = accepted ? "客人接受" : "客人拒绝";
        return previousOffer is not null && finalOffer is not null
            ? $"{outcome}，成交报价 {previousOffer} -> {finalOffer}"
            : $"{outcome}，成交报价暂时无法读取";
    }

    private static NativeTradeApplication Result(
        bool proposed,
        bool invoked,
        TradeProposal proposal,
        string message,
        bool accepted = false,
        long? previousOfferValue = null,
        long? finalOfferValue = null) => new()
    {
        Proposed = proposed,
        Invoked = invoked,
        Accepted = accepted,
        Kind = proposal.Kind,
        Percent = proposal.Percent,
        Scope = proposal.Scope,
        PreviousOfferValue = previousOfferValue,
        FinalOfferValue = finalOfferValue,
        Message = message
    };

    private static string FirstNotBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static List<GameItem> SnapshotRelevantItems(
        string direction,
        PlayerStore? playerStore,
        NegociationUIManager? negotiation,
        GameItem currentItem)
    {
        try
        {
            if (direction == "player_sells_to_client" && negotiation is not null)
                return DistinctItems(negotiation.GetSellableItem(), currentItem);
        }
        catch
        {
            // Fall through to the broader native item scan.
        }

        return SnapshotAffectedUniverse(direction, playerStore, currentItem);
    }

    private static List<GameItem> SnapshotAffectedUniverse(
        string direction,
        PlayerStore? playerStore,
        GameItem currentItem)
    {
        try
        {
            if (playerStore is null)
                return new List<GameItem> { currentItem };

            // PlayerStore.FindAllItem(bool) performs a global native type scan
            // in this build.  Besides producing the recurring
            // FindAllObjectsOfType(GameGridInventory/GameSlotInventory) errors,
            // that scan is unsafe while the negotiation scene is rebuilding.
            // Use the already-live negotiation/client collections instead.
            if (direction == "player_sells_to_client")
                return new List<GameItem> { currentItem };

            var clientItems = playerStore.currentClientInstance?.gridInv?.items;
            return DistinctItems(clientItems, currentItem);
        }
        catch
        {
            return new List<GameItem> { currentItem };
        }
    }

    private static List<GameItem> DistinctItems(
        Il2CppSystem.Collections.Generic.List<GameItem>? items,
        GameItem currentItem)
    {
        var result = new List<GameItem>();
        if (items is not null)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var candidate = items[i];
                if (candidate is not null &&
                    result.All(existing => existing.uniqueId != candidate.uniqueId))
                    result.Add(candidate);
            }
        }

        if (result.All(candidate => candidate.uniqueId != currentItem.uniqueId))
            result.Add(currentItem);
        return result;
    }

    private static List<GameItem> ResolveItemsById(
        IEnumerable<GameItem> items,
        IReadOnlyCollection<int> ids) =>
        items.Where(item => ids.Contains(item.uniqueId)).ToList();

    private static IReadOnlyCollection<int>? SelectMentionedItemIds(
        IReadOnlyCollection<GameItem> items,
        string playerText)
    {
        var matches = items
            .Where(item => PlayerTextMentionsItem(playerText, item))
            .Select(item => item.uniqueId)
            .ToList();
        return matches.Count > 0 ? matches : null;
    }

    private static bool PlayerTextMentionsItem(string playerText, GameItem item)
    {
        var text = playerText.Replace(" ", string.Empty);
        var names = new[]
        {
            SafeItemName(item),
            item.name ?? string.Empty,
            item.identifier ?? string.Empty
        };
        foreach (var rawName in names)
        {
            var name = rawName.Replace(" ", string.Empty);
            if (name.Length > 0 && text.Contains(name, StringComparison.OrdinalIgnoreCase))
                return true;
            var core = string.Concat(name.Where(character =>
                !"桶装瓶盒包罐组套份个件支把块".Contains(character)));
            if (core.Length > 0 && text.Contains(core, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string SafeItemName(GameItem item)
    {
        try
        {
            return item.GetDisplayName(true) ?? string.Empty;
        }
        catch
        {
            return FirstNotBlank(item.name, item.identifier);
        }
    }

    private static void NarrowAppliedFeature(
        IReadOnlyCollection<GameItem> items,
        IReadOnlySet<int> targetIds,
        string featureId,
        string kind,
        int percent)
    {
        foreach (var candidate in items)
        {
            if (targetIds.Contains(candidate.uniqueId))
            {
                if (candidate.FindItemFeatureByID(featureId) is null)
                {
                    var feature = BargainAgreementRegistry.CreateFeature(kind, percent);
                    if (feature is not null)
                        candidate.AddItemFeature(feature);
                }
            }
            else if (candidate.FindItemFeatureByID(featureId) is not null)
            {
                candidate.RemoveItemFeatureByID(featureId);
            }
        }
    }

    private static string BuildAgreementDisplay(
        TradeProposal proposal,
        IReadOnlyCollection<GameItem> targetItems,
        string playerText)
    {
        var future = proposal.Scope == BargainScopes.AllCurrentAndFuture
            ? "整单及后续新增商品"
            : DescribeTargets(targetItems, playerText);
        var price = proposal.Kind switch
        {
            "offer_discount" or "ask_buying_discount" when proposal.Percent == 50 => "五折",
            "offer_discount" or "ask_buying_discount" => $"优惠{proposal.Percent}%",
            _ => $"加价{proposal.Percent}%"
        };
        return $"本次{future}{price}";
    }

    private static string DescribeTargets(
        IReadOnlyCollection<GameItem> targetItems,
        string playerText)
    {
        if (targetItems.Count == 0)
            return "约定商品";
        var spokenTarget = ExtractSpokenTarget(playerText);
        if (!string.IsNullOrWhiteSpace(spokenTarget))
            return spokenTarget;
        var names = targetItems.Select(SafeItemName).Distinct().ToList();
        var name = names.Count == 1 ? names[0] : "指定商品";
        return targetItems.Count == 1
            ? name
            : $"{targetItems.Count}件{name}";
    }

    private static string ExtractSpokenTarget(string playerText)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            playerText,
            @"(?:这|那|当前|眼前|本次)?\s*(?<count>[一二两三四五六七八九十\d]+)\s*(?<unit>个|件|瓶|盒|包|把|支|块|桶|罐|份|组|套)\s*(?<name>[^，。,.！!？?]{0,12}?)(?=\s*(?:半价|[零〇一二两三四五六七八九十\d]+(?:[点\.]?[零〇一二三四五六七八九\d]+)?\s*折|百分之|\d+(?:\.\d+)?\s*%))",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success)
            return string.Empty;
        return $"{match.Groups["count"].Value}{match.Groups["unit"].Value}" +
               match.Groups["name"].Value.Trim();
    }

    private static string BuildClarificationQuestion() =>
        "这项价格约定只算现在柜台上的商品，还是后面新加的也算？";
}

internal static class ObjectExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
