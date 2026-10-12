using System.Diagnostics;
using System.Text.Json;
using AIWorldPawnshop.Model;
using AIWorldPawnshop.Models;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: ModelLatencyHarness <Config.json> [protocol] [casual|gm]");
    return 2;
}

var config = JsonSerializer.Deserialize<ModConfig>(
    await File.ReadAllTextAsync(args[0]),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException("Config.json could not be read.");
config.Normalize();
var profile = config.GetActiveProfile().Clone();
if (args.Length >= 2)
    profile.Protocol = args[1];
profile.Normalize();

var mode = args.Length >= 3 ? args[2] : "casual";
using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var provider = new OpenAiCompatibleProvider(
    http,
    profile,
    message => Console.WriteLine("PERF " + message));
var request = BuildRepresentativeRequest();
if (mode.Equals("full", StringComparison.OrdinalIgnoreCase))
    request.PlayerText = "我用旧收音机换你的小瓶装水，你再补给我五信用点，可以吗？";
var requestJson = JsonSerializer.Serialize(request);

Console.WriteLine(JsonSerializer.Serialize(new
{
    mode,
    protocol = profile.Protocol,
    model = profile.Model,
    request_chars = requestJson.Length,
    configured_output_tokens = profile.MaxOutputTokens
}));

var watch = Stopwatch.StartNew();
ConversationResponse? response;
if (mode.Equals("gm", StringComparison.OrdinalIgnoreCase))
{
    var candidate = new ConversationResponse
    {
        NpcReply = "行，我会请那位警员最近多留意你店铺附近。",
        Topic = "security",
        ThirdPartyTask = new ThirdPartyTaskProposal
        {
            Explicit = true,
            Accepted = true,
            TargetCharacterKey = "officer_01",
            TargetDisplayName = "巡警",
            Purpose = "最近三天加强店铺附近巡逻",
            DueInDays = 3
        }
    };
    response = await provider.CompleteGmAsync(request, candidate, CancellationToken.None);
}
else
{
    response = await provider.CompleteAsync(request, CancellationToken.None);
}
watch.Stop();

Console.WriteLine(JsonSerializer.Serialize(new
{
    elapsed_ms = watch.ElapsedMilliseconds,
    parsed = response is not null,
    reply_chars = response?.NpcReply.Length ?? 0,
    topic = response?.Topic,
    barter_explicit = response?.BarterProposal.Explicit ?? false,
    barter_accepted = response?.BarterProposal.Accepted ?? false,
    wallet_explicit = response?.WalletTransfer.Explicit ?? false,
    wallet_accepted = response?.WalletTransfer.Accepted ?? false,
    wallet_amount = response?.WalletTransfer.Amount ?? 0,
    structured_action = response is not null &&
        (response.TradeProposal.Explicit ||
         response.WalletTransfer.Explicit ||
         response.ProcurementProposal.Explicit ||
         response.ReturnVisitProposal.Explicit ||
         response.CarriedInventoryProposal.Explicit ||
         response.BarterProposal.Explicit ||
         response.ThirdPartyTask.Explicit ||
         response.WorldModifications.Count > 0)
}));
if (response is not null)
    Console.WriteLine(JsonSerializer.Serialize(response));
return response is null ? 1 : 0;

static ConversationRequest BuildRepresentativeRequest()
{
    var carried = new List<NativeItemContext>
    {
        Item(1101, "water_small", "小瓶装水", "client", 12),
        Item(1102, "water_standard", "标准瓶装水", "client", 20),
        Item(1103, "newspaper", "今日报纸", "client", 8)
    };
    var offered = new List<NativeItemContext>
    {
        Item(2101, "old_radio", "旧收音机", "player", 35),
        Item(2102, "battery", "电池", "player", 10)
    };
    return new ConversationRequest
    {
        ConversationId = "latency-harness",
        TurnId = 4,
        CharacterKey = "customer_01",
        CharacterDisplayName = "过路客",
        FactionKey = "civilian",
        Scene = "store",
        PlayerText = "你好，今天过得怎么样？",
        RecentHistory = new List<string>
        {
            "你：最近城里不太平。", "过路客：晚上确实得小心。",
            "你：你带了什么东西？", "过路客：几瓶水和一份报纸。",
            "你：先随便聊聊。", "过路客：行，不过我赶时间。"
        },
        GameContext = new GameConversationContext
        {
            Character = new NativeCharacterContext
            {
                Key = "customer_01", DisplayName = "过路客", OriginalIdentifier = "customer_01",
                FactionKey = "civilian", Intent = "sell", CanReturn = true, VisitCount = 2,
                Tags = new List<string> { "customer", "traveller" }
            },
            OriginalCharacterProfile = new OriginalCharacterProfileContext
            {
                CanonicalIdentity = "一名来当铺出售随身物品的普通旅客",
                RoleAndBackground = new List<string> { "经常往返港区", "对本地物价略有了解" },
                CurrentStoryState = new List<string> { "正在柜台前和店主交谈" },
                ImmutableConstraints = new List<string> { "不能知道未公开的秘密", "不能替警方作决定" },
                KnowledgeBoundaries = new List<string> { "只知道自己携带的物品和亲历事件" },
                SpeechStyleSamples = new List<string> { "有话直说，句子简短。" }
            },
            Trade = new NativeTradeContext
            {
                Available = true, Phase = "inspection", Direction = "client_sells",
                ClientCash = 42, ClientBudget = 42, ClientBuyPriceModifier = 0,
                ClientSellPriceModifier = 0, ClientImpatience = 1,
                ClientInventoryCount = carried.Count, ClientCarriedItems = carried,
                PlayerOfferedItems = offered, Item = carried[0],
                ClientCanInspectItem = true, ClientHasBudget = true,
                Bargain = new NativeBargainContext { CanOfferDiscount = true, CanOfferMarkup = true }
            },
            OriginalStoryFacts = new List<string> { "这是该顾客第二次来店", "顾客尚未完成本次交易" },
            OriginalDialogueContext = new List<string> { "顾客愿意出售自己带来的饮用水" },
            Relationship = new RelationshipContext
            {
                PersonalRelationship = 2, ModFactionReputation = 1, InteractionCount = 3,
                MemorySummaries = new List<string> { "上次交易顺利", "店主曾提醒他注意盗窃" },
                FactionMemorySummaries = new List<string> { "普通市民对店铺印象中性" }
            },
            Procurement = new ProcurementConversationContext
            {
                CanRequestFutureGoods = true,
                SupportedCategories = new List<string> { "daily", "electronics", "medical" },
                AllowedCategories = new List<string> { "daily" },
                RarityBasis = "当前关系仅允许普通日用品"
            },
            ActiveWorldEvents = new List<string> { "港区近日有零星盗窃传闻" },
            ThirdPartyTasks = new List<string>(),
            KnownCharacters = new List<KnownCharacterContext>
            {
                new() { Key = "officer_01", DisplayName = "巡警", FactionKey = "police", CanReturn = true },
                new() { Key = "merchant_02", DisplayName = "行商", FactionKey = "civilian", CanReturn = true }
            },
            GmKnowledge = new List<string> { "security.patrol_modifier 可临时调整并在期限后恢复" }
        }
    };
}

static NativeItemContext Item(int runtimeKey, string key, string name, string owner, long value) => new()
{
    RuntimeKey = runtimeKey,
    ItemKey = key,
    Identifier = key,
    InternalName = key,
    DisplayName = name,
    Owner = owner,
    Quantity = 1,
    BaseValue = value,
    CurrentValue = value,
    NegotiatedValue = value,
    ItemTypes = new List<string> { "daily" }
};
