using AIWorldPawnshop.Models;
using AIWorldPawnshop.Compatibility;
using MelonLoader;

namespace AIWorldPawnshop.Runtime;

internal sealed class BarterApplication
{
    public bool Proposed { get; init; }
    public bool Applied { get; init; }
    public string Message { get; init; } = string.Empty;
}

internal static class NativeBarterAdapter
{
    private static string _activeClientKey = string.Empty;
    private static StoreClient.ClientIntent _originalIntent;
    private static BarterOffer? _originalOffer;
    private static BarterExecutionSnapshot? _execution;
    private static bool _uiRefreshPending;
    private static int _uiRefreshAttempts;
    private const int MaxUiRefreshAttempts = 45;

    internal static bool HasActiveExecution => _execution is not null;

    internal static bool IsActiveUiOffer(StoreClient? client)
    {
        var execution = _execution;
        var offer = client?.barterOffer;
        return execution is { Failed: false } && offer is not null &&
               FirstNotBlank(client?.identifier, client?.displayName)
                   .Equals(execution.ClientKey, StringComparison.OrdinalIgnoreCase) &&
               IsMatchingNativeOffer(offer, execution);
    }

    internal static List<GameItem> FilterDisplayedClientItems(List<GameItem> items)
    {
        var targetKey = _execution?.ClientItemRuntimeKey ?? 0;
        var target = items.FirstOrDefault(item => SafeRuntimeKey(item) == targetKey);
        return target is null ? new List<GameItem>() : new List<GameItem> { target };
    }

    /// <summary>
    /// The native condition language has no runtime-key field.  These are the
    /// only existing discriminators that can be put into a native condition
    /// without changing an item: its identifier, an already-enabled tag, and
    /// existing item-condition identifiers to exclude.
    /// </summary>
    private sealed class BarterConditionPlan
    {
        public string RequiredItemId { get; init; } = string.Empty;
        public string RequiredItemTag { get; init; } = string.Empty;
        public List<string> BlacklistedConditionIds { get; init; } = new();
        public int TargetRuntimeKey { get; init; }
    }

    /// <summary>
    /// The native barter UI describes a condition by identifier, but the
    /// native commit path later traverses whole counter grids.  Keep the
    /// exact runtime keys selected by the conversation so the commit hooks
    /// can narrow that traversal back to the objects shown to the player.
    /// </summary>
    private sealed class BarterExecutionSnapshot
    {
        public string ClientKey { get; init; } = string.Empty;
        public int ClientItemRuntimeKey { get; init; }
        public GameItem? ClientItemObject { get; init; }
        public string ClientItemIdentifier { get; init; } = string.Empty;
        public string ClientItemName { get; init; } = string.Empty;
        public List<int> PlayerItemRuntimeKeys { get; init; } = new();
        public List<GameItem> PlayerItemObjects { get; init; } = new();
        public List<string> PlayerItemIdentifiers { get; init; } = new();
        public List<string> PlayerItemNames { get; init; } = new();
        public List<BarterConditionPlan> PlayerItemConditionPlans { get; init; } = new();
        public int PromisedCashAmount { get; init; }
        public BarterOffer? NativeOffer { get; set; }
        public bool FrontOwnershipHandled { get; set; }
        public bool BackOwnershipHandled { get; set; }
        public bool SwapHandled { get; set; }
        public bool ItemsSoldHandled { get; set; }
        public bool Failed { get; set; }
        public string FailureReason { get; set; } = string.Empty;
    }

