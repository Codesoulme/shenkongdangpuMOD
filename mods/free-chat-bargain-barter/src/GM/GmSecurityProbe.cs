
namespace AIWorldPawnshop.GM;

public sealed class GmSecuritySnapshot
{
    public bool PlayerStoreAvailable { get; init; }
    public bool AlarmActive { get; init; }
    public bool AlarmPresent { get; init; }
    public int AlarmPerformance { get; init; }
    public int AlarmStopRate { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public static class GmSecurityProbe
{
    public const string ExecutorKey = "security.probe";
    public static GmSecuritySnapshot Capture()
    {
        try
        {
            var store = PlayerStore.Instance;
            if (store is null)
                return new() { Summary = "当前无法读取商店安全状态。" };

            var alarm = MachineAlarm.FindAlarmeItem();
            var present = alarm is not null;
            var performance = present ? MachineAlarm.GetAlarmePerformance(alarm) : 0;
            var stopRate = present ? MachineAlarm.GetAlarmeStopRate(alarm) : 0;
            var active = store.isSecurityAlarmActive;
            return new GmSecuritySnapshot
            {
                PlayerStoreAvailable = true,
                AlarmActive = active,
                AlarmPresent = present,
                AlarmPerformance = performance,
                AlarmStopRate = stopRate,
                Summary = $"警报器：{(present ? "已安装" : "未安装")}，{(active ? "已启用" : "未启用")}；性能 {performance}，拦截率 {stopRate}。"
            };
        }
        catch (Exception ex)
        {
            return new GmSecuritySnapshot { Summary = $"安全状态读取失败：{ex.Message}" };
        }
    }
}
