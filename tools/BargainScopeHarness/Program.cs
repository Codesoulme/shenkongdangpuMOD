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

var storeRequest = new ConversationRequest { Scene = "store" };
Assert(ConversationActionResolver.ShouldDismissStoreClient(
    storeRequest,
    new ConversationResponse { NpcReply = "那我先走了，下次再来。" }),
    "顾客明确说要走时应触发原版离店路径");
Assert(!ConversationActionResolver.ShouldDismissStoreClient(
    storeRequest,
    new ConversationResponse { NpcReply = "我不走，我还要看看。" }),
    "顾客明确说不走时不能误触发离店");
var phoneRequest = new ConversationRequest { Scene = "phone" };
Assert(ConversationActionResolver.ShouldHangUpPhone(
    phoneRequest,
    new ConversationResponse { NpcReply = "那先挂了，改天再聊。" }),
    "电话中明确结束通话时应触发原版挂断路径");
Assert(!ConversationActionResolver.ShouldHangUpPhone(
    phoneRequest,
    new ConversationResponse { NpcReply = "先别挂，我还有话说。" }),
    "电话中明确要求别挂时不能误触发挂断");

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

Console.WriteLine("Bargain scope regression harness passed.");
