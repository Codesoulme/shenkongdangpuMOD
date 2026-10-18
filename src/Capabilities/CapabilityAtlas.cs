using AIWorldPawnshop.Models;
using AIWorldPawnshop.Models.EchoWeave;

namespace AIWorldPawnshop.Capabilities;

public sealed class CapabilityDescriptor
{
    public string CapabilityKey { get; init; } = string.Empty;
    public string ImplementationStatus { get; init; } = "catalog_only";
    public string Version { get; init; } = "1.0";
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<string> Tags { get; init; } = new();
    public List<string> SupportedScenes { get; init; } = new();
    public EchoWeaveRiskLevel RiskLevel { get; init; } = EchoWeaveRiskLevel.R0ReadOnly;
    public List<string> Permissions { get; init; } = new();
    public List<EchoWeaveSafeTiming> SafeTimings { get; init; } = new();
    public List<string> Preconditions { get; init; } = new();
    public List<string> Effects { get; init; } = new();
    public List<string> IdempotencyFields { get; init; } = new();
    public string FailureDescription { get; init; } = string.Empty;
}

public sealed class CapabilityInvocation
{
    public string CapabilityKey { get; init; } = string.Empty;
    public string CapabilityVersion { get; init; } = string.Empty;
    public Dictionary<string, string> Arguments { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string DeduplicationKey { get; init; } = string.Empty;
}

public sealed class CapabilityCheck
{
    public bool Allowed { get; init; }
    public string Reason { get; init; } = string.Empty;
    public bool RequiresDeferral { get; init; }
    public EchoWeaveSafeTiming? RequiredTiming { get; init; }

