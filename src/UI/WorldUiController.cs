using UnityEngine;
using AIWorldPawnshop.Compatibility;
using AIWorldPawnshop.Models;
using AIWorldPawnshop.Runtime;
using AIWorldPawnshop.GM;

namespace AIWorldPawnshop.UI;

public sealed class WorldUiController
{
    private const string WindowId = "AIWorldPawnshop.World";
    private CustomUIWindow? _window;
    private bool _loggedFailure;

    public void Update(WorldRuntime runtime)
    {
        if (!Input.GetKeyDown(KeyCode.F7) &&
            !Input.GetKeyDown(KeyCode.F9) &&
            !Input.GetKeyDown(KeyCode.Keypad7))
            return;

        try
        {
            Toggle(runtime);
        }
        catch (System.Exception ex)
        {
            if (!_loggedFailure)
            {
                MelonLoader.MelonLogger.Error($"[当铺世界] 人物页面创建失败：{ex}");
                _loggedFailure = true;
            }
        }
    }

    public void Toggle(WorldRuntime runtime)
    {
        MelonLoader.MelonLogger.Msg("[当铺世界] 切换人物与世界页面");
        if (_window is not null && _window.IsAlive && _window.IsVisible)
        {
            _window.Close();
            _window = null;
            return;
        }

        Open(runtime);
    }

    private void Open(WorldRuntime runtime)
    {
        var builder = CustomUIManager.Window(WindowId, ModConstants.DisplayName);
        builder
            .SetSize(760f, 560f)
            .Center()
            .SetCloseOnEscape()
            .SetDraggable(true);

        builder.BeginColumn(8f);
        builder.AddLabel("人物与世界", "title");
        builder.AddLabel(BuildSummary(runtime), "summary");
        builder.AddButton("刷新", new System.Action(() => Refresh(runtime)), "refresh");
        builder.BeginScroll(390f);
        builder.AddLabel(BuildRelationships(runtime.State), "relationships");
        builder.AddLabel(BuildProcurements(runtime.State), "procurements");
        builder.AddLabel(BuildEvents(runtime.State), "events");
        builder.AddLabel(BuildModifications(runtime.State), "modifications");
        builder.AddLabel(BuildThirdPartyTasks(runtime.State), "thirdPartyTasks");
        builder.AddLabel(BuildDiagnostics(runtime), "diagnostics");
        builder.AddLabel(BuildNews(runtime.State), "news");
        builder.End();
        builder.End();

        _window = builder.Show();
        MelonLoader.MelonLogger.Msg("[当铺世界] 人物与世界页面已打开");
    }

    private void Refresh(WorldRuntime runtime)
    {
        if (_window is null || !_window.IsAlive)
            return;

        _window.Get("summary")?.SetText(BuildSummary(runtime));
        _window.Get("relationships")?.SetText(BuildRelationships(runtime.State));
        _window.Get("procurements")?.SetText(BuildProcurements(runtime.State));
        _window.Get("events")?.SetText(BuildEvents(runtime.State));
        _window.Get("modifications")?.SetText(BuildModifications(runtime.State));
        _window.Get("thirdPartyTasks")?.SetText(BuildThirdPartyTasks(runtime.State));
        _window.Get("diagnostics")?.SetText(BuildDiagnostics(runtime));
        _window.Get("news")?.SetText(BuildNews(runtime.State));
    }

    private static string BuildSummary(WorldRuntime runtime)
    {
        var currentClient = string.Empty;
        try
        {
            var client = FormalGameAccess.GetClient(PlayerStore.Instance?.currentClientInstance);
            if (client is not null)
            {
                var name = string.IsNullOrWhiteSpace(client.realName) ? client.displayName : client.realName;
                currentClient = $"\n当前客人：{name} [{client.identifier}]";
            }
        }
        catch
        {
            currentClient = "\n当前客人：暂不可读取";
        }

        return $"第 {runtime.State.CurrentDay} 天  |  世界 {runtime.State.WorldId[..8]}{currentClient}";
    }

    private static string BuildRelationships(WorldState state)
    {
        if (state.Characters.Count == 0)
            return "人物关系\n暂无稳定人物记录。";

        var lines = state.Characters.Values
            .OrderByDescending(x => x.Relationship)
            .Take(20)
            .Select(x =>
                $"{x.DisplayName}  关系 {FormatRelationship(x.Relationship)}  互动 {x.InteractionCount} 次" +
                (string.IsNullOrWhiteSpace(x.FactionKey) ? string.Empty : $"  阵营 {x.FactionKey}") +
                (x.LastInteractionDay is int day ? $"  最近第 {day} 天" : string.Empty) +
                (x.CanReturn || x.CanReturnAsBuyer ? "  可回访" : string.Empty));
        return "人物关系\n" + string.Join("\n", lines);
    }

    private static string BuildEvents(WorldState state)
    {
        if (state.ActiveEvents.Count == 0)
            return "\n当前事件\n暂无持续中的重大事件。";

        var lines = state.ActiveEvents.Select(x =>
            $"[{x.ItemGroup}] {x.Title}（持续至第 {x.StartDay + x.DurationDays - 1} 天）\n{x.Summary}");
        return "\n当前事件\n" + string.Join("\n\n", lines);
    }

