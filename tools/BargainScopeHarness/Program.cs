using AIWorldPawnshop.Runtime;
using AIWorldPawnshop.Models;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

var snapshot = new[] { 101, 102 };
var later = new[] { 101, 102, 103 };

Assert(
    CounterOwnershipRules.Classify(
        inPlayerArea: true,
        inClientArea: false,
        ownershipTag: false,
        inPlayerOwnedSet: true) == CounterItemOwner.Client,
    "未拥有标签优先归顾客，不能因合并柜台或旧拥有集合误判为玩家物品");
Assert(
    CounterOwnershipRules.Classify(
        inPlayerArea: true,
        inClientArea: false,
        ownershipTag: true,
        inPlayerOwnedSet: false) == CounterItemOwner.Player,
    "已拥有标签应归玩家");
Assert(
    CounterOwnershipRules.Classify(
        inPlayerArea: false,
        inClientArea: true,
        ownershipTag: null,
        inPlayerOwnedSet: false) == CounterItemOwner.Client,
    "顾客侧柜台位置可作为无标签物品的顾客证据");
Assert(
    CounterOwnershipRules.Classify(
        inPlayerArea: false,
        inClientArea: false,
        ownershipTag: null,
        inPlayerOwnedSet: false) == CounterItemOwner.Unknown,
    "没有证据时必须保持未知，不能猜成玩家物品");

var twoWater = BargainScopeResolver.Resolve("这两瓶水五折", snapshot.Length);
Assert(twoWater.Scope == BargainScopes.CurrentSnapshot, "两瓶水应绑定当前快照");
var twoWaterIds = BargainScopeResolver.SelectTargetIds(
    twoWater.Scope,
    snapshot,
    101,
    matchingIds: snapshot,
    requestedCount: twoWater.RequestedCount);
Assert(twoWaterIds.SetEquals(new[] { 101, 102 }), "前两瓶应享受五折");
Assert(!twoWaterIds.Contains(103), "之后新增的第三瓶不应继承折扣");

var all = BargainScopeResolver.Resolve("全部五折，后面新加的也算", snapshot.Length);
Assert(all.Scope == BargainScopes.AllCurrentAndFuture, "全部约定应包含后续商品");
var allIds = BargainScopeResolver.SelectTargetIds(all.Scope, later, 101);
Assert(allIds.SetEquals(later), "全部约定应覆盖第三瓶");

var ambiguous = BargainScopeResolver.Resolve("给你五折", snapshot.Length);
Assert(ambiguous.NeedsClarification, "多件商品且未说明范围时应追问");
Assert(ambiguous.Scope == BargainScopes.Ambiguous, "模糊约定不能直接改价");

var trade = new NativeTradeContext
{
    Available = true,
    Direction = "player_sells_to_client",
    FinalOfferValue = 34
};
var bareAmount = TradeProposalResolver.Resolve(
    "好，就35.成交",
    trade,
    generated: null);
Assert(bareAmount.Explicit, "成交语境中的裸金额应被识别为明确议价");
Assert(bareAmount.Kind == "ask_markup", "35高于34时应走原版加价接口");
Assert(bareAmount.Percent == 3, "34调整到35应四舍五入为3%的原版议价幅度");
Assert(bareAmount.TargetAmount == 35, "应保留玩家明确提出的目标金额");

var nonPriceNumber = TradeProposalResolver.Resolve(
    "我今年35岁",
    trade,
    generated: null);
Assert(!nonPriceNumber.Explicit, "普通数字不能被误判为报价");

var revokedDiscount = TradeProposalResolver.Resolve(
    "开始你不要铁锈水的话，这捅优质水也不能八折给你了。",
    trade,
    generated: null);
Assert(!revokedDiscount.Explicit, "否定八折不能被解析成可执行折扣");
Assert(revokedDiscount.RevokesPriorPriceChange, "否定八折应标记为撤回既有价格约定");

var offeredDiscount = TradeProposalResolver.Resolve(
    "如果你不要铁锈水，我可以给你八折优质水。",
    trade,
    generated: null);
Assert(offeredDiscount.Explicit && offeredDiscount.Kind == "offer_discount" && offeredDiscount.Percent == 20,
    "条件句中的正向八折仍应被识别为折扣提议");