    public static CapabilityCheck Allow(string reason = "") => new() { Allowed = true, Reason = reason };
    public static CapabilityCheck Deny(string reason) => new() { Allowed = false, Reason = reason };
    public static CapabilityCheck Defer(EchoWeaveSafeTiming timing, string reason) => new()
    {
        Allowed = true,
        RequiresDeferral = true,
        RequiredTiming = timing,
        Reason = reason
    };
}

public sealed class CapabilityReceipt
{
    public string CapabilityKey { get; init; } = string.Empty;
    public string DeduplicationKey { get; init; } = string.Empty;
    public string Status { get; init; } = "failed";
    public string Summary { get; init; } = string.Empty;
    public Dictionary<string, string> Outputs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsSuccess => Status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                             Status.Equals("deferred", StringComparison.OrdinalIgnoreCase) ||
                             Status.Equals("awaiting_native_confirmation", StringComparison.OrdinalIgnoreCase);
}

public sealed class ReconciliationResult
{
    public bool Consistent { get; init; }
    public string Reason { get; init; } = string.Empty;
    public bool ShouldRetry { get; init; }
}

public sealed class CompensationResult
{
    public bool Compensated { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public sealed class WorldExecutionContext
{
    public WorldState State { get; init; } = new();
    public EchoWeaveSnapshot Snapshot { get; init; } = new();
    public EchoWeaveSafeTiming Timing { get; init; }
}

public interface IWorldCapability
{
    CapabilityDescriptor Descriptor { get; }
    CapabilityCheck Check(CapabilityInvocation invocation, EchoWeaveSnapshot snapshot);
    CapabilityReceipt Execute(CapabilityInvocation invocation, WorldExecutionContext context);
    ReconciliationResult Reconcile(IntentRecord intent, EchoWeaveSnapshot snapshot);
    CompensationResult Compensate(CapabilityReceipt receipt, WorldExecutionContext context);
}

/// <summary>
/// Static capability directory. Registration is intentionally explicit so a
/// model can only select actions compiled into the Mod.
/// </summary>
public sealed class CapabilityRegistry
{
    private readonly Dictionary<string, IWorldCapability> _capabilities =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count => _capabilities.Count;

    public void Register(IWorldCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        var key = capability.Descriptor.CapabilityKey.Trim();
        if (key.Length == 0)
            throw new ArgumentException("Capability key cannot be empty.", nameof(capability));
        if (!_capabilities.TryAdd(key, capability))
            throw new InvalidOperationException($"Capability already registered: {key}");
    }

    public bool TryGet(string capabilityKey, out IWorldCapability capability) =>
        _capabilities.TryGetValue(capabilityKey ?? string.Empty, out capability!);

    public IWorldCapability? Describe(string capabilityKey) =>
        TryGet(capabilityKey, out var capability) ? capability : null;

    public IReadOnlyList<CapabilityDescriptor> Search(
        string? query,
        string? scene = null,
        EchoWeaveRiskLevel? maximumRisk = null)
    {
        var terms = (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return _capabilities.Values
            .Where(capability => maximumRisk is null ||
                                 capability.Descriptor.RiskLevel <= maximumRisk.Value)
            .Where(capability => string.IsNullOrWhiteSpace(scene) ||
                                 capability.Descriptor.SupportedScenes.Count == 0 ||
                                 capability.Descriptor.SupportedScenes.Any(x =>
                                     x.Equals(scene, StringComparison.OrdinalIgnoreCase)))
            .Where(capability => terms.Length == 0 || terms.All(term =>
                capability.Descriptor.CapabilityKey.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                capability.Descriptor.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                capability.Descriptor.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                capability.Descriptor.Tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase))))
            .Select(capability => capability.Descriptor)
            .OrderBy(descriptor => descriptor.RiskLevel)
            .ThenBy(descriptor => descriptor.CapabilityKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<CapabilityDescriptor> All() => _capabilities.Values
        .Select(x => x.Descriptor)
        .OrderBy(x => x.CapabilityKey, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

public sealed class DelegateWorldCapability : IWorldCapability
{
    private readonly Func<CapabilityInvocation, EchoWeaveSnapshot, CapabilityCheck>? _check;
    private readonly Func<CapabilityInvocation, WorldExecutionContext, CapabilityReceipt>? _execute;
    private readonly Func<IntentRecord, EchoWeaveSnapshot, ReconciliationResult>? _reconcile;
    private readonly Func<CapabilityReceipt, WorldExecutionContext, CompensationResult>? _compensate;

    public DelegateWorldCapability(
        CapabilityDescriptor descriptor,
        Func<CapabilityInvocation, EchoWeaveSnapshot, CapabilityCheck>? check = null,
        Func<CapabilityInvocation, WorldExecutionContext, CapabilityReceipt>? execute = null,
        Func<IntentRecord, EchoWeaveSnapshot, ReconciliationResult>? reconcile = null,
        Func<CapabilityReceipt, WorldExecutionContext, CompensationResult>? compensate = null)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _check = check;
        _execute = execute;
        _reconcile = reconcile;
        _compensate = compensate;
    }

    public CapabilityDescriptor Descriptor { get; }

    public CapabilityCheck Check(CapabilityInvocation invocation, EchoWeaveSnapshot snapshot) =>
        _check?.Invoke(invocation, snapshot) ?? CapabilityCheck.Allow();

    public CapabilityReceipt Execute(CapabilityInvocation invocation, WorldExecutionContext context) =>
        _execute?.Invoke(invocation, context) ?? new CapabilityReceipt
        {
            CapabilityKey = Descriptor.CapabilityKey,
            DeduplicationKey = invocation.DeduplicationKey,
            Status = "deferred",
            Summary = "该能力已登记，等待原版安全时机。"
        };

    public ReconciliationResult Reconcile(IntentRecord intent, EchoWeaveSnapshot snapshot) =>
        _reconcile?.Invoke(intent, snapshot) ?? new ReconciliationResult
        {
            Consistent = true,
            Reason = "没有可供第一阶段检查的原版回执。"
        };

    public CompensationResult Compensate(CapabilityReceipt receipt, WorldExecutionContext context) =>
        _compensate?.Invoke(receipt, context) ?? new CompensationResult
        {
            Compensated = false,
            Summary = "该能力没有登记补偿动作。"
        };
}

public static class BuiltInCapabilityCatalog
{
    public static CapabilityRegistry Create()
    {
        var registry = new CapabilityRegistry();
        Register(registry, "character.resolve", "解析人物", EchoWeaveRiskLevel.R0ReadOnly,
            "根据稳定键或当前可见称呼解析人物，不修改世界。", "read", "store phone");
        Register(registry, "character.check_contact", "检查联系路径", EchoWeaveRiskLevel.R0ReadOnly,
            "检查人物是否有原版或扩展可用的联系路径。", "read", "store phone");
        Register(registry, "message.relay", "转达消息", EchoWeaveRiskLevel.R1ExtensionState,
            "写入一条待处理的消息转达记录。", "world.read mod.tasks.write", "store phone");
        Register(registry, "social.task.create", "建立人物任务", EchoWeaveRiskLevel.R1ExtensionState,
            "登记一项可在后续安全时机继续处理的人物任务。", "world.read mod.tasks.write", "store phone");
        Register(registry, "visit.request.self", "登记本人回访", EchoWeaveRiskLevel.R2RecoverableNative,
            "通过原版来客队列安排当前顾客未来回访。", "world.read mod.tasks.write native.visitor_queue.propose", "store", true);
        Register(registry, "visit.request.third_party", "申请第三方来访", EchoWeaveRiskLevel.R2RecoverableNative,
            "在目标人物同意后申请未来来访候选。", "world.read mod.tasks.write native.visitor_queue.propose", "store phone", true);
        Register(registry, "procurement.request.goods", "登记找货委托", EchoWeaveRiskLevel.R3SensitiveNative,
            "按人物渠道登记未来找货委托。", "world.read mod.tasks.write native.inventory.propose", "store", true);
        Register(registry, "inventory.reveal.carried", "展示随身货物", EchoWeaveRiskLevel.R2RecoverableNative,
            "读取并展示当前顾客的真实随身货物。", "world.read", "store", true);
        Register(registry, "trade.adjust.selected", "调整指定报价", EchoWeaveRiskLevel.R2RecoverableNative,
            "调用原版议价规则调整当前指定物品报价。", "world.read native.trade.propose", "store", true);
        Register(registry, "trade.adjust.all", "调整整批报价", EchoWeaveRiskLevel.R2RecoverableNative,
            "调用原版议价规则调整当前交易范围报价。", "world.read native.trade.propose", "store", true);
        Register(registry, "barter.open.native", "打开原版交换", EchoWeaveRiskLevel.R3SensitiveNative,
            "打开原版交换窗口，并等待原版最终确认。", "world.read native.barter.open", "store", true);
        Register(registry, "wallet.transfer.native", "执行顾客预算转账", EchoWeaveRiskLevel.R3SensitiveNative,
            "通过原版预算和现金接口完成顾客到玩家的明确补付。", "world.read native.wallet.transfer", "store", true);
        Register(registry, "world.modify", "提交世界数值修正", EchoWeaveRiskLevel.R1ExtensionState,
            "通过 GM 数值执行器写入已验证的供需或价格修正。", "world.read mod.world.write", "store", true);
        Register(registry, "memory.append.summary", "记录经历摘要", EchoWeaveRiskLevel.R1ExtensionState,
            "追加经过长度限制和去重的经历摘要。", "mod.memory.write", "store phone");
        Register(registry, "relationship.propose.change", "提出关系变化", EchoWeaveRiskLevel.R1ExtensionState,
            "记录待裁决的关系候选，不直接相信模型数值。", "world.read mod.relationship.propose", "store phone");
        Register(registry, "world.propose.event", "提出世界事件", EchoWeaveRiskLevel.R1ExtensionState,
            "登记待确定的世界事件草稿，不直接修改原版经济。", "world.read mod.world.propose", "store");
        Register(registry, "dialogue.ask.clarification", "请求澄清", EchoWeaveRiskLevel.R0ReadOnly,
            "要求玩家补充唯一目标、范围或期限。", "dialogue.write", "store phone");
        return registry;
    }

    private static void Register(
        CapabilityRegistry registry,
        string key,
        string title,
        EchoWeaveRiskLevel risk,
        string description,
        string permissions,
        string scenes,
        bool implemented = false)
    {
        registry.Register(new DelegateWorldCapability(new CapabilityDescriptor
        {
            CapabilityKey = key,
            ImplementationStatus = implemented ? "implemented" : "catalog_only",
            Title = title,
            Description = description,
            Tags = key.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList(),
            SupportedScenes = scenes.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
            RiskLevel = risk,
            Permissions = permissions.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
            SafeTimings = new List<EchoWeaveSafeTiming> { EchoWeaveSafeTiming.AfterDialogue },
            IdempotencyFields = new List<string> { "world_id", "session_id", "turn_id", "capability_key", "arguments" },
            FailureDescription = "原版状态变化或上下文失效时，必须延期、拒绝或说明未完成。"
        }));
    }
}
