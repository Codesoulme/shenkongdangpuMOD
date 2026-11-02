using AIWorldPawnshop.Models;

namespace AIWorldPawnshop.Runtime;

// Public setting only. Plot revelations and private documents belong to the
// current character's observed story state, not every customer's dialogue.
public static class OriginalWorldLoreCatalog
{
    private static readonly string[] WorldFacts =
    {
        "故事发生在共和国管辖的空间站；玩家在下层区第14区经营店铺，经营受租约和营业许可约束。",
        "空间站分上层区与下层区，生活物资、净水和财富分配差异明显；食物与水的供应会影响市场。",
        "共和国运营配给站，食品券用于下层区福利与食物兑换；配给站售价固定，但供货仍会受市场影响。",
        "治安部会盘查赃物和违禁品；黑市交易存在风险，不能把所有下层区居民都视为罪犯。"
    };

    private static readonly string[] FactionFacts =
    {
        "下层区、上层区和中层区首先是居住与顾客来源分类，不是统一的政治组织。",
        "空间站治安部负责秩序、举报和违禁品盘查，也会采购物资；警官的做法可能因人而异。",
        "黑市是地下交易网络，涉及违禁品、赃物与销赃渠道；不能据此断定某个顾客必然违法。",
        "革命军由不同小组开展行动、筹措物资并使用暗号；具体成员和计划并非公众常识。",
        "卡特尔有接头人和内部忠诚关系；其内部安排与个人身份不能凭顾客分类推定。",
        "拾荒者是一类求生和谋生者，游客是一类来访顾客；两者不等于政治派系。"
    };

    public static void AddPublicKnowledge(OriginalCharacterProfileContext profile)
    {
        profile.WorldBackground.AddRange(WorldFacts);
        profile.FactionBackground.AddRange(FactionFacts);
        profile.KnowledgeBoundaries.Add(
            "世界背景是可谈论的公开设定，不代表你亲历所有事件、认识任何派系成员或掌握秘密计划。"
        );
    }

    public static void AddGmKnowledge(List<string> knowledge)
    {
        knowledge.Add(
            "原版导师笔记称：改造体由失控的脑部植入物接管宿主，会强制植入以扩散，并需要原料与医疗物资；这是剧情敏感资料，不得让普通人物无条件透露，也不得推断其最终目标。"
        );
        knowledge.Add(
            "原版顾客分类、派系声望和实际组织成员身份并不等价；没有当前人物的原版身份或剧情证据，不得擅自赋予组织权限。"
        );
    }
}