var askedDiscount = TradeProposalResolver.Resolve(
    "你能不能给我八折？",
    trade,
    generated: null);
Assert(askedDiscount.Explicit && askedDiscount.Kind == "offer_discount" && askedDiscount.Percent == 20,
    "询问能否八折不能被误判成撤回");

var floorPrice = TradeProposalResolver.Resolve(
    "这桶优质水不能低于八折。",
    trade,
    generated: null);
Assert(!floorPrice.Explicit && !floorPrice.RevokesPriorPriceChange,
    "不能低于八折只能阻断自动议价，不能删除既有价格约定");

var generatedDiscount = TradeProposalResolver.Resolve(
    "开始你不要铁锈水的话，这捅优质水也不能八折给你了。",
    trade,
    generated: new TradeProposal
    {
        Kind = "offer_discount",
        Percent = 20,
        Explicit = true,
        Reason = "模型误读"
    });
Assert(!generatedDiscount.Explicit, "否定句不能被模型的正向折扣提案覆盖");

var storeRequest = new ConversationRequest { Scene = "store" };
Assert(ConversationActionResolver.ShouldDismissStoreClient(
    storeRequest,
    new ConversationResponse { NpcReply = "那我先走了，下次再来。" }),
    "顾客明确说要走时应触发原版离店路径");
Assert(!ConversationActionResolver.ShouldDismissStoreClient(
    storeRequest,
    new ConversationResponse { NpcReply = "我不走，我还要看看。" }),
    "顾客明确说不走时不能误触发离店");
Assert(!ConversationActionResolver.ShouldDismissStoreClient(
    storeRequest,
    new ConversationResponse { NpcReply = "我走过去看看。" }),
    "普通移动描述不能被误判成顾客离店");
var phoneRequest = new ConversationRequest { Scene = "phone" };
Assert(ConversationActionResolver.ShouldHangUpPhone(
    phoneRequest,
    new ConversationResponse { NpcReply = "那先挂了，改天再聊。" }),
    "电话中明确结束通话时应触发原版挂断路径");
Assert(!ConversationActionResolver.ShouldHangUpPhone(
    phoneRequest,
    new ConversationResponse { NpcReply = "先别挂，我还有话说。" }),
    "电话中明确要求别挂时不能误触发挂断");

var carriedRequest = new ConversationRequest
{
    Scene = "store",
    PlayerText = "你身上还有什么，拿出来我看看",
    GameContext = new GameConversationContext
    {
        Trade = new NativeTradeContext
        {
            ClientCarriedItems = new List<NativeItemContext>
            {
                new()
                {
                    RuntimeKey = 501,
                    ItemKey = "water_small",
                    Identifier = "water_small",
                    DisplayName = "小瓶装水",
                    Owner = "client"
                }
            }
        }
    }
};
var carriedResponse = new ConversationResponse
{
    NpcReply = "有，我拿出来给你看看。",
    CarriedInventoryProposal = new CarriedInventoryProposal
    {
        Explicit = true,
        Accepted = true
    }
};
ConversationActionResolver.Normalize(carriedRequest, carriedResponse);
Assert(carriedResponse.CarriedInventoryProposal.Accepted &&
       carriedResponse.CarriedInventoryProposal.ItemRuntimeKey == 501 &&
       carriedResponse.CarriedInventoryProposal.ItemName == "小瓶装水",
    "真实顾客随身物应被规范化为唯一运行时物品");

var fabricatedCarried = new ConversationResponse
{
    NpcReply = "有，我拿出来给你看看。",
    CarriedInventoryProposal = new CarriedInventoryProposal
    {
        Explicit = true,
        Accepted = true,
        ItemName = "虚构物品"
    }
};
ConversationActionResolver.Normalize(
    new ConversationRequest
    {
        Scene = "store",
        PlayerText = carriedRequest.PlayerText,
        GameContext = new GameConversationContext()
    },
    fabricatedCarried);
Assert(!fabricatedCarried.CarriedInventoryProposal.Accepted,
    "没有真实顾客库存时不能接受随身物品展示");