    public static BarterApplication ApplyConversation(
        ConversationRequest request,
        ConversationResponse response)
    {
        var proposal = response.BarterProposal;
        if (!proposal.Explicit)
            return Result(false, false, proposal.Reason);
        if (!proposal.Accepted)
            return Result(true, false, proposal.Reason);

        try
        {
            var playerStore = PlayerStore.Instance;
            var instance = playerStore?.currentClientInstance;
            var client = FormalGameAccess.GetClient(instance);
            var inventory = FormalGameAccess.GetItems(FormalGameAccess.GetInventory(instance)).ToList();
            if (playerStore is null || client is null)
                return Result(true, false, "原版来客已经离开");

            // The native counter list combines player and client-facing areas.
            // Capture ownership once and use it for both sides of the proposal;
            // a counter location alone is never enough evidence of ownership.
            var ownership = CounterOwnershipSnapshot.Capture(playerStore, instance);

            var expectedKey = FirstNotBlank(
                request.GameContext.Character.OriginalIdentifier,
                request.GameContext.Character.Key,
                request.CharacterKey);
            var liveKey = FirstNotBlank(client.identifier, client.displayName);
            if (!liveKey.Equals(expectedKey, StringComparison.OrdinalIgnoreCase))
                return Result(true, false, "回复到达时来客已经改变");

            var offeredItem = FindClientItem(
                inventory,
                ownership,
                proposal,
                request.GameContext.Trade.Item);
            if (offeredItem is null)
                return Result(true, false, "顾客物品栏和当前交易物品中都没有模型指定的交换物");

            var requiredItems = FindPlayerItems(playerStore, ownership, proposal);
            if (requiredItems.Count == 0)
                return Result(true, false, "没有识别出玩家用于交换的真实物品，请先把物品放到柜台再说清楚");

            if (!TryBuildConditionPlans(
                    requiredItems,
                    out var conditionPlans,
                    out var conditionFailure))
                return Result(true, false, conditionFailure);

            if (offeredItem.uniqueId <= 0 || requiredItems.Any(item => item.uniqueId <= 0))
                return Result(true, false, "交换物缺少可验证的唯一标识，无法安全成交");

            if (_execution is not null)
                return Result(true, false, "上一笔交换还在确认中，请先完成或取消它");

            var offeredName = DisplayName(offeredItem);
            var conditions = new System.Collections.Generic.List<BarterCondition>();
            for (var index = 0; index < requiredItems.Count; index++)
            {
                var requiredItem = requiredItems[index];
                var plan = conditionPlans[index];
                conditions.Add(new BarterCondition
                {
                    requiredItemId = plan.RequiredItemId,
                    requiredItemTag = plan.RequiredItemTag,
                    requiredDisplay = DisplayName(requiredItem),
                    offeredDisplay = offeredName,
                    blacklistedConditionId = ToNativeStringList(plan.BlacklistedConditionIds)
                });
            }
            var requiredName = string.Join("、", requiredItems.Select(DisplayName));
            var barterText = $"用{requiredName}交换{offeredName}";
            if (response.WalletTransfer.Explicit &&
                response.WalletTransfer.Accepted &&
                response.WalletTransfer.Amount > 0)
            {
                barterText += $"，并补付{response.WalletTransfer.Amount}信用点";
            }

            var acceptedText = string.IsNullOrWhiteSpace(response.NpcReply)
                ? $"行，{requiredName}换{offeredName}。"
                : response.NpcReply;
            var offer = new BarterOffer
            {
                ownerClient = client,
                barterTextBody = barterText,
                conditions = conditions,
                acceptedResponse = acceptedText,
                deniedResponse = $"这不是说好的{requiredName}。",
                currentSetIndex = 0,
                offerSets = new System.Collections.Generic.List<System.Action>()
            };
            // The offer is instantiated and accepted asynchronously.  Capture
            // only immutable scalar identity here; IL2CPP GameItem wrappers
            // and PlayerStore instances may be rebuilt before the callback.
            var offeredRuntimeKey = offeredItem.uniqueId;
            var offeredIdentifier = Safe(() => offeredItem.identifier, string.Empty);
            var offeredDisplayName = offeredName;
            var offeredClientKey = liveKey;
            offer.onInstance = new System.Action(() =>
            {
                BarterExecutionSnapshot? callbackExecution = null;
                try
                {
                    // The native offer is accepted asynchronously.  The
                    // customer can leave or the counter can rebuild before
                    // this callback runs, so never let a stale IL2CPP object
                    // escape into the game's event loop.
                    // The selected customer item is often already on the
                    // customer-side counter.  Re-adding it can merge or
                    // recreate a stack and invalidate the captured runtime
                    // key, so only place it when the native counter lacks it.
                    var execution = _execution;
                    callbackExecution = execution;
                    if (execution is null ||
                        !execution.ClientKey.Equals(
                            offeredClientKey,
                            StringComparison.OrdinalIgnoreCase) ||
                        execution.ClientItemRuntimeKey != offeredRuntimeKey)
                    {
                        if (execution is not null)
                            FailExecution(execution, "交换接收回调对应的成交对象已改变");
                        return;
                    }

                    var liveStore = PlayerStore.Instance;
                    var liveEntry = EmporiumEntry.Instance;
                    if (liveStore is null || liveEntry is null)
                    {
                        FailExecution(execution, "交换接收时原版柜台已经重建");
                        return;
                    }

                    var frontItems = ReadGridItems(liveEntry.frontInvinvElement);
                    var liveItem = ResolveLiveClientItem(
                        liveEntry,
                        offeredRuntimeKey,
                        offeredIdentifier,
                        offeredDisplayName,
                        out var alreadyOnCounter);
                    if (liveItem is null)
                    {
                        FailExecution(execution, "交换接收时找不到约定的顾客物品");
                        return;
                    }

                    if (!TryReadOwnership(liveItem, out var liveItemOwned) || liveItemOwned)
                    {
                        FailExecution(execution, "交换接收时顾客目标已经不是顾客物品");
                        return;
                    }

                    if (!alreadyOnCounter &&
                        !frontItems.Any(item =>
                            SafeRuntimeKey(item) == SafeRuntimeKey(liveItem)))
                    {
                        liveStore.AddDirectSellingItemToTable(
                            liveItem,
                            false,
                            false,
                            true,
                            0);
                    }
                }
                catch (Exception ex)
                {
                    if (callbackExecution is not null &&
                        ReferenceEquals(_execution, callbackExecution))
                    {
                        FailExecution(callbackExecution, $"交换接收顾客物品失败：{ex.Message}");
                    }
                    MelonLogger.Warning($"[当铺世界] 交换接收顾客物品失败：{ex.Message}");
                }
            });
            offer.getBarterResult = new System.Func<BarterResult>(() => new BarterResult
            {
                isAccepted = true,
                response = acceptedText
            });

            _activeClientKey = liveKey;
            _originalIntent = client.clientIntent;
            _originalOffer = client.barterOffer;
            _execution = new BarterExecutionSnapshot
            {
                ClientKey = liveKey,
                ClientItemRuntimeKey = offeredItem.uniqueId,
                ClientItemObject = offeredItem,
                ClientItemIdentifier = Safe(() => offeredItem.identifier, string.Empty),
                ClientItemName = offeredName,
                PlayerItemRuntimeKeys = requiredItems.Select(item => item.uniqueId).ToList(),
                PlayerItemObjects = requiredItems.ToList(),
                PlayerItemIdentifiers = requiredItems
                    .Select(item => Safe(() => item.identifier, string.Empty))
                    .ToList(),
                PlayerItemNames = requiredItems
                    .Select(DisplayName)
                    .ToList(),
                PlayerItemConditionPlans = conditionPlans,
                PromisedCashAmount = response.WalletTransfer.Explicit &&
                                     response.WalletTransfer.Accepted
                    ? response.WalletTransfer.Amount
                    : 0
            };
            _execution.NativeOffer = offer;
            client.SetBarterOffer(offer);
            client.clientIntent = StoreClient.ClientIntent.BARTER;
            offer.InstanciateOffer();
            // The native offer and counter rebuild finish after this callback.
            // Redraw from the update loop so the old quote is not left on top.
            playerStore.RefreshCounterItem();
            ScheduleUiRefresh();
            MelonLogger.Msg(
                $"[当铺世界] 已进入原版交换流程：{requiredName} -> {offeredName}");
            return Result(true, true, $"已进入原版交换流程：{requiredName}换{offeredName}");
        }
        catch (Exception ex)
        {
            RestoreOriginalIntent();
            MelonLogger.Error($"[当铺世界] 创建原版交换失败：{ex.Message}");
            return Result(true, false, "原版交换流程初始化失败");
        }
    }

