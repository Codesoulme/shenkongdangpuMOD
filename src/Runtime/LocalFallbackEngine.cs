using System.Security.Cryptography;
using System.Text;
using AIWorldPawnshop.Models;

namespace AIWorldPawnshop.Runtime;

public sealed class LocalFallbackEngine
{
    private readonly LocalContent _content;

    public LocalFallbackEngine(LocalContent content)
    {
        _content = content;
    }

    public ConversationResponse Reply(ConversationRequest request)
    {
        if (LooksLikeReturnInvitation(request))
        {
            return new ConversationResponse
            {
                NpcReply = "好，我会按约再来。至于当天什么时候轮到我，得看店里的来客安排。",
                ConversationMode = "continue",
                Topic = "personal",
                TaskRelevance = 0.9,
                Mood = "agreeable",
                Signals = new ConversationSignals { Cooperation = 1, Trust = 1 },
                ReturnVisitProposal = new ReturnVisitProposal
                {
                    Explicit = true,
                    Accepted = true,
                    DueInDays = request.PlayerText.Contains("后天", StringComparison.Ordinal) ? 2 : 1,
                    Reason = "本地降级规则识别到明确回访邀请"
                }
            };
        }

        if (LooksLikeCarriedInventoryRequest(request))
        {
            var carriedItems = request.GameContext.Trade.ClientCarriedItems
                .Where(x => IsCapturedItem(x, "client"))
                .ToList();
            var hasItems = carriedItems.Count > 0;
            var selectedCarried = carriedItems.Count == 1 ? carriedItems[0] : null;
            return new ConversationResponse
            {
                NpcReply = hasItems
                    ? "行，我身上确实还有点东西。先拿一件出来，你看看要不要。"
                    : "这次我身上没有别的东西能出手。",
                ConversationMode = "continue",
                Topic = "trade",
                TaskRelevance = 1,
                Mood = hasItems ? "interested" : "calm",
                CarriedInventoryProposal = new CarriedInventoryProposal
                {
                    Explicit = true,
                    Accepted = hasItems,
                    ItemRuntimeKey = selectedCarried?.RuntimeKey > 0 ? selectedCarried.RuntimeKey : null,
                    ItemKey = selectedCarried?.ItemKey ?? string.Empty,
                    ItemName = selectedCarried?.DisplayName ?? string.Empty,
                    Reason = hasItems ? "本地降级规则确认存在临时随身物" : "顾客没有临时随身物"
                }
            };
        }

        if (LooksLikeBarterRequest(request))
        {
            var offered = request.GameContext.Trade.ClientCarriedItems
                .FirstOrDefault(item => IsCapturedItem(item, "client"));
            var required = request.GameContext.Trade.PlayerOfferedItems
                               .FirstOrDefault(item => IsCapturedItem(item, "player")) ??
                           (request.GameContext.Trade.Item is not null &&
                            IsCapturedItem(request.GameContext.Trade.Item, "player")
                               ? request.GameContext.Trade.Item
                               : null);
            var valid = offered is not null && required is not null &&
                        string.Equals(required.Owner, "player", StringComparison.OrdinalIgnoreCase);
            return new ConversationResponse
            {
                NpcReply = valid
                    ? $"可以。你拿{required!.DisplayName}，换我的{offered!.DisplayName}。"
                    : "先把你要拿来交换的东西摆到柜台上，再说清楚你想换我哪件货。",
                ConversationMode = "continue",
                Topic = "trade",
                TaskRelevance = 1,
                Mood = valid ? "interested" : "inquiring",
                BarterProposal = new BarterProposal
                {
                    Explicit = true,
                    Accepted = valid,
                    ClientItemRuntimeKey = offered?.RuntimeKey,
                    ClientItemName = offered?.DisplayName ?? string.Empty,
                    RequiredPlayerItemKey = valid ? required!.ItemKey : string.Empty,
                    RequiredPlayerItemName = valid ? required!.DisplayName : string.Empty,
                    RequiredPlayerItemKeys = valid ? new List<string> { required!.ItemKey } : new List<string>(),
                    RequiredPlayerItemNames = valid ? new List<string> { required!.DisplayName } : new List<string>(),
                    ValueAssessment = new BarterValueAssessment
                    {
                        Decision = valid ? "accept" : "reject",
                        Reason = valid
                            ? "本地降级规则将由成交前原版价值校验继续复核"
                            : "交换双方物品不明确"
                    },
                    Reason = valid ? "本地降级规则识别到双方真实物品" : "交换双方物品不明确"
                }
            };
        }

        if (ProcurementRequestResolver.LooksLikeFutureProcurement(request.PlayerText) &&
            request.GameContext.Procurement.CanRequestFutureGoods)
        {
            var categories = ProcurementRequestResolver.ParseCategories(request.PlayerText);
            if (categories.Count != 1)
            {
                return new ConversationResponse
                {
                    NpcReply =
                        "你得说具体一点。食品、药品、材料、日用品、武器、工具、模块、门禁卡，还是废品？",
                    ConversationMode = "continue",
                    Topic = "procurement",
                    TaskRelevance = 1,
                    Mood = "inquiring",
                    ProcurementProposal = new ProcurementProposal
                    {
                        Explicit = true,
                        Accepted = false,
                        Category = "none",
                        CategoryConfidence = 0,
                        ClarificationNeeded = true,
                        DueInDays = 1,
                        Reason = "本地降级规则无法把请求唯一映射到一种物资类别"
                    }
                };
            }

            var category = categories[0];
            if (category != "none")
            {
                var access = ProcurementAccessPolicy.Evaluate(request.GameContext, category);
                if (!access.Allowed)
                {
                    return new ConversationResponse
                    {
                        NpcReply =
                            $"{ProcurementRequestResolver.CategoryDisplayName(category)}不在我的路子里。" +
                            "你得找真正有这种渠道的人。",
                        ConversationMode = "continue",
                        Topic = "procurement",
                        TaskRelevance = 1,
                        Mood = "guarded",
                        ProcurementProposal = new ProcurementProposal
                        {
                            Explicit = true,
                            Accepted = false,
                            Category = category,
                            CategoryConfidence = 1,
                            ClarificationNeeded = false,
                            RequestedDescription =
                                ProcurementRequestResolver.CategoryDisplayName(category),
                            DueInDays = 1,
                            PromisedPremiumPercent = 0,
                            Reason = access.Reason
                        }
                    };
                }

                return new ConversationResponse
                {
                    NpcReply = "我可以替你留意这类货。下次过来时，找没找到我都会给你个准话。",
                    ConversationMode = "continue",
                    Topic = "procurement",
                    TaskRelevance = 1,
                    Mood = "interested",
                    Signals = new ConversationSignals
                    {
                        Cooperation = 1,
                        TradeWillingness = 1
                    },
                    MemoryCandidates = new List<MemoryCandidate>
                    {
                        new()
                        {
                            Kind = "promise",
                            Summary = $"玩家委托我寻找{ProcurementRequestResolver.CategoryDisplayName(category)}",
                            Importance = 0.75
                        }
                    },
                    ProcurementProposal = new ProcurementProposal
                    {
                        Explicit = true,
                        Accepted = true,
                        Category = category,
                        CategoryConfidence = 1,
                        ClarificationNeeded = false,
                        RequestedDescription =
                            ProcurementRequestResolver.CategoryDisplayName(category),
                        DueInDays = 1,
                        PromisedPremiumPercent = 0,
                        Reason = "本地降级规则识别到明确的未来找货请求"
                    }
                };
            }
        }

        var topic = ClassifyTopic(request.PlayerText, request.OffTopicStreak);
        var candidates = _content.FallbackReplies
            .Where(x => x.Topic.Equals(topic, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            candidates = _content.FallbackReplies;

        var selected = candidates.Count == 0
            ? new LocalReply { Reply = "客人看了你一眼，示意你继续谈眼前的事。" }
            : candidates[StableIndex($"reply:{request.CharacterKey}:{request.TurnId}", candidates.Count)];

        return new ConversationResponse
        {
            NpcReply = selected.Reply,
            ConversationMode = request.OffTopicStreak >= 5 ? "return_to_task" : "continue",
            Topic = topic,
            TaskRelevance = topic == "off_topic" ? 0.1 : 0.8,
            Mood = selected.Mood,
            Signals = new ConversationSignals
            {
                TradeWillingness = Math.Clamp(selected.TradeWillingness, -2, 2),
                Warmth = selected.Mood == "friendly" ? 1 : 0
            }
        };
    }

    public IReadOnlyList<WorldEventState> BuildDailyEvents(WorldState state, int day)
    {
        if (_content.FallbackEvents.Count == 0)
            return Array.Empty<WorldEventState>();

        var seed = $"event:{state.WorldId}:{day}:{state.RulesVersion}";
        var index = StableIndex(seed, _content.FallbackEvents.Count);
        var templates = new List<LocalEventTemplate> { _content.FallbackEvents[index] };
        if (_content.FallbackEvents.Count > 1 && StableIndex(seed + ":extra", 3) == 0)
            templates.Add(_content.FallbackEvents[(index + 1) % _content.FallbackEvents.Count]);

        return templates.Select(template => new WorldEventState
        {
            EventKey = $"{template.EventKey}-day-{day}",
                Title = template.Title,
                Summary = template.Summary,
                ItemGroup = template.ItemGroup,
                StartDay = day,
                DurationDays = Math.Clamp(template.DurationDays, 1, 3),
                PriceModifier = Math.Clamp(template.PriceModifier, -0.15, 0.15),
                DemandModifier = Math.Clamp(template.DemandModifier, -0.3, 0.3),
                SupplyModifier = Math.Clamp(template.SupplyModifier, -0.3, 0.3),
                NewsImportance = Math.Clamp(template.NewsImportance, 0, 100)
        }).ToList();
    }

    private static int StableIndex(string seed, int exclusiveMax)
    {
        if (exclusiveMax <= 1)
            return 0;
        var hash = RuntimeCompat.HashSha256(Encoding.UTF8.GetBytes(seed));
        var value = ((uint)hash[0] << 24) |
                    ((uint)hash[1] << 16) |
                    ((uint)hash[2] << 8) |
                    hash[3];
        return (int)(value % (uint)exclusiveMax);
    }

    private static string ClassifyTopic(string text, int offTopicStreak)
    {
        if (offTopicStreak >= 3)
            return "off_topic";
        if (text.Contains("报纸", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("新闻", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("巡查", StringComparison.OrdinalIgnoreCase))
            return "event";
        if (text.Contains("你", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("我", StringComparison.OrdinalIgnoreCase))
            return "personal";
        return "trade";
    }

    private static bool LooksLikeReturnInvitation(ConversationRequest request)
    {
        if (!request.Scene.Equals("store", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(request.GameContext.Character.OriginalIdentifier))
            return false;
        var text = request.PlayerText;
        var future = ContainsAny(text, "明天", "后天", "下次", "改天", "过几天");
        var visit = ContainsAny(text, "再来", "回来", "来找我", "来店", "来一趟");
        return future && visit && !ProcurementRequestResolver.LooksLikeFutureProcurement(text);
    }

    private static bool LooksLikeCarriedInventoryRequest(ConversationRequest request)
    {
        var text = request.PlayerText;
        return request.Scene.Equals("store", StringComparison.OrdinalIgnoreCase) &&
               ContainsAny(text, "身上", "随身", "带着", "带的", "还有什么", "别的东西") &&
               ContainsAny(text, "卖", "拿出来", "看看", "交易", "收");
    }

    private static bool LooksLikeBarterRequest(ConversationRequest request) =>
        request.Scene.Equals("store", StringComparison.OrdinalIgnoreCase) &&
        (ContainsAny(request.PlayerText, "交换", "以物易物", "换你", "换这个", "换那个", "跟你换", "给你换") ||
         (request.PlayerText.Contains("换", StringComparison.Ordinal) &&
          ContainsAny(request.PlayerText, "拿", "用", "跟你", "给你")));

    /// <summary>
    /// Validates an item using only the immutable native snapshot captured on
    /// the Unity thread.  The model request and local fallback can complete on
    /// a thread-pool thread, so this helper must never consult PlayerStore or
    /// any other Il2Cpp object.
    /// </summary>
    public static bool IsCapturedItem(NativeItemContext? item, string expectedOwner)
    {
        return item is not null &&
               item.RuntimeKey > 0 &&
               !string.IsNullOrWhiteSpace(expectedOwner) &&
               string.Equals(item.Owner, expectedOwner, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