var ambiguousCarried = new ConversationRequest
{
    Scene = "store",
    PlayerText = carriedRequest.PlayerText,
    GameContext = new GameConversationContext
    {
        Trade = new NativeTradeContext
        {
            ClientCarriedItems = new List<NativeItemContext>
            {
                new() { RuntimeKey = 601, ItemKey = "water_small", DisplayName = "小瓶装水", Owner = "client" },
                new() { RuntimeKey = 602, ItemKey = "water_large", DisplayName = "大瓶装水", Owner = "client" }
            }
        }
    }
};
var ambiguousCarriedResponse = new ConversationResponse
{
    NpcReply = "有，我拿一件给你看看。",
    CarriedInventoryProposal = new CarriedInventoryProposal
    {
        Explicit = true,
        Accepted = true
    }
};
ConversationActionResolver.Normalize(ambiguousCarried, ambiguousCarriedResponse);
Assert(!ambiguousCarriedResponse.CarriedInventoryProposal.Accepted,
    "多件顾客随身物且没有唯一指定时不能擅自挑选");

var wholesaleRequest = new ConversationRequest
{
    Scene = "store",
    GameContext = new GameConversationContext
    {
        Trade = new NativeTradeContext
        {
            IsOfferWholesale = true,
            WholesaleDiscountPercent = 20,
            FinalOfferValue = 80
        }
    }
};
var wrongWholesale = new ConversationResponse { NpcReply = "批发按九折给你。" };
ConversationActionResolver.Normalize(wholesaleRequest, wrongWholesale);
Assert(wrongWholesale.NpcReply.Contains("收银台", StringComparison.Ordinal),
    "人物报出与原版批发字段不符的折扣时应回退到收银台报价");
var matchingWholesale = new ConversationResponse { NpcReply = "批发按八折给你。" };
ConversationActionResolver.Normalize(wholesaleRequest, matchingWholesale);
Assert(matchingWholesale.NpcReply.Contains("八折", StringComparison.Ordinal),
    "人物报出与原版批发字段一致的折扣时应保留");
var wrongWholesaleAmount = new ConversationResponse { NpcReply = "批发价70信用点。" };
ConversationActionResolver.Normalize(wholesaleRequest, wrongWholesaleAmount);
Assert(wrongWholesaleAmount.NpcReply.Contains("收银台", StringComparison.Ordinal),
    "人物报出与原版收银台不符的批发金额时应回退到收银台报价");

var departureReply = new ConversationResponse { NpcReply = "不买了。" };
ConversationActionResolver.EnsureNaturalExitReply(storeRequest, departureReply);
Assert(departureReply.NpcReply.Contains("先走", StringComparison.Ordinal),
    "顾客离店回复缺少结束语时应补自然告别");
var stayReply = new ConversationResponse { NpcReply = "我不走，我还要看看。" };
ConversationActionResolver.EnsureNaturalExitReply(storeRequest, stayReply);
Assert(stayReply.NpcReply == "我不走，我还要看看。",
    "明确表示不离店时不能追加告别语");

Assert(
    LocalFallbackEngine.IsCapturedItem(
        new NativeItemContext { Owner = "client", RuntimeKey = 99 },
        "client"),
    "降级交换应接受对话上下文中已捕获的顾客物品");
Assert(
    !LocalFallbackEngine.IsCapturedItem(
        new NativeItemContext { Owner = "unknown", RuntimeKey = 99 },
        "player"),
    "归属未知的物品不能被降级逻辑当作玩家物品");

var fallbackRequest = new ConversationRequest
{
    Scene = "store",
    PlayerText = "交换这件物品",
    GameContext = new GameConversationContext
    {
        Trade = new NativeTradeContext
        {
            ClientCarriedItems = new List<NativeItemContext>
            {
                new() { Owner = "client", RuntimeKey = 99, DisplayName = "顾客物品" }
            },
            PlayerOfferedItems = new List<NativeItemContext>
            {
                new() { Owner = "player", RuntimeKey = 101, ItemKey = "player-item", DisplayName = "玩家物品" }
            }
        }
    }
};
var fallbackResponse = new LocalFallbackEngine(LocalContent.CreateDefaults()).Reply(fallbackRequest);
Assert(
    fallbackResponse.BarterProposal.Accepted &&
    fallbackResponse.BarterProposal.ClientItemRuntimeKey == 99 &&
    fallbackResponse.BarterProposal.RequiredPlayerItemKey == "player-item",
    "本地降级交换必须只使用已捕获的交易上下文，不能回读 PlayerStore");