    public static bool RestoreOriginalIntent(StoreClient? client = null)
    {
        if (string.IsNullOrWhiteSpace(_activeClientKey))
            return false;
        try
        {
            client ??= FormalGameAccess.GetClient(PlayerStore.Instance?.currentClientInstance);
            var liveKey = FirstNotBlank(client?.identifier, client?.displayName);
            if (client is not null && liveKey.Equals(_activeClientKey, StringComparison.OrdinalIgnoreCase))
            {
                client.clientIntent = _originalIntent;
                client.barterOffer = _originalOffer;
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[当铺世界] 恢复交换前来意失败：{ex.Message}");
        }
        finally
        {
            NativeWalletAdapter.CancelPending(_activeClientKey);
            _activeClientKey = string.Empty;
            _originalOffer = null;
            _execution = null;
            _uiRefreshPending = false;
            _uiRefreshAttempts = 0;
        }
        return true;
    }

    /// <summary>
    /// Retries the native barter UI after the offer is created.  The native
    /// counter can rebuild its negotiation manager one frame after the offer
    /// callback, which otherwise makes the player re-place an item to trigger
    /// the same refresh.
    /// </summary>
    public static void Update()
    {
        if (!_uiRefreshPending || _execution is null)
            return;

        if (_uiRefreshAttempts++ >= MaxUiRefreshAttempts)
        {
            _uiRefreshPending = false;
            MelonLogger.Warning("[当铺世界] 交换 UI 多次刷新仍未完成");
            return;
        }

        TryRefreshUi();
    }

    private static void ScheduleUiRefresh()
    {
        _uiRefreshPending = true;
        _uiRefreshAttempts = 0;
    }

    private static bool TryRefreshUi()
    {
        try
        {
            var negotiation = NegociationUIManager.Instance;
            if (negotiation is null || negotiation.uiObject is null ||
                !negotiation.uiObject.activeInHierarchy)
                return false;

            // Reproduce the native item-placement redraw. RefreshUI followed by
            // InitUIWithItemBarterMode leaves the previous quote's text alive.
            var item = negotiation.currentNegociatedItem;
            negotiation.ClearUI();
            if (item is null)
                negotiation.UpdateUI();
            else
                negotiation.UpdateUI(item);
            _uiRefreshPending = false;
            return true;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[当铺世界] 交换 UI 刷新等待下一帧：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Keeps the game's normal placement flow, but replaces its generic
    /// rejection line when the active offer belongs to this exact exchange.
    /// </summary>
    public static bool TryOverridePlacedItemResponse(
        BarterOffer offer,
        out string response)
    {
        response = string.Empty;
        var execution = _execution;
        if (execution is null || !IsMatchingNativeOffer(offer, execution))
            return false;

        try
        {
            var entry = EmporiumEntry.Instance;
            if (entry is null)
                return false;

            var expectedKeys = execution.PlayerItemRuntimeKeys
                .Where(key => key > 0)
                .ToHashSet();
            var placedOwned = ReadGridItems(entry.backInvinvElement)
                .Where(item => TryReadOwnership(item, out var owned) && owned)
                .ToList();
            var exactItemsPresent = expectedKeys.Count > 0 &&
                expectedKeys.All(key => placedOwned.Any(item => SafeRuntimeKey(item) == key));
            var hasUnexpectedItem = placedOwned.Any(item =>
                !expectedKeys.Contains(SafeRuntimeKey(item)));
            if (placedOwned.Count > 0 && (!exactItemsPresent || hasUnexpectedItem))
            {
                response = $"这不是说好的{string.Join("、", execution.PlayerItemNames)}。";
                return true;
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[当铺世界] 校验交换放置物回复失败：{ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Commits a mixed barter's wallet leg only after the native accept path has
    /// completed all exact-item transfer hooks. A cancelled or failed offer
    /// clears the reservation without touching either balance.
    /// </summary>
    public static void CompleteAcceptedTransaction()
    {
        var execution = _execution;
        if (execution is null)
            return;

        var complete = !execution.Failed &&
                       execution.ItemsSoldHandled &&
                       execution.FrontOwnershipHandled &&
                       execution.BackOwnershipHandled &&
                       execution.SwapHandled;
        if (!complete)
        {
            NativeWalletAdapter.CancelPending(execution.ClientKey);
            MelonLogger.Warning(
                $"[当铺世界] 交换确认未完成全部物品阶段，补款未执行：" +
                $"售出记录={execution.ItemsSoldHandled}, " +
                $"顾客归属={execution.FrontOwnershipHandled}, " +
                $"玩家归属={execution.BackOwnershipHandled}, " +
                $"转移={execution.SwapHandled}，{execution.FailureReason}");
            return;
        }

        if (execution.PromisedCashAmount <= 0)
        {
            NativeWalletAdapter.CancelPending(execution.ClientKey);
            return;
        }

        if (!NativeWalletAdapter.CommitPending(execution.ClientKey))
            MelonLogger.Warning("[当铺世界] 交换物已成交，但确认后的补款未执行");
    }

    /// <summary>
    /// Called before the native confirmation method.  A stale or incomplete
    /// snapshot must cancel the confirmation rather than fall through to the
    /// game's all-items implementation.
    /// </summary>
    public static bool ValidateBeforeAccept()
    {
        var execution = _execution;
        if (execution is null)
            return true;

        if (execution.Failed)
            return false;

        try
        {
            var playerStore = PlayerStore.Instance;
            var client = FormalGameAccess.GetClient(playerStore?.currentClientInstance);
            var liveKey = FirstNotBlank(client?.identifier, client?.displayName);
            if (client is null || !liveKey.Equals(execution.ClientKey, StringComparison.OrdinalIgnoreCase))
                return CancelExecution(execution, "确认时来客已改变");
            if (execution.PromisedCashAmount > 0 &&
                !NativeWalletAdapter.CanCommitPending(
                    execution.ClientKey, execution.PromisedCashAmount))
                return CancelExecution(execution, "确认时顾客补款已不可用");

            var entry = EmporiumEntry.Instance;
            if (entry is null)
                return CancelExecution(execution, "确认时柜台尚未准备好");

            var clientItem = ResolveClientItem(entry, execution);
            if (clientItem is null)
                return CancelExecution(execution, "确认时找不到约定的顾客物品");
            if (!TryReadOwnership(clientItem, out var clientItemOwned) || clientItemOwned)
                return CancelExecution(execution, "确认时顾客目标已经不是顾客物品");

            // The native UI may rebuild wrappers after the offer is shown.
            // Resolve by the captured runtime key first, then by the same
            // verified native condition plan.  Never fall back to collection
            // order or to an arbitrary same-identifier item.
            var playerItems = ResolvePlayerItems(entry, execution);
            if (playerItems.Count != execution.PlayerItemRuntimeKeys.Count)
                return CancelExecution(execution, "确认时找不到约定的玩家物品");

            foreach (var item in playerItems)
            {
                if (!TryReadOwnership(item, out var itemOwned) || !itemOwned)
                    return CancelExecution(execution, "确认时约定的玩家物品已不再归玩家所有");
            }

            return true;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[当铺世界] 交换确认校验失败：{ex.Message}");
            return CancelExecution(execution, "交换物状态无法确认");
        }
    }

    /// <summary>
    /// The original BarterOffer.OnBarterAccepted scans every owned item that
    /// satisfies an identifier-based condition and passes the whole list to
    /// PlayerStore.OnItemsSold.  That is correct for the game's own offers,
    /// but unsafe for a conversation-selected runtime object.  Preserve the
    /// native sold-item side effects while submitting only the captured keys.
    /// </summary>
    public static bool SkipNativeBarterAccepted(BarterOffer offer)
    {
        var execution = _execution;
        if (execution is null || !IsMatchingNativeOffer(offer, execution))
            return false;

        // Always skip the original once this is recognised as a Mod offer.
        // A failed snapshot must never fall through to the native all-items
        // scan.
        if (execution.Failed || execution.ItemsSoldHandled)
            return true;

        try
        {
            var entry = EmporiumEntry.Instance;
            var store = PlayerStore.Instance;
            if (entry is null || store is null)
            {
                FailExecution(execution, "成交记录阶段原版柜台已经重建");
                return true;
            }

            var playerItems = ResolvePlayerItems(entry, execution);
            if (playerItems.Count != execution.PlayerItemRuntimeKeys.Count)
            {
                FailExecution(execution, "成交记录阶段找不到约定的玩家物品");
                return true;
            }

            var soldItems = new System.Collections.Generic.List<GameItem>();
            foreach (var item in playerItems)
            {
                if (!TryReadOwnership(item, out var owned) || !owned)
                {
                    FailExecution(execution, "成交记录阶段约定的玩家物品归属已改变");
                    return true;
                }
                soldItems.Add(item);
            }

            store.OnItemsSold(soldItems);
            execution.ItemsSoldHandled = true;
            MelonLogger.Msg(
                $"[当铺世界] 精确交换：仅记录玩家物品售出 " +
                $"{string.Join(",", execution.PlayerItemRuntimeKeys)}");
            return true;
        }
        catch (Exception ex)
        {
            FailExecution(execution, $"成交记录阶段执行失败：{ex.Message}");
            return true;
        }
    }

    private static bool IsMatchingNativeOffer(
        BarterOffer offer,
        BarterExecutionSnapshot execution)
    {
        if (execution.NativeOffer is not null &&
            ReferenceEquals(execution.NativeOffer, offer))
            return true;

        try
        {
            var owner = offer?.ownerClient;
            var ownerKey = FirstNotBlank(owner?.identifier, owner?.displayName);
            if (owner is null ||
                !ownerKey.Equals(execution.ClientKey, StringComparison.OrdinalIgnoreCase))
                return false;

            var expectedBody =
                $"用{string.Join("、", execution.PlayerItemNames)}交换{execution.ClientItemName}";
            var actualBody = Safe(() => offer!.barterTextBody, string.Empty);
            // Mixed barter offers append a cash leg to the same native text.
            // Match the exact item exchange prefix while allowing that known
            // suffix; otherwise the native callback falls through to its
            // all-items scan and the deferred wallet settlement is cancelled.
            return actualBody.Equals(expectedBody, StringComparison.Ordinal) ||
                   actualBody.StartsWith(expectedBody + "，并补付", StringComparison.Ordinal) ||
                   actualBody.StartsWith(expectedBody + ",并补付", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Harmony Prefix helpers.  A true return value means the original native
    /// method must be skipped.  When no Mod barter is active they return
    /// false, preserving every ordinary native barter and sale.
    /// </summary>
    public static bool SkipNativeTransferOwnershipFront(EmporiumEntry entry) =>
        HandleNativeTransfer(entry, TransferPhase.FrontOwnership);

    public static bool SkipNativeTransferOwnershipBack(EmporiumEntry entry) =>
        HandleNativeTransfer(entry, TransferPhase.BackOwnership);

    public static bool SkipNativeSwap(EmporiumEntry entry) =>
        HandleNativeTransfer(entry, TransferPhase.Swap);

    private enum TransferPhase
    {
        FrontOwnership,
        BackOwnership,
        Swap
    }

    private static bool HandleNativeTransfer(EmporiumEntry entry, TransferPhase phase)
    {
        var execution = _execution;
        if (execution is null)
            return false;

        // Never allow a stale Mod snapshot to fall through to the native
        // implementation: that implementation intentionally processes every
        // object in the counter area.
        if (!IsLiveExecution(execution))
            return true;

        try
        {
            switch (phase)
            {
                case TransferPhase.FrontOwnership:
                    if (execution.FrontOwnershipHandled)
                        return true;
                    var clientItem = ResolveClientItem(entry, execution);
                    if (clientItem is null)
                    {
                        FailExecution(execution, "成交阶段找不到顾客目标物品");
                        return true;
                    }
                    if (!TryReadOwnership(clientItem, out var clientItemOwned) || clientItemOwned)
                    {
                        FailExecution(execution, "成交阶段顾客目标已经不是顾客物品");
                        return true;
                    }

                    GeneralHelper.SetItemOwned(clientItem, true);
                    execution.FrontOwnershipHandled = true;
                    LogExecution("顾客目标归属", execution, clientItem);
                    return true;

                case TransferPhase.BackOwnership:
                    if (execution.BackOwnershipHandled)
                        return true;
                    var playerItems = ResolvePlayerItems(entry, execution);
                    if (playerItems.Count != execution.PlayerItemRuntimeKeys.Count)
                    {
                        FailExecution(execution, "成交阶段找不到约定的玩家物品");
                        return true;
                    }

                    foreach (var item in playerItems)
                    {
                        if (!TryReadOwnership(item, out var itemOwned) || !itemOwned)
                        {
                            FailExecution(execution, "成交阶段约定的玩家物品已不再归玩家所有");
                            return true;
                        }
                        GeneralHelper.SetItemOwned(item, false);
                    }
                    execution.BackOwnershipHandled = true;
                    MelonLogger.Msg(
                        $"[当铺世界] 精确交换：仅转移玩家物品 " +
                        $"{string.Join(",", execution.PlayerItemRuntimeKeys)}");
                    return true;

                case TransferPhase.Swap:
                    if (execution.SwapHandled)
                        return true;
                    var target = ResolveClientItem(entry, execution);
                    if (target is null)
                    {
                        FailExecution(execution, "成交阶段找不到顾客目标物品");
                        return true;
                    }

                    // The original SwapInv enumerates the complete front
                    // inventory.  Feed only the selected object to the same
                    // native transfer primitive, preserving stack semantics.
                    // The generated wrapper exposes the back counter as a
                    // GameGridInventory, while the game's public overload
                    // accepting that concrete type is TryAcceptAllMid.  It
                    // performs the same slot/stack transfer as the native
                    // SwapInv call without requiring an unsafe wrapper cast.
                    var remaining = GraphUtils.TryAcceptAllMid(
                        entry.backInvinvElement,
                        target,
                        -1);
                    if (remaining != 0)
                    {
                        FailExecution(
                            execution,
                            $"精确交换目标未能完全放入玩家柜台，剩余数量={remaining}");
                        return true;
                    }

                    execution.SwapHandled = true;
                    LogExecution("顾客目标转移", execution, target);
                    return true;
            }
        }
        catch (Exception ex)
        {
            FailExecution(execution, $"成交阶段执行失败：{ex.Message}");
            return true;
        }

        return true;
    }

    private static bool IsLiveExecution(BarterExecutionSnapshot execution)
    {
        if (execution.Failed)
            return false;

        try
        {
            var client = FormalGameAccess.GetClient(PlayerStore.Instance?.currentClientInstance);
            var liveKey = FirstNotBlank(client?.identifier, client?.displayName);
            if (client is null || !liveKey.Equals(execution.ClientKey, StringComparison.OrdinalIgnoreCase))
            {
                FailExecution(execution, "原版来客在成交期间发生变化");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            FailExecution(execution, $"成交对象无法确认：{ex.Message}");
            return false;
        }
    }

    private static GameItem? ResolveClientItem(
        EmporiumEntry entry,
        BarterExecutionSnapshot execution)
    {
        var front = ReadGridItems(entry.frontInvinvElement);
        var exact = front.FirstOrDefault(item =>
            SafeRuntimeKey(item) == execution.ClientItemRuntimeKey);
        if (exact is not null)
            return exact;

        // OnItemsSold and the native counter callbacks can remove the selected
        // object from the live front list before the later ownership hooks run.
        // Keep the object captured when the offer was created as the strict
        // runtime-key fallback; it is safer than choosing a same-name neighbour.
        var captured = execution.ClientItemObject;
        if (captured is not null &&
            SafeRuntimeKey(captured) == execution.ClientItemRuntimeKey)
            return captured;

        // A native stack merge can recreate the wrapper.  Textual fallback is
        // only allowed when it now resolves to one object.
        return ResolveUniqueItem(
            front,
            new[] { execution.ClientItemIdentifier, execution.ClientItemName },
            0);
    }

    /// <summary>
    /// Resolve the selected customer item at callback time.  The native UI
    /// can replace a wrapper or move the item back to the customer's grid
    /// between offer creation and instantiation, so the original reference is
    /// deliberately never reused.
    /// </summary>
    private static GameItem? ResolveLiveClientItem(
        EmporiumEntry entry,
        int runtimeKey,
        string identifier,
        string displayName,
        out bool alreadyOnCounter)
    {
        alreadyOnCounter = false;
        var front = ReadGridItems(entry.frontInvinvElement);
        var exactFront = front.FirstOrDefault(item => SafeRuntimeKey(item) == runtimeKey);
        if (exactFront is not null)
        {
            alreadyOnCounter = true;
            return exactFront;
        }

        var clientInventory = ReadGridItems(
            FormalGameAccess.GetInventory(PlayerStore.Instance?.currentClientInstance));
        var all = front.Concat(clientInventory).ToList();
        var exact = all.FirstOrDefault(item => SafeRuntimeKey(item) == runtimeKey);
        if (exact is not null)
        {
            alreadyOnCounter = front.Any(item =>
                SafeRuntimeKey(item) == SafeRuntimeKey(exact));
            return exact;
        }

        // A stack merge can recreate the wrapper with a new runtime key.  A
        // textual fallback is safe only when exactly one customer-side object
        // remains identifiable; ambiguity must cancel the exchange.
        var match = ResolveUniqueItem(all, new[] { identifier, displayName }, 0);
        if (match is null)
            return null;

        alreadyOnCounter = front.Any(item =>
            SafeRuntimeKey(item) == SafeRuntimeKey(match));
        return match;
    }

    private static List<GameItem> ResolvePlayerItems(
        EmporiumEntry entry,
        BarterExecutionSnapshot execution)
    {
        var nativeItems = ReadPlayerBackItems(entry);
        var result = new List<GameItem>();
        foreach (var key in execution.PlayerItemRuntimeKeys)
        {
            var match = nativeItems.FirstOrDefault(item => SafeRuntimeKey(item) == key);
            if (match is null)
            {
                // The original object can disappear from both back-counter
                // lists after OnItemsSold.  Reuse only the exact captured
                // runtime-key object; never broaden this to same-name items.
                match = execution.PlayerItemObjects.FirstOrDefault(item =>
                    SafeRuntimeKey(item) == key);
            }
            if (match is null)
                continue;
            if (!result.Any(item => SafeRuntimeKey(item) == key))
                result.Add(match);
        }

        if (result.Count == execution.PlayerItemRuntimeKeys.Count)
            return result;

        // If a native UI rebuild replaced a wrapper, resolve by identifier or
        // exact display name only when the condition remains unambiguous.
        foreach (var index in Enumerable.Range(0, execution.PlayerItemRuntimeKeys.Count))
        {
            if (result.Any(item => SafeRuntimeKey(item) == execution.PlayerItemRuntimeKeys[index]))
                continue;

            var identifier = index < execution.PlayerItemIdentifiers.Count
                ? execution.PlayerItemIdentifiers[index]
                : string.Empty;
            var displayName = index < execution.PlayerItemNames.Count
                ? execution.PlayerItemNames[index]
                : string.Empty;
            var plan = index < execution.PlayerItemConditionPlans.Count
                ? execution.PlayerItemConditionPlans[index]
                : null;
            var planned = plan is null
                ? Array.Empty<GameItem>()
                : nativeItems
                    .Where(item => IsConditionPlanMatch(plan, item))
                    .ToArray();
            var match = SelectPreferred(
                planned,
                0) ?? ResolveUniqueItem(
                nativeItems,
                new[] { identifier, displayName },
                0);
            if (match is not null &&
                !result.Any(item => SafeRuntimeKey(item) == SafeRuntimeKey(match)))
            {
                result.Add(match);
            }
        }

        return result
            .GroupBy(SafeRuntimeKey)
            .Where(group => group.Key > 0)
            .Select(group => group.First())
            .ToList();
    }

    private static bool TryBuildConditionPlans(
        IReadOnlyList<GameItem> requiredItems,
        out List<BarterConditionPlan> plans,
        out string failure)
    {
        plans = new List<BarterConditionPlan>();
        failure = string.Empty;

        var ids = requiredItems.Select(SafeRuntimeKey).ToList();
        if (ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Count)
        {
            failure = "玩家交换物无法唯一定位";
            return false;
        }

        var entry = EmporiumEntry.Instance;
        var owned = entry is null ? new List<GameItem>() : ReadOwnedPlayerBackItems(entry);
        foreach (var target in requiredItems)
        {
            var targetKey = SafeRuntimeKey(target);
            var identifier = Safe(() => target.identifier, string.Empty).Trim();
            var candidates = string.IsNullOrWhiteSpace(identifier)
                ? owned.ToList()
                : owned
                    .Where(item => string.Equals(
                        Safe(() => item.identifier, string.Empty),
                        identifier,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (!candidates.Any(item => SafeRuntimeKey(item) == targetKey))
                candidates.Add(target);

            var candidateKeys = candidates
                .Where(item => SafeRuntimeKey(item) > 0)
                .GroupBy(SafeRuntimeKey)
                .Select(group => group.First())
                .ToList();
            if (candidateKeys.Count == 0)
            {
                failure = "玩家交换物所在柜台无法读取，暂时不能安全交换";
                return false;
            }

            var targetTags = ReadActiveTags(target).ToList();
            var targetConditions = ReadCurrentConditionIds(target).ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            var otherConditionIds = candidateKeys
                .Where(item => SafeRuntimeKey(item) != targetKey)
                .SelectMany(ReadCurrentConditionIds)
                .Where(value => !string.IsNullOrWhiteSpace(value) &&
                                !targetConditions.Contains(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var possible = new List<BarterConditionPlan>();
            void AddPossible(string tag, IEnumerable<string> blacklist)
            {
                var normalizedBlacklist = blacklist
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (string.IsNullOrWhiteSpace(identifier) && string.IsNullOrWhiteSpace(tag))
                    return;
                if (!string.IsNullOrWhiteSpace(identifier) &&
                    possible.Any(plan =>
                        string.Equals(plan.RequiredItemId, identifier, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(plan.RequiredItemTag, tag, StringComparison.OrdinalIgnoreCase) &&
                        plan.BlacklistedConditionIds.SequenceEqual(
                            normalizedBlacklist,
                            StringComparer.OrdinalIgnoreCase)))
                    return;
                possible.Add(new BarterConditionPlan
                {
                    RequiredItemId = identifier,
                    RequiredItemTag = tag,
                    BlacklistedConditionIds = normalizedBlacklist,
                    TargetRuntimeKey = targetKey
                });
            }

            // Identifier alone is sufficient only when the native predicate
            // itself proves that it resolves to this one runtime object.
            AddPossible(string.Empty, Array.Empty<string>());

            // Prefer a tag that already exists on this item and excludes all
            // same-identifier neighbours.  No tag is created or changed.
            foreach (var tag in targetTags)
            {
                AddPossible(tag, Array.Empty<string>());
                AddPossible(tag, otherConditionIds);
            }

            // A condition blacklist is the other discriminator supported by
            // the original game.  It is useful when variants share an id and
            // expose different existing condition identifiers.
            if (otherConditionIds.Count > 0)
                AddPossible(string.Empty, otherConditionIds);

            var selected = possible.FirstOrDefault(plan =>
                IsUniqueConditionPlan(plan, candidateKeys, targetKey));
            if (selected is null)
            {
                var description = string.IsNullOrWhiteSpace(identifier)
                    ? DisplayName(target)
                    : identifier;
                failure = $"玩家侧有多个“{description}”，现有标识、标签和状态都无法唯一区分";
                return false;
            }

            plans.Add(selected);
        }

        return true;
    }

    private static bool IsUniqueConditionPlan(
        BarterConditionPlan plan,
        IReadOnlyCollection<GameItem> candidates,
        int targetRuntimeKey)
    {
        var valid = candidates
            .Where(item => IsConditionPlanMatch(plan, item))
            .Select(SafeRuntimeKey)
            .Where(key => key > 0)
            .Distinct()
            .ToList();
        return valid.Count == 1 && valid[0] == targetRuntimeKey;
    }

    private static bool IsConditionPlanMatch(BarterConditionPlan plan, GameItem item)
    {
        try
        {
            var condition = new BarterCondition
            {
                requiredItemId = plan.RequiredItemId,
                requiredItemTag = plan.RequiredItemTag,
                blacklistedConditionId = ToNativeStringList(plan.BlacklistedConditionIds)
            };
            return condition.IsItemValid(item);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> ReadActiveTags(GameItem item)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var system in new[]
        {
            Safe(() => item.state, null),
            Safe(() => item.modifiedState, null)
        })
        {
            var dictionary = Safe(() => system?.dict, null);
            if (dictionary is null)
                continue;
            try
            {
                foreach (var pair in dictionary)
                {
                    var key = pair.Key;
                    var state = pair.Value;
                    if (string.IsNullOrWhiteSpace(key) || state is null)
                        continue;
                    var enabled = Safe(() => state.IsEnabled(), state.valueEnabled);
                    if (enabled && Safe(() => item.IsTag(key), false))
                        result.Add(key);
                }
            }
            catch
            {
                // A scene rebuild can invalidate the native dictionary.  An
                // empty discriminator simply makes the exchange fail closed.
            }
        }

        return result;
    }

    private static IEnumerable<string> ReadCurrentConditionIds(GameItem item)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var features = item.itemFeatures;
            if (features is null)
                return result;
            for (var index = 0; index < features.Count; index++)
            {
                var feature = features[index];
                if (feature is null || Safe(() => feature.isDisabled, false))
                    continue;
                var condition = Safe(() => feature.GetCurrentItemCondition(), null);
                var identifier = Safe(() => condition?.identifier, string.Empty);
                if (!string.IsNullOrWhiteSpace(identifier))
                    result.Add(identifier);
            }
        }
        catch
        {
            // Leave the set empty; the caller will reject if no other
            // discriminator can prove uniqueness.
        }

        return result;
    }

    private static System.Collections.Generic.List<string> ToNativeStringList(
        IEnumerable<string> values)
    {
        var result = new System.Collections.Generic.List<string>();
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                result.Add(value);
        }
        return result;
    }

    private static bool CancelExecution(BarterExecutionSnapshot execution, string reason)
    {
        FailExecution(execution, reason);
        RestoreOriginalIntent();
        return false;
    }

    private static void FailExecution(BarterExecutionSnapshot execution, string reason)
    {
        if (execution.Failed)
            return;
        execution.Failed = true;
        execution.FailureReason = reason;
        MelonLogger.Warning($"[当铺世界] 已阻止不安全的精确交换：{reason}");
    }

    private static void LogExecution(string action, BarterExecutionSnapshot execution, GameItem item)
    {
        MelonLogger.Msg(
            $"[当铺世界] {action}：目标 runtime_key={SafeRuntimeKey(item)}，" +
            $"玩家交换={string.Join(",", execution.PlayerItemRuntimeKeys)}");
    }

    private static List<GameItem> ReadGridItems(GameGridInventory? inventory)
    {
        var result = new List<GameItem>();
        try
        {
            var items = FormalGameAccess.GetItems(inventory);
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (item is not null)
                    result.Add(item);
            }
        }
        catch
        {
            // The native scene can rebuild the grid between confirmation
            // callbacks.  An empty result causes a safe cancellation.
        }

        return result;
    }

    private static List<GameItem> ReadPlayerBackItems(EmporiumEntry entry)
    {
        var result = new List<GameItem>();
        foreach (var inventory in new[]
        {
            entry.backInvinvElementCounter,
            entry.backInvinvElement
        })
        {
            foreach (var item in ReadGridItems(inventory))
            {
                var key = SafeRuntimeKey(item);
                if (key > 0)
                {
                    if (result.Any(existing => SafeRuntimeKey(existing) == key))
                        continue;
                }
                else if (result.Contains(item))
                {
                    continue;
                }

                result.Add(item);
            }
        }

        return result;
    }

    private static List<GameItem> ReadOwnedPlayerBackItems(EmporiumEntry entry)
    {
        var result = new List<GameItem>();
        try
        {
            var owned = entry.GetOwnedItemsBack();
            if (owned is not null)
            {
                for (var index = 0; index < owned.Count; index++)
                {
                    var item = owned[index];
                    if (item is null || SafeRuntimeKey(item) <= 0)
                        continue;
                    if (!result.Any(existing =>
                            SafeRuntimeKey(existing) == SafeRuntimeKey(item)))
                        result.Add(item);
                }
            }
        }
        catch
        {
            // Fall back to the raw counter lists below when the native list is
            // being rebuilt.  Ownership is still checked per item.
        }

        if (result.Count > 0)
            return result;

        foreach (var item in ReadPlayerBackItems(entry))
        {
            if (TryReadOwnership(item, out var owned) && owned &&
                !result.Any(existing =>
                    SafeRuntimeKey(existing) == SafeRuntimeKey(item)))
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static bool ContainsRuntimeKey(GameGridInventory? inventory, int runtimeKey) =>
        runtimeKey > 0 && ReadGridItems(inventory).Any(item => SafeRuntimeKey(item) == runtimeKey);

    private static int SafeRuntimeKey(GameItem? item)
    {
        try { return item?.uniqueId ?? 0; }
        catch { return 0; }
    }

    private static bool TryReadOwnership(GameItem item, out bool owned)
    {
        try
        {
            owned = GeneralHelper.IsItemOwned(item);
            return true;
        }
        catch
        {
            owned = false;
            return false;
        }
    }

    private static GameItem? FindClientItem(
        System.Collections.Generic.List<GameItem>? inventory,
        CounterOwnershipSnapshot ownership,
        BarterProposal proposal,
        NativeItemContext? currentContext)
    {
        var candidates = new List<GameItem>();
        var candidateIds = new HashSet<int>();

        void AddCandidate(GameItem? item)
        {
            if (item is null)
                return;
            if (!ownership.IsClientItem(item))
                return;
            if (item.uniqueId > 0)
            {
                if (!candidateIds.Add(item.uniqueId))
                    return;
            }
            else if (candidates.Contains(item))
            {
                return;
            }
            candidates.Add(item);
        }

        if (inventory is not null)
        {
            for (var i = 0; i < inventory.Count; i++)
            {
                AddCandidate(inventory[i]);
            }
        }

        foreach (var item in ownership.ClientInventoryItems)
            AddCandidate(item);
        foreach (var item in ownership.ClientCounterItems)
            AddCandidate(item);

        // A runtime key is the strongest identity available.  Do not replace
        // it with a textual match when the model supplied one.
        if (proposal.ClientItemRuntimeKey is > 0)
        {
            var match = candidates.FirstOrDefault(item =>
                SafeRuntimeKey(item) == proposal.ClientItemRuntimeKey.Value);
            if (match is not null)
                return match;
        }

        var contextKey = currentContext?.RuntimeKey > 0 &&
                         string.Equals(
                             currentContext.Owner,
                             "client",
                             StringComparison.OrdinalIgnoreCase)
            ? currentContext.RuntimeKey
            : 0;
        var requestedName = proposal.ClientItemName?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            // Prefer an exact identifier/display match.  A fuzzy match is
            // only accepted when it leaves one object, or when the current
            // negotiated object is one of the matches.  This handles names
            // such as “small bottled water” without silently selecting the
            // neighbouring standard bottle.
            var match = ResolveUniqueItem(candidates, new[] { requestedName }, contextKey);
            if (match is not null)
                return match;
        }

        if (contextKey > 0)
        {
            // The active customer-side negotiated item is a safe fallback
            // only for a generic/omitted reference.  An explicit name that
            // does not describe it must remain unresolved.
            if (string.IsNullOrWhiteSpace(requestedName) ||
                IsGenericItemReference(requestedName))
            {
                var contextItem = candidates.FirstOrDefault(item =>
                    SafeRuntimeKey(item) == contextKey);
                if (contextItem is not null)
                    return contextItem;
            }
        }

        return string.IsNullOrWhiteSpace(requestedName)
            ? ResolveUniqueItem(candidates, Array.Empty<string>(), 0)
            : null;
    }

    private static List<GameItem> FindPlayerItems(
        PlayerStore playerStore,
        CounterOwnershipSnapshot ownership,
        BarterProposal proposal)
    {
        var items = new List<GameItem>();
        foreach (var item in ownership.PlayerCounterItems)
        {
            if (item is not null &&
                ownership.IsPlayerItem(item) &&
                !items.Any(x => x.uniqueId == item.uniqueId))
            {
                items.Add(item);
            }
        }

        var currentNegotiatedItem = playerStore.currentNegociatedItem;
        if (currentNegotiatedItem is not null &&
            ownership.IsPlayerItem(currentNegotiatedItem) &&
            !items.Any(x => x.uniqueId == currentNegotiatedItem.uniqueId))
        {
            items.Add(currentNegotiatedItem);
        }

        var keys = proposal.RequiredPlayerItemKeys
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Append(proposal.RequiredPlayerItemKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var names = proposal.RequiredPlayerItemNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Append(proposal.RequiredPlayerItemName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var runtimeKeys = proposal.RequiredPlayerItemRuntimeKeys
            .Where(key => key > 0)
            .Append(proposal.RequiredPlayerItemRuntimeKey ?? 0)
            .Where(key => key > 0)
            .Distinct()
            .ToList();

        var selected = new List<GameItem>();
        var unresolvedReference = false;
        foreach (var runtimeKey in runtimeKeys)
        {
            var match = items.FirstOrDefault(item => SafeRuntimeKey(item) == runtimeKey);
            if (match is null)
            {
                unresolvedReference = true;
                continue;
            }
            selected.Add(match);
        }
        foreach (var key in keys.Concat(names))
        {
            var preferredKey = currentNegotiatedItem is not null &&
                               (IsGenericItemReference(key) ||
                                MatchesName(currentNegotiatedItem, key))
                ? SafeRuntimeKey(currentNegotiatedItem)
                : 0;
            var match = ResolveUniqueItem(items, new[] { key }, preferredKey);
            if (match is null)
            {
                unresolvedReference = true;
                continue;
            }

            if (!selected.Any(x => SafeRuntimeKey(x) == SafeRuntimeKey(match)))
            {
                // If the proposal supplied both a runtime key and a textual
                // discriminator, they must identify the same object.  This
                // prevents a stale name from widening a precise selection.
                if (runtimeKeys.Count > 0)
                    unresolvedReference = true;
                else
                    selected.Add(match);
            }
        }

        // Never turn a partially understood multi-item request into a
        // different barter.  A later caller can still use the current
        // negotiated item when no explicit reference was supplied.
        if (unresolvedReference)
            return new List<GameItem>();

        if (selected.Count == 0 && keys.Count == 0 && names.Count == 0 && runtimeKeys.Count == 0)
        {
            // When the player says “use this to trade”, the native
            // negotiated item is a stronger reference than list order.  This
            // permits a multi-item counter while still selecting one exact
            // object; if there is no current item, accept only one candidate.
            if (currentNegotiatedItem is not null &&
                items.Any(item => SafeRuntimeKey(item) == SafeRuntimeKey(currentNegotiatedItem)))
            {
                selected.Add(currentNegotiatedItem);
            }
            else if (items.Count == 1)
            {
                selected.Add(items[0]);
            }
        }
        return selected;
    }

    /// <summary>
    /// Resolves a textual item reference without relying on collection order.
    /// Exact identifiers/display names win; fuzzy matching is permitted only
    /// when it resolves to one runtime object (or the active negotiated key
    /// disambiguates the set).  A null result is intentional: callers must
    /// refuse the barter instead of widening it to a whole counter grid.
    /// </summary>
    private static GameItem? ResolveUniqueItem(
        IEnumerable<GameItem> source,
        IEnumerable<string> rawQueries,
        int preferredRuntimeKey)
    {
        var candidates = source
            .Where(item => item is not null && SafeRuntimeKey(item) > 0)
            .GroupBy(SafeRuntimeKey)
            .Select(group => group.First())
            .ToList();
        if (candidates.Count == 0)
            return null;

        var queries = rawQueries
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Select(query => query.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var query in queries)
        {
            // Keep identifier, display name, and internal name as separate
            // tiers.  A broad identifier match must not mask a unique exact
            // display-name match for a size/variant such as small vs standard.
            foreach (var field in new Func<GameItem, string>[]
                     {
                         item => Safe(() => item.identifier, string.Empty),
                         DisplayName,
                         item => Safe(() => item.name, string.Empty)
                     })
            {
                var exact = candidates
                    .Where(item => SameNormalized(field(item), query))
                    .ToList();
                var resolved = SelectPreferred(exact, preferredRuntimeKey);
                if (resolved is not null)
                    return resolved;
            }
        }

        if (queries.Count == 0)
            return candidates.Count == 1 ? candidates[0] : null;

        // Finally allow the existing natural-language matcher, but never
        // choose the first item from an ambiguous set.
        var fuzzy = candidates
            .Where(item => queries.Any(query => MatchesName(item, query)))
            .ToList();
        return SelectPreferred(fuzzy, preferredRuntimeKey);
    }

    private static GameItem? SelectPreferred(
        IReadOnlyCollection<GameItem> matches,
        int preferredRuntimeKey)
    {
        if (matches.Count == 0)
            return null;

        if (preferredRuntimeKey > 0)
        {
            var preferred = matches.FirstOrDefault(item =>
                SafeRuntimeKey(item) == preferredRuntimeKey);
            if (preferred is not null)
                return preferred;
        }

        return matches.Count == 1 ? matches.First() : null;
    }

    private static bool SameNormalized(string? left, string? right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        return normalizedLeft.Length > 0 &&
               normalizedLeft.Equals(normalizedRight, StringComparison.Ordinal);
    }

    private static bool IsGenericItemReference(string value)
    {
        var normalized = Normalize(value);
        return normalized is "这个" or "这件" or "这瓶" or "这个东西" or
            "那个" or "那件" or "那瓶" or "它";
    }

    private static bool MatchesName(GameItem item, string expected)
    {
        var normalized = Normalize(expected);
        if (normalized.Length == 0)
            return false;
        var candidates = new[] { Normalize(item.identifier), Normalize(DisplayName(item)), Normalize(item.name) };
        return candidates.Any(candidate => candidate.Equals(normalized, StringComparison.Ordinal) ||
            (candidate.Length >= 2 && normalized.Length >= 2 &&
             (candidate.Contains(normalized, StringComparison.Ordinal) || normalized.Contains(candidate, StringComparison.Ordinal))));
    }

    private static string DisplayName(GameItem item) =>
        FirstNotBlank(Safe(() => item.GetDisplayName(true), string.Empty), item.name, item.identifier);

    private static string Normalize(string? value) =>
        string.Concat((value ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit));

    private static T Safe<T>(Func<T> getter, T fallback)
    {
        try { return getter(); }
        catch { return fallback; }
    }

    private static BarterApplication Result(bool proposed, bool applied, string message) => new()
    {
        Proposed = proposed,
        Applied = applied,
        Message = message
    };

    private static string FirstNotBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