    private static string BuildProcurements(WorldState state)
    {
        if (state.ProcurementRequests.Count == 0)
            return "\n找货约定\n暂无委托。";

        var lines = state.ProcurementRequests
            .OrderByDescending(x => x.CreatedDay)
            .Take(12)
            .Select(x =>
                $"{x.CharacterDisplayName}  {x.RequestedDescription}  " +
                $"第 {x.DueDay} 天  {FormatProcurementStatus(x.Status)}" +
                (string.IsNullOrWhiteSpace(x.ResolvedItemName)
                    ? string.Empty
                    : $"  [{x.ResolvedItemName}" +
                      (string.IsNullOrWhiteSpace(x.ResolvedRarityLabel)
                          ? string.Empty
                          : $" / {x.ResolvedRarityLabel}") +
                      "]"));
        return "\n找货约定\n" + string.Join("\n", lines);
    }

    private static string BuildModifications(WorldState state)
    {
        if (state.ActiveModifications.Count == 0)
            return "\nGM 系统变化\n暂无额外世界修正。";
        var lines = state.ActiveModifications.Select(x =>
            $"{x.Title}（{(x.DurationDays == 0 ? "永久" : $"持续至第 {x.StartDay + x.DurationDays - 1} 天")}）\n" +
            $"{x.Summary}\n数据影响：{x.Field} {x.Value:+0.##;-0.##;0}");
        return "\nGM 系统变化\n" + string.Join("\n\n", lines);
    }

    private static string BuildThirdPartyTasks(WorldState state)
    {
        if (state.ThirdPartyTasks.Count == 0) return "\n联系安排\n暂无第三方联系安排。";
        var lines = state.ThirdPartyTasks.OrderByDescending(x => x.CreatedDay).Take(10)
            .Select(x =>
                $"{x.TargetDisplayName}（第 {x.DueDay} 天）\n" +
                $"{x.Purpose}  [{x.Status}]" +
                (x.NativeQueueScheduled ? "  [原版队列已登记]" : "  [等待原版路径]") +
                (x.ArrivedDay is int arrivedDay ? $"  [第 {arrivedDay} 天到店]" : string.Empty));
        return "\n联系安排\n" + string.Join("\n\n", lines);
    }

    private static string BuildDiagnostics(WorldRuntime runtime)
    {
        var all = GmInterfaceCatalog.All;
        var executable = GmInterfaceCatalog.Executable().Count();
        var gaps = all
            .Where(x => !x.Status.Equals("implemented", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Key)
            .Take(8)
            .ToList();
        var queueCount = 0;
        try
        {
            queueCount = PlayerStore.Instance?.futurStoreClientIdQueue?.Count ?? 0;
        }
        catch
        {
            // Native queue can be unavailable during scene transitions.
        }

        var eventPriceCount = runtime.State.ActiveEvents.Count(x =>
            Math.Abs(x.PriceModifier) > 0.0001 ||
            Math.Abs(x.DemandModifier) > 0.0001 ||
            Math.Abs(x.SupplyModifier) > 0.0001);
        var activePriceCount = runtime.State.ActiveModifications.Count(x =>
            x.Field.Equals("price_modifier", StringComparison.OrdinalIgnoreCase));
        var gapText = gaps.Count == 0 ? "无" : string.Join("、", gaps);
        return $"\nGM 诊断（仅本地状态）\n" +
               $"可执行接口：{executable}  | 能力缺口：{gaps.Count}\n" +
               $"缺口项：{gapText}\n" +
               $"世界修订：{runtime.State.StateRevision}  | 事件：{runtime.State.ActiveEvents.Count}（有经济影响 {eventPriceCount}）\n" +
               $"活动价格修正：{activePriceCount}  | 原版未来来客队列：{queueCount}  | 联系任务：{runtime.State.ThirdPartyTasks.Count}";
    }

    private static string FormatProcurementStatus(string status) => status switch
    {
        "pending" => "寻找中",
        "found" => "已经找到",
        "not_found" => "没有找到",
        "delivered" => "已带到店内",
        "fulfilled" => "已成交",
        "declined" => "玩家未购买",
        "failed" => "未能完成",
        "cancelled" => "已取消",
        _ => status
    };

    private static string BuildNews(WorldState state)
    {
        if (state.News.Count == 0)
            return "\n世界新闻\n暂无 Mod 新闻。";

        var lines = state.News
            .OrderByDescending(x => x.Importance)
            .ThenByDescending(x => x.Day)
            .Take(10)
            .Select(x => $"第 {x.Day} 天  {x.Title}\n{x.Body}");
        return "\n世界新闻\n" + string.Join("\n\n", lines);
    }

    private static string FormatRelationship(int relationship)
    {
        return relationship switch
        {
            <= -60 => "敌意",
            <= -20 => "疏远",
            < 20 => "中立",
            < 60 => "熟悉",
            _ => "信任"
        };
    }
}