var preciseBarter = new BarterProposal
{
    Explicit = true,
    Accepted = true,
    ClientItemRuntimeKey = 99,
    RequiredPlayerItemRuntimeKey = 101,
    RequiredPlayerItemRuntimeKeys = new List<int> { 101, 102 }
};
var preciseJson = System.Text.Json.JsonSerializer.Serialize(preciseBarter);
Assert(preciseJson.Contains("required_player_item_runtime_key", StringComparison.Ordinal),
    "交换提案必须保留玩家侧单件 runtime key");
Assert(preciseJson.Contains("required_player_item_runtime_keys", StringComparison.Ordinal),
    "交换提案必须保留玩家侧多件 runtime key");
Assert(
    !ConversationActionResolver.NaturalBarterFailureReply.Contains("接口", StringComparison.Ordinal) &&
    !ConversationActionResolver.NaturalBarterFailureReply.Contains("runtime", StringComparison.OrdinalIgnoreCase) &&
    ConversationActionResolver.NaturalBarterFailureReply.Contains("交换", StringComparison.Ordinal),
    "已接受但无法执行的交换必须使用自然拒绝文本，不得暴露技术原因");

var fairValueRequest = new ConversationRequest
{
    Scene = "store",
    PlayerText = "交换这件物品",
    GameContext = new GameConversationContext
    {
        Trade = new NativeTradeContext
        {
            ClientCarriedItems = new List<NativeItemContext>
            {
                new() { Owner = "client", RuntimeKey = 201, DisplayName = "顾客物品", CurrentValue = 10 }
            },
            PlayerOfferedItems = new List<NativeItemContext>
            {
                new() { Owner = "player", RuntimeKey = 202, DisplayName = "玩家物品", CurrentValue = 10 }
            }
        }
    }
};
var fairValueResponse = new ConversationResponse
{
    NpcReply = "可以交换。",
    BarterProposal = new BarterProposal
    {
        Explicit = true,
        Accepted = true,
        ClientItemRuntimeKey = 201,
        RequiredPlayerItemRuntimeKey = 202,
        ValueAssessment = new BarterValueAssessment
        {
            Decision = "accept",
            Reason = "双方价值相当"
        }
    }
};
var fairValueCheck = BarterValuePolicy.Evaluate(fairValueRequest, fairValueResponse);
Assert(fairValueCheck.Allowed, "价值相当的交换应允许进入原版流程");
Assert(fairValueCheck.ClientTotalValue == 10,
    "交换安全门应记录顾客一侧的原版价值");

var mixedResponse = new ConversationResponse
{
    NpcReply = "可以交换，我再补5点。",
    BarterProposal = new BarterProposal
    {
        Explicit = true,
        Accepted = true,
        ClientItemRuntimeKey = 201,
        RequiredPlayerItemRuntimeKey = 202,
        ValueAssessment = new BarterValueAssessment
        {
            Decision = "accept",
            Reason = "顾客明确需要玩家物品"
        }
    },
    WalletTransfer = new WalletTransferProposal
    {
        Explicit = true,
        Accepted = true,
        Amount = 5,
        Source = "client_budget"
    }
};
var mixedValueCheck = BarterValuePolicy.Evaluate(fairValueRequest, mixedResponse);
Assert(mixedValueCheck.Allowed && mixedValueCheck.ClientTotalValue == 15,
    "混合交换应把顾客补付款计入顾客给出的一侧");

var unfairValueResponse = new ConversationResponse
{
    NpcReply = "可以交换。",
    BarterProposal = new BarterProposal
    {
        Explicit = true,
        Accepted = true,
        ClientItemRuntimeKey = 201,
        RequiredPlayerItemRuntimeKey = 202,
        ValueAssessment = new BarterValueAssessment
        {
            Decision = "accept",
            Reason = "顾客想要这件东西"
        }
    }
};
fairValueRequest.GameContext.Trade.ClientCarriedItems[0].CurrentValue = 100;
var unfairValueCheck = BarterValuePolicy.Evaluate(fairValueRequest, unfairValueResponse);
Assert(!unfairValueCheck.Allowed,
    "明显低于顾客给出价值的交换必须在成交前拒绝");
