using AIWorldPawnshop.Models;

namespace AIWorldPawnshop.Runtime;

internal static class ProcurementRarity
{
    public static string Label(int tier) => tier switch
    {
        <= 0 => "常见",
        1 => "较少见",
        2 => "稀有",
        _ => "极稀有"
    };

    public static int Penalty(int tier) => tier switch
    {
        <= 0 => 0,
        1 => 8,
        2 => 20,
        _ => 35
    };

    public static int FromRelativeWeight(float weight, float maxWeight)
    {
        if (maxWeight <= 0)
            return 1;

        var relative = weight / maxWeight;
        if (relative >= 0.6f)
            return 0;
        if (relative >= 0.25f)
            return 1;
        if (relative >= 0.08f)
            return 2;
        return 3;
    }
}

internal sealed class ProcurementAccessDecision
{
    public string Category { get; init; } = "none";
    public int AccessLevel { get; init; }
    public int MaxRarityTier { get; init; }
    public int SearchModifier { get; init; }
    public string Reason { get; init; } = string.Empty;
    public bool Allowed => AccessLevel > 0;
    public string AccessLabel => AccessLevel switch
    {
        1 => "间接渠道",
        2 => "稳定渠道",
        3 => "专业渠道",
        _ => "没有渠道"
    };
    public string MaxRarityLabel => ProcurementRarity.Label(MaxRarityTier);
}

internal static class ProcurementAccessPolicy
{
    public static readonly IReadOnlyList<string> Categories = new[]
    {
        "food",
        "medical",
        "material",
        "household",
        "weapon",
        "tool",
        "module",
        "access_card",
        "junk"
    };

    public static IReadOnlyList<ProcurementAccessDecision> EvaluateAll(
        GameConversationContext context) =>
        Categories.Select(category => Evaluate(context, category)).ToList();

