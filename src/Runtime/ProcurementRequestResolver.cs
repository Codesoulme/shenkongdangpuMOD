using System.Text.RegularExpressions;
using AIWorldPawnshop.Models;

namespace AIWorldPawnshop.Runtime;

internal sealed class ProcurementResolution
{
    public bool Explicit { get; init; }
    public bool Accepted { get; init; }
    public string Category { get; init; } = "none";
    public string RequestedDescription { get; init; } = string.Empty;
    public int DueInDays { get; init; } = 1;
    public int PromisedPremiumPercent { get; init; }
    public int AccessLevel { get; init; }
    public int MaxRarityTier { get; init; }
    public int AccessSearchModifier { get; init; }
    public string AccessReason { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

internal static class ProcurementRequestResolver
{
    private static readonly Regex PremiumRegex = new(
        @"(?<value>\d{1,3})\s*%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DaysRegex = new(
        @"(?<value>[1-3一二两三])\s*天后",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static readonly IReadOnlyList<string> SupportedCategoryDescriptions = new[]
    {
        "food（食品、饮料、口粮和水）",
        "medical（药品和医疗用品）",
        "material（原料、矿物和建材）",
        "household（日用品和家居物资）",
        "weapon（枪械、刀具和其他武器）",
        "tool（工具和常用设备）",
        "module（机械模块和芯片）",
        "access_card（门禁卡和钥匙卡）",
        "junk（废品和杂物）"
    };

    public static ProcurementResolution Resolve(
        ConversationRequest request,
        ConversationResponse response)
    {
        if (!request.Scene.Equals("store", StringComparison.OrdinalIgnoreCase))
            return None("电话交谈不能创建店内顾客找货委托");
        if (!request.GameContext.Procurement.CanRequestFutureGoods)
            return None("当前原版来客类型不适合接受找货委托");
        if (!LooksLikeFutureProcurement(request.PlayerText))
            return None("玩家原话中没有明确的未来找货请求");

        var generated = response.ProcurementProposal ?? new ProcurementProposal();
        var explicitCategories = ParseCategories(request.PlayerText);
        if (explicitCategories.Count > 1)
        {
            var categoryNames = string.Join("、", explicitCategories.Select(CategoryDisplayName));
            var reason = $"玩家同时提到了多个物资类别：{categoryNames}";
            OverrideNeedsClarification(response, reason);
            return None(reason, true);
        }

        var category = explicitCategories.Count == 1
            ? explicitCategories[0]
            : NormalizeCategory(generated.Category);
        var reliesOnSemanticClassification = explicitCategories.Count == 0;
        if (category == "none" ||
            (reliesOnSemanticClassification &&
             (generated.ClarificationNeeded ||
              generated.CategoryConfidence < 0.7 ||
              IsGenericDescription(generated.RequestedDescription))))
        {
            const string reason = "请求过于宽泛，无法可靠确定一种原版物资类别";
            OverrideNeedsClarification(response, reason);
            return None(reason, true);
        }

        var access = ProcurementAccessPolicy.Evaluate(request.GameContext, category);
        if (!access.Allowed)
        {
            var reason = $"这名人物无法接触{CategoryDisplayName(category)}：{access.Reason}";
            OverrideFalseAcceptance(response, category, reason);
            return None(reason, true, category);
        }

        if (!generated.Explicit)
            return None("人物没有把本轮话语理解为明确委托", true, category);
        if (!generated.Accepted)
            return None("人物没有答应寻找物品", true, category);

        var description = Clean(generated.RequestedDescription, 80);
        if (string.IsNullOrWhiteSpace(description))
            description = CategoryDisplayName(category);

        var result = new ProcurementResolution
        {
            Explicit = true,
            Accepted = true,
            Category = category,
            RequestedDescription = description,
            DueInDays = ResolveDueInDays(request.PlayerText, generated.DueInDays),
            PromisedPremiumPercent = ResolvePremium(
                request.PlayerText,
                generated.PromisedPremiumPercent),
            AccessLevel = access.AccessLevel,
            MaxRarityTier = access.MaxRarityTier,
            AccessSearchModifier = access.SearchModifier,
            AccessReason = access.Reason,
            Reason = "玩家明确提出未来找货，人物在回复中明确接受"
        };

        response.ProcurementProposal = new ProcurementProposal
        {
            Explicit = true,
            Accepted = true,
            Category = result.Category,
            CategoryConfidence = 1,
            ClarificationNeeded = false,
            RequestedDescription = result.RequestedDescription,
            DueInDays = result.DueInDays,
            PromisedPremiumPercent = result.PromisedPremiumPercent,
            Reason = result.Reason
        };
        return result;
    }

    private static void OverrideFalseAcceptance(
        ConversationResponse response,
        string category,
        string reason)
    {
        response.NpcReply =
            $"{CategoryDisplayName(category)}不在我的路子里。答应你也是白说，你得找真正有这种渠道的人。";
        response.Topic = "procurement";
        response.Mood = "guarded";
        response.Signals.Cooperation = Math.Min(0, response.Signals.Cooperation);
        response.Signals.TradeWillingness = Math.Min(0, response.Signals.TradeWillingness);
        response.MemoryCandidates.RemoveAll(x =>
            x.Kind.Equals("promise", StringComparison.OrdinalIgnoreCase) ||
            x.Summary.Contains("寻找", StringComparison.OrdinalIgnoreCase) ||
            x.Summary.Contains("带来", StringComparison.OrdinalIgnoreCase));
        response.ProcurementProposal = new ProcurementProposal
        {
            Explicit = true,
            Accepted = false,
            Category = category,
            CategoryConfidence = 1,
            ClarificationNeeded = false,
            RequestedDescription = CategoryDisplayName(category),
            DueInDays = 1,
            PromisedPremiumPercent = 0,
            Reason = reason
        };
    }

    private static void OverrideNeedsClarification(
        ConversationResponse response,
        string reason)
    {
        response.NpcReply =
            "你得说具体一点。食品、药品、材料、日用品、武器、工具、模块、门禁卡，还是废品？";
        response.Topic = "procurement";
        response.Mood = "inquiring";
        response.MemoryCandidates.RemoveAll(x =>
            x.Kind.Equals("promise", StringComparison.OrdinalIgnoreCase) ||
            x.Summary.Contains("寻找", StringComparison.OrdinalIgnoreCase) ||
            x.Summary.Contains("带来", StringComparison.OrdinalIgnoreCase));
        response.ProcurementProposal = new ProcurementProposal
        {
            Explicit = true,
            Accepted = false,
            Category = "none",
            CategoryConfidence = 0,
            ClarificationNeeded = true,
            RequestedDescription = string.Empty,
            DueInDays = 1,
            PromisedPremiumPercent = 0,
            Reason = reason
        };
    }

    public static bool LooksLikeFutureProcurement(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasFuture = ContainsAny(
            text,
            "明天", "后天", "下次", "下回", "下趟", "回头", "改天", "以后", "过几天", "天后", "下周");
        var hasSearchAction = ContainsAny(
            text,
            "带", "找", "寻找", "寻", "弄", "搞", "收集", "拿", "搜罗", "物色", "留意", "打听");
        var hasPurchaseIntent = ContainsAny(
            text,
            "我收", "我会收", "我买", "我要", "给我", "需要", "想要", "高价", "加价", "多给", "多付", "采购");
        var hasGoodsTarget = ContainsAny(
            text,
            "货", "东西", "物品", "商品", "食品", "食物", "水", "饮料", "药", "医疗", "材料", "建材",
            "日用品", "武器", "枪", "工具", "设备", "模块", "芯片", "门禁", "卡", "废品", "垃圾", "零件");
        return hasFuture && hasSearchAction && hasPurchaseIntent && hasGoodsTarget;
    }

    public static string NormalizeCategory(string? category) =>
        category?.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_') switch
        {
            "food" or "water" or "drink" => "food",
            "medical" or "medicine" or "med" or "health" => "medical",
            "material" or "materials" or "ore" or "resource" => "material",
            "household" or "amenity" or "amenities" or "daily" => "household",
            "weapon" or "weapons" or "gun" or "arms" => "weapon",
            "tool" or "tools" or "equipment" or "instrument" => "tool",
            "module" or "modules" or "chip" or "electronics" or "tech" => "module",
            "access_card" or "keycard" or "card" => "access_card",
            "junk" or "scrap" => "junk",
            _ => "none"
        };

    public static string CategoryDisplayName(string category) => category switch
    {
        "food" => "食品或饮用物资",
        "medical" => "药品或医疗用品",
        "material" => "原料或建材",
        "household" => "日用品",
        "weapon" => "武器",
        "tool" => "工具或设备",
        "module" => "机械模块",
        "access_card" => "门禁卡",
        "junk" => "废品或杂物",
        _ => "约定的物品"
    };

    public static string ParseCategory(string text)
    {
        var categories = ParseCategories(text);
        return categories.Count == 0 ? "none" : categories[0];
    }

    public static IReadOnlyList<string> ParseCategories(string text)
    {
        var categories = new List<string>();
        if (ContainsAny(text, "门禁卡", "钥匙卡", "门卡", "keycard", "access card"))
            categories.Add("access_card");
        if (ContainsAny(text, "药", "医疗", "医用", "绷带", "注射器", "急救", "治伤", "救命", "medicine", "medical"))
            categories.Add("medical");
        if (ContainsAny(text, "武器", "枪", "弹药", "刀具", "军火", "防身", "火力", "weapon"))
            categories.Add("weapon");
        if (ContainsAny(text, "模块", "模组", "芯片", "组件", "电子元件", "机械零件", "高科技", "module"))
            categories.Add("module");
        if (ContainsAny(text, "工具", "仪器", "设备", "修理用", "干活用", "检测用", "tool"))
            categories.Add("tool");
        if (ContainsAny(text, "材料", "原料", "建材", "矿物", "矿石", "金属", "material"))
            categories.Add("material");
        if (ContainsAny(text, "日用品", "生活用品", "生活物资", "日常用品", "家居", "家用", "household"))
            categories.Add("household");
        if (ContainsAny(text, "废品", "废料", "破烂", "垃圾", "杂物", "junk", "scrap"))
            categories.Add("junk");
        if (ContainsAny(text, "食物", "食品", "口粮", "吃的", "喝的", "饮料", "饮用水", "瓶装水", "酒", "food"))
            categories.Add("food");

        if (categories.Contains("medical", StringComparer.OrdinalIgnoreCase) &&
            categories.Contains("tool", StringComparer.OrdinalIgnoreCase) &&
            !ContainsAny(text, "工具", "tool", "修理用", "干活用"))
        {
            categories.RemoveAll(x => x.Equals("tool", StringComparison.OrdinalIgnoreCase));
        }
        if (ContainsAny(text, "医疗材料", "医用材料"))
            categories.RemoveAll(x => x.Equals("material", StringComparison.OrdinalIgnoreCase));

        return categories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsGenericDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var compact = Regex.Replace(value.ToLowerInvariant(), @"[\s\p{P}]", string.Empty);
        return compact is
            "东西" or "物品" or "货" or "货物" or "好东西" or "值钱的东西" or
            "稀罕货" or "随便什么" or "something" or "goods" or "items";
    }

    private static int ResolveDueInDays(string text, int generated)
    {
        if (text.Contains("后天", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (text.Contains("下周", StringComparison.OrdinalIgnoreCase))
            return 3;

        var match = DaysRegex.Match(text);
        if (match.Success)
        {
            return match.Groups["value"].Value switch
            {
                "1" or "一" => 1,
                "2" or "二" or "两" => 2,
                "3" or "三" => 3,
                _ => 1
            };
        }

        if (text.Contains("明天", StringComparison.OrdinalIgnoreCase))
            return 1;
        return Math.Clamp(generated <= 0 ? 1 : generated, 1, 3);
    }

    private static int ResolvePremium(string text, int generated)
    {
        var promisesMore = ContainsAny(text, "高价", "加价", "多给", "多付", "溢价");
        if (!promisesMore)
            return 0;

        var match = PremiumRegex.Match(text);
        if (match.Success && int.TryParse(match.Groups["value"].Value, out var parsed))
            return Math.Clamp(parsed, 0, 50);

        if (generated > 0)
            return Math.Clamp(generated, 0, 50);
        return 15;
    }

    private static ProcurementResolution None(
        string reason,
        bool explicitRequest = false,
        string category = "none") => new()
    {
        Explicit = explicitRequest,
        Category = category,
        Reason = reason
    };

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string Clean(string? value, int maxLength)
    {
        var result = string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.ReplaceLineEndings(" ").Trim();
        return result.Length > maxLength ? result[..maxLength] : result;
    }
}