Assert(unfairValueCheck.Message.Contains("价值", StringComparison.Ordinal),
    "价值不合理时应给出价值拒绝原因");

var replayEngine = new LocalFallbackEngine(LocalContent.CreateDefaults());
var replayClientItem = new NativeItemContext
{
    RuntimeKey = 701,
    ItemKey = "water_small",
    Identifier = "water_small",
    DisplayName = "小瓶装水",
    Owner = "client",
    CurrentValue = 10,
    Quantity = 1
};
var replayPlayerItem = new NativeItemContext
{
    RuntimeKey = 702,
    ItemKey = "paper",
    Identifier = "paper",
    DisplayName = "报纸",
    Owner = "player",
    CurrentValue = 10,
    Quantity = 1
};

ConversationRequest ReplayRequest(
    string text,
    NativeTradeContext? trade = null,
    bool namedCharacter = false,
    string scene = "store") =>
    new()
    {
        ConversationId = "replay",
        TurnId = 1,
        CharacterKey = "replay-client",
        CharacterDisplayName = "回放顾客",
        Scene = scene,
        PlayerText = text,
        GameContext = new GameConversationContext
        {
            Character = new NativeCharacterContext
            {
                Key = "replay-client",
                OriginalIdentifier = namedCharacter ? "native-replay-client" : string.Empty,
                DisplayName = "回放顾客",
                Intent = "SELLNBUY"
            },
            Trade = trade ?? new NativeTradeContext()
        }
    };

string ReplayActions(
    ConversationRequest request,
    ConversationResponse response,
    bool normalize = true)
{
    if (normalize && !response.BarterProposal.Explicit)
        ConversationActionResolver.Normalize(request, response);

    var actions = new List<string>();
    if (response.CarriedInventoryProposal.Explicit)
    {
        actions.Add(response.CarriedInventoryProposal.Accepted
            ? $"RevealCarriedInventory(runtime={response.CarriedInventoryProposal.ItemRuntimeKey})"
            : "RejectCarriedInventory");
    }
    if (response.BarterProposal.Explicit)
    {
        var valueCheck = BarterValuePolicy.Evaluate(request, response);
        actions.Add(valueCheck.Allowed
            ? "OpenNativeBarter(deferred-settlement)"
            : $"RejectBarter({valueCheck.Message})");
    }
    if (response.ProcurementProposal.Explicit)
        actions.Add(response.ProcurementProposal.Accepted
            ? $"ScheduleProcurement({response.ProcurementProposal.Category})"
            : "RejectProcurement");
    if (response.ReturnVisitProposal.Explicit)
        actions.Add(response.ReturnVisitProposal.Accepted
            ? $"ScheduleReturnVisit(+{response.ReturnVisitProposal.DueInDays}d)"
            : "RejectReturnVisit");
    if (response.WalletTransfer.Explicit)
        actions.Add(response.WalletTransfer.Accepted
            ? $"QueueWalletTransfer({response.WalletTransfer.Amount})"
            : "RejectWalletTransfer");
    if (ConversationActionResolver.ShouldDismissStoreClient(request, response))
    {
        ConversationActionResolver.EnsureNaturalExitReply(request, response);
        actions.Add("DismissCurrentClient(after-reply)");
    }
    if (ConversationActionResolver.ShouldHangUpPhone(request, response))
    {
        ConversationActionResolver.EnsureNaturalExitReply(request, response);
        actions.Add("StopPhoneCall(after-reply)");
    }
    return actions.Count == 0 ? "NoNativeAction" : string.Join(" + ", actions);
}

void ReplayFallbackVariants(
    string group,
    IReadOnlyList<string> phrases,
    Func<string, ConversationRequest> requestFactory,
    Func<ConversationResponse, bool> expected)
{
    var actionSet = new HashSet<string>(StringComparer.Ordinal);
    foreach (var phrase in phrases)
    {
        var request = requestFactory(phrase);
        var response = replayEngine.Reply(request);
        var actions = ReplayActions(request, response);
        Console.WriteLine($"[REPLAY] {group} | 玩家：{phrase} | NPC：{response.NpcReply} | 后端：{actions}");
        Assert(expected(response), $"{group} 未识别：{phrase}");
        actionSet.Add(actions);
    }
    Assert(actionSet.Count == 1, $"{group} 的不同说法产生了不同后端动作：{string.Join(" / ", actionSet)}");
}