    public static ProcurementAccessDecision Evaluate(
        GameConversationContext context,
        string category)
    {
        category = ProcurementRequestResolver.NormalizeCategory(category);
        if (category == "none")
            return Decision(category, 0, "无法识别所需物资类别");

        var levels = Categories.ToDictionary(x => x, _ => 0, StringComparer.OrdinalIgnoreCase);
        var reasons = Categories.ToDictionary(
            x => x,
            _ => "原版身份、阵营与随身货物均未显示这类渠道",
            StringComparer.OrdinalIgnoreCase);

        Raise(levels, reasons, "food", 1, "普通生活物资可以通过零散市场打听");
        Raise(levels, reasons, "household", 1, "普通生活物资可以通过零散市场打听");
        Raise(levels, reasons, "junk", 1, "废品可以通过公开或零散渠道搜集");

        var character = context.Character;
        var faction = Join(
            character.FactionKey,
            character.FactionDisplayName);
        var identity = Join(
            character.Key,
            character.OriginalIdentifier,
            character.OriginalRealName,
            character.DisplayName,
            string.Join(" ", character.Tags));

        if (character.IsSecurity || ContainsAny(faction, "security", "安保", "治安"))
        {
            Raise(levels, reasons, "weapon", 3, "安保体系拥有受控武器渠道");
            Raise(levels, reasons, "access_card", 3, "安保体系拥有门禁凭证渠道");
            Raise(levels, reasons, "medical", 2, "安保体系可以取得制式医疗物资");
            Raise(levels, reasons, "tool", 2, "安保体系可以取得专业设备");
            Raise(levels, reasons, "module", 2, "安保体系可以取得受控模块");
            Raise(levels, reasons, "material", 1, "安保后勤可以间接取得材料");
        }

        if (character.IsWanted || ContainsAny(faction, "crime", "criminal", "黑市", "帮派", "犯罪"))
        {
            Raise(levels, reasons, "weapon", 2, "地下关系可以取得部分武器");
            Raise(levels, reasons, "access_card", 2, "地下关系可以取得部分门禁凭证");
            Raise(levels, reasons, "tool", 2, "地下关系可以取得专业工具");
            Raise(levels, reasons, "material", 2, "地下关系可以取得来路复杂的材料");
            Raise(levels, reasons, "junk", 2, "地下关系熟悉废品货源");
            Raise(levels, reasons, "medical", 1, "地下关系只能间接取得医疗物资");
            Raise(levels, reasons, "module", 1, "地下关系只能间接取得模块");
        }

        if (ContainsAny(faction, "upper", "上层"))
        {
            Raise(levels, reasons, "medical", 2, "上层渠道可以取得较好的医疗物资");
            Raise(levels, reasons, "module", 2, "上层渠道可以取得技术模块");
            Raise(levels, reasons, "tool", 2, "上层渠道可以取得专业设备");
            Raise(levels, reasons, "household", 2, "上层渠道熟悉生活物资市场");
            Raise(levels, reasons, "material", 1, "上层渠道可通过中间人取得材料");
        }

        if (ContainsAny(faction, "middle", "中层"))
        {
            Raise(levels, reasons, "food", 2, "中层商业渠道熟悉食品供应");
            Raise(levels, reasons, "household", 2, "中层商业渠道熟悉日用品供应");
            Raise(levels, reasons, "medical", 1, "中层渠道可间接取得普通医疗物资");
            Raise(levels, reasons, "material", 1, "中层渠道可间接取得常见材料");
            Raise(levels, reasons, "tool", 1, "中层渠道可间接取得常见工具");
        }

        if (ContainsAny(faction, "lower", "下层", "底层"))
        {
            Raise(levels, reasons, "material", 2, "下层生产与回收网络熟悉材料货源");
            Raise(levels, reasons, "junk", 2, "下层回收网络熟悉废品货源");
            Raise(levels, reasons, "tool", 1, "下层渠道可间接取得常见工具");
            Raise(levels, reasons, "food", 1, "下层市场可取得普通食品");
        }

        if (ContainsAny(identity,
                "doctor", "medic", "nurse", "clinic", "pharma", "chemist",
                "医生", "医疗", "护士", "诊所", "药剂", "化学"))
        {
            Raise(levels, reasons, "medical", 3, "此人的职业直接连接医疗货源");
            Raise(levels, reasons, "tool", 2, "此人的职业可以取得医疗设备");
        }

        if (ContainsAny(identity,
                "gunsmith", "merc", "soldier", "army", "officer", "armory",
                "枪匠", "佣兵", "士兵", "军火", "军队", "警官"))
        {
            Raise(levels, reasons, "weapon", 3, "此人的职业直接连接武器货源");
            Raise(levels, reasons, "tool", 2, "此人的职业可以取得武器维护工具");
        }

        if (ContainsAny(identity,
                "inventor", "engineer", "technician", "mechanic",
                "发明", "工程", "技师", "机械师"))
        {
            Raise(levels, reasons, "module", 3, "此人的职业直接连接模块货源");
            Raise(levels, reasons, "tool", 3, "此人的职业直接连接专业工具");
            Raise(levels, reasons, "material", 2, "此人的职业熟悉技术材料");
        }

        if (ContainsAny(identity,
                "miner", "scrap", "junker", "scav", "salvage",
                "矿工", "拾荒", "废品", "回收"))
        {
            Raise(levels, reasons, "material", 3, "此人的职业直接连接材料货源");
            Raise(levels, reasons, "junk", 3, "此人的职业直接连接回收货源");
            Raise(levels, reasons, "tool", 2, "此人的职业可以取得作业工具");
        }

        if (ContainsAny(identity,
                "thief", "shady", "cartel", "blackmarket", "smuggler",
                "窃贼", "小偷", "黑市", "走私", "帮派"))
        {
            Raise(levels, reasons, "access_card", 3, "此人的地下职业直接连接门禁凭证");
            Raise(levels, reasons, "weapon", 2, "此人的地下职业可以取得部分武器");
            Raise(levels, reasons, "tool", 2, "此人的地下职业可以取得特殊工具");
        }

        if (ContainsAny(identity,
                "supplier", "merchant", "dealer", "trader", "wholesale",
                "供应", "商人", "商贩", "经销", "批发"))
        {
            foreach (var commonCategory in new[]
                     {
                         "food", "material", "household", "tool", "junk"
                     })
            {
                Raise(levels, reasons, commonCategory, 2, "此人的商业职业拥有稳定供货渠道");
            }
            Raise(levels, reasons, "medical", 1, "此人的商业职业可间接打听普通医疗物资");
            Raise(levels, reasons, "module", 1, "此人的商业职业可间接打听普通模块");
        }

        if (ContainsAny(identity,
                "food", "farmer", "brewer", "water", "cook",
                "食品", "农场", "农夫", "酿造", "饮水", "厨师"))
        {
            Raise(levels, reasons, "food", 3, "此人的职业直接连接食品与饮水货源");
        }

        foreach (var item in context.Trade.ClientCarriedItems)
        {
            var carriedCategory = InferCategory(item);
            if (carriedCategory != "none")
            {
                Raise(
                    levels,
                    reasons,
                    carriedCategory,
                    2,
                    "这名顾客本次确实携带了同类货物");
            }
        }

        return Decision(category, levels[category], reasons[category]);
    }

    private static ProcurementAccessDecision Decision(
        string category,
        int level,
        string reason) => new()
    {
        Category = category,
        AccessLevel = level,
        MaxRarityTier = level switch
        {
            >= 3 => 3,
            2 => 2,
            1 => 0,
            _ => 0
        },
        SearchModifier = level switch
        {
            >= 3 => 10,
            2 => 0,
            1 => -15,
            _ => -100
        },
        Reason = reason
    };

    private static void Raise(
        IDictionary<string, int> levels,
        IDictionary<string, string> reasons,
        string category,
        int level,
        string reason)
    {
        if (level <= levels[category])
            return;
        levels[category] = level;
        reasons[category] = reason;
    }

    private static string InferCategory(NativeItemContext item)
    {
        foreach (var type in item.ItemTypes)
        {
            var normalized = ProcurementRequestResolver.NormalizeCategory(type);
            if (normalized != "none")
                return normalized;
        }

        var featureText = string.Join(" ", item.Features.Select(x => x.Category));
        var text = Join(
            item.ItemKey,
            item.DisplayName,
            item.Description,
            string.Join(" ", item.ItemTypes),
            featureText);
        return ProcurementRequestResolver.ParseCategory(text);
    }

    private static string Join(params string?[] values) =>
        string.Join(" ", values.Where(x => !string.IsNullOrWhiteSpace(x))).ToLowerInvariant();

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
}
