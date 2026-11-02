using JsonPropertyName = Newtonsoft.Json.JsonPropertyAttribute;

namespace AIWorldPawnshop.Models;

public sealed class LocalContent
{
    [JsonPropertyName("fallback_replies")]
    public List<LocalReply> FallbackReplies { get; set; } = new();
    [JsonPropertyName("fallback_events")]
    public List<LocalEventTemplate> FallbackEvents { get; set; } = new();

    public static LocalContent CreateDefaults()
    {
        return new LocalContent
        {
            FallbackReplies = new List<LocalReply>
            {
                new LocalReply
                {
                    Topic = "trade",
                    Reply = "先把这笔交易谈妥吧。价钱合适，我不会浪费你的时间。",
                    Mood = "calm",
                    TradeWillingness = 1
                },
                new LocalReply
                {
                    Topic = "personal",
                    Reply = "我记得你。至于更多的事，等我们把眼前的东西说清楚再谈。",
                    Mood = "friendly"
                },
                new LocalReply
                {
                    Topic = "event",
                    Reply = "最近外面的风声确实不太平。价格会受影响，但我还没打算白白吃亏。",
                    Mood = "interested"
                },
                new LocalReply
                {
                    Topic = "off_topic",
                    Reply = "这话题和今天的交易没什么关系。你决定好了吗？",
                    Mood = "impatient",
                    TradeWillingness = -1
                }
            },
            FallbackEvents = new List<LocalEventTemplate>
            {
                new LocalEventTemplate
                {
                    EventKey = "local-medical-delay",
                    Title = "医疗物资运输延误",
                    Summary = "一批医疗物资迟迟没有抵达，附近诊所开始提前寻找替代货源。",
                    ItemGroup = "medical",
                    DurationDays = 2,
                    PriceModifier = 0.08,
                    DemandModifier = 0.15,
                    SupplyModifier = -0.1,
                    NewsImportance = 60
                },
                new LocalEventTemplate
                {
                    EventKey = "local-security-check",
                    Title = "安保巡查临时加强",
                    Summary = "安保部门在周边增加了临时巡查，几类交易开始受到额外关注。",
                    ItemGroup = "security",
                    DurationDays = 1,
                    DemandModifier = -0.05,
                    SupplyModifier = -0.05,
                    NewsImportance = 55
                }
            }
        };
    }
}

public sealed class LocalReply
{
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = "trade";
    [JsonPropertyName("reply")]
    public string Reply { get; set; } = string.Empty;
    [JsonPropertyName("mood")]
    public string Mood { get; set; } = "calm";
    [JsonPropertyName("trade_willingness")]
    public int TradeWillingness { get; set; }
}

public sealed class LocalEventTemplate
{
    [JsonPropertyName("event_key")]
    public string EventKey { get; set; } = string.Empty;
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;
    [JsonPropertyName("item_group")]
    public string ItemGroup { get; set; } = string.Empty;
    [JsonPropertyName("duration_days")]
    public int DurationDays { get; set; } = 1;
    [JsonPropertyName("price_modifier")]
    public double PriceModifier { get; set; }
    [JsonPropertyName("demand_modifier")]
    public double DemandModifier { get; set; }
    [JsonPropertyName("supply_modifier")]
    public double SupplyModifier { get; set; }
    [JsonPropertyName("news_importance")]
    public int NewsImportance { get; set; }
}