ReplayFallbackVariants(
    "随身库存-有物品",
    new[]
    {
        "你身上还有什么货？拿出来看看。",
        "随身带了别的东西吗？我想看看。",
        "除了这件，你还带的东西里有能卖的吗？"
    },
    phrase => ReplayRequest(
        phrase,
        new NativeTradeContext
        {
            ClientCarriedItems = new List<NativeItemContext> { replayClientItem }
        }),
    response => response.CarriedInventoryProposal.Accepted &&
               response.CarriedInventoryProposal.ItemRuntimeKey == 701);

var departureActions = new HashSet<string>(StringComparer.Ordinal);
foreach (var phrase in new[] { "那我先走了。", "不买了，我离开。", "这次不谈了，下次再来。" })
{
    var request = ReplayRequest("结束这次交谈。");
    var response = new ConversationResponse { NpcReply = phrase };
    var actions = ReplayActions(request, response, normalize: false);
    Console.WriteLine($"[REPLAY] 离店-明确告别 | 玩家：{request.PlayerText} | NPC：{response.NpcReply} | 后端：{actions}");
    Assert(actions == "DismissCurrentClient(after-reply)", $"明确告别未触发离店：{phrase}");
    Assert(response.NpcReply.Contains("先走", StringComparison.Ordinal) ||
           response.NpcReply.Contains("下次", StringComparison.Ordinal) ||
           response.NpcReply.Contains("回头", StringComparison.Ordinal),
        $"明确离店回复没有自然结束语：{phrase}");
    departureActions.Add(actions);
}
Assert(departureActions.Count == 1, "离店的不同说法没有落到同一个后端动作");

ReplayFallbackVariants(
    "回访",
    new[] { "明天再来一趟吧。", "过几天回来看看。", "改天来店里聊。" },
    phrase => ReplayRequest(phrase, namedCharacter: true),
    response => response.ReturnVisitProposal.Accepted);

ReplayFallbackVariants(
    "未来找货",
    new[] { "下次帮我找点医疗用品。", "以后带些药品过来。", "下次能不能替我弄到医疗物资？" },
    phrase =>
    {
        var request = ReplayRequest(
            phrase,
            new NativeTradeContext
            {
                ClientCarriedItems = new List<NativeItemContext>()
            });
        request.GameContext.Procurement.CanRequestFutureGoods = true;
        request.GameContext.Character.Key = "doctor-client";
        request.GameContext.Character.OriginalIdentifier = "doctor-client";
        return request;
    },
    response => response.ProcurementProposal.Explicit);

var barterVariants = new[]
{
    "用这份报纸换你的小瓶装水。",
    "我拿报纸跟你换小瓶装水。",
    "以物易物，用报纸换水。"
};
var barterActions = new HashSet<string>(StringComparer.Ordinal);
foreach (var phrase in barterVariants)
{
    var request = ReplayRequest(
        phrase,
        new NativeTradeContext
        {
            ClientCarriedItems = new List<NativeItemContext> { replayClientItem },
            PlayerOfferedItems = new List<NativeItemContext> { replayPlayerItem }
        });
    var response = replayEngine.Reply(request);
    var actions = ReplayActions(request, response, normalize: false);
    Console.WriteLine($"[REPLAY] 交换-等值 | 玩家：{phrase} | NPC：{response.NpcReply} | 后端：{actions}");
    Assert(response.BarterProposal.Accepted, $"交换口语未识别：{phrase}");
    barterActions.Add(actions);
}
Assert(barterActions.Count == 1, "交换的不同说法没有落到同一个后端动作");

var noCarriedRequest = ReplayRequest(
    "你随身还有能卖的吗？拿出来看看。",
    new NativeTradeContext());
var noCarriedResponse = replayEngine.Reply(noCarriedRequest);
var noCarriedActions = ReplayActions(noCarriedRequest, noCarriedResponse);
Console.WriteLine($"[REPLAY] 随身库存-无物品 | 玩家：{noCarriedRequest.PlayerText} | NPC：{noCarriedResponse.NpcReply} | 后端：{noCarriedActions}");
Assert(noCarriedActions == "RejectCarriedInventory",
    "没有真实随身物品时不能进入展示执行器");

var ambiguousRequest = ReplayRequest(
    "你身上还有什么，拿出来看看。",
    new NativeTradeContext
    {
        ClientCarriedItems = new List<NativeItemContext>
        {
            replayClientItem,
            new()
            {
                RuntimeKey = 703,
                ItemKey = "water_large",
                DisplayName = "大瓶装水",
                Owner = "client",
                CurrentValue = 20
            }
        }
    });
var ambiguousResponse = replayEngine.Reply(ambiguousRequest);
var ambiguousActions = ReplayActions(ambiguousRequest, ambiguousResponse);
Console.WriteLine($"[REPLAY] 随身库存-多件未指定 | 玩家：{ambiguousRequest.PlayerText} | NPC：{ambiguousResponse.NpcReply} | 后端：{ambiguousActions}");
Assert(ambiguousActions == "RejectCarriedInventory",
    "多件随身物未指定时不能擅自摆第一件");

var wholesalePhrases = new[]
{
    "批发价按九折算吗？",
    "要是批量买，给我九折？",
    "批发就按90%价格吧。"
};
foreach (var phrase in wholesalePhrases)
{
    var request = ReplayRequest(phrase);
    request.GameContext.Trade.IsOfferWholesale = true;
    request.GameContext.Trade.WholesaleDiscountPercent = 20;
    request.GameContext.Trade.FinalOfferValue = 80;
    var response = new ConversationResponse { NpcReply = "批发按九折给你。" };
    var actions = ReplayActions(request, response);
    Console.WriteLine($"[REPLAY] 批发价-不一致 | 玩家：{phrase} | NPC：{response.NpcReply} | 后端：{actions}");
    Assert(response.NpcReply.Contains("收银台", StringComparison.Ordinal),
        $"批发价不一致时未回退收银台：{phrase}");
}

var mixedRequest = ReplayRequest(
    "用报纸换小瓶装水，再补我5信用点。",
    new NativeTradeContext
    {
        ClientCarriedItems = new List<NativeItemContext> { replayClientItem },
        PlayerOfferedItems = new List<NativeItemContext> { replayPlayerItem }
    });
var mixedReplayResponse = new ConversationResponse
{
    NpcReply = "可以交换，我再补5信用点。",
    BarterProposal = new BarterProposal
    {
        Explicit = true,
        Accepted = true,
        ClientItemRuntimeKey = 701,
        RequiredPlayerItemRuntimeKey = 702,
        ValueAssessment = new BarterValueAssessment
        {
            Decision = "accept",
            Reason = "双方价值相当"
        }
    },
    WalletTransfer = new WalletTransferProposal
    {
        Explicit = true,
        Accepted = true,
        Amount = 5,
        Source = "client_budget"
    }
};
var mixedReplayActions = ReplayActions(mixedRequest, mixedReplayResponse, normalize: false);
Console.WriteLine($"[REPLAY] 混合交换 | 玩家：{mixedRequest.PlayerText} | NPC：{mixedReplayResponse.NpcReply} | 后端：{mixedReplayActions}");
Assert(mixedReplayActions.Contains("OpenNativeBarter", StringComparison.Ordinal) &&
       mixedReplayActions.Contains("QueueWalletTransfer(5)", StringComparison.Ordinal),
    "混合交换没有同时保留交换和补付款执行动作");

var phonePhrases = new[] { "那先挂了。", "今天就聊到这里，拜拜。", "我不打扰了，结束通话吧。" };
foreach (var phrase in phonePhrases)
{
    var request = ReplayRequest(phrase, scene: "phone");
    var response = new ConversationResponse { NpcReply = phrase };
    var actions = ReplayActions(request, response);
    Console.WriteLine($"[REPLAY] 电话结束 | 玩家：{phrase} | NPC：{response.NpcReply} | 后端：{actions}");
    Assert(actions == "StopPhoneCall(after-reply)", $"电话结束未触发挂断：{phrase}");
}

Console.WriteLine("Bargain scope regression harness passed.");
