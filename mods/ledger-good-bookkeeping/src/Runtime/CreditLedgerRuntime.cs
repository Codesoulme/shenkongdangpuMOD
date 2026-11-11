using System.Diagnostics;
using System.Text;
using AIWorldPawnshop.Compatibility;
using AIWorldPawnshop.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIWorldPawnshop.Runtime;

internal sealed class CreditLedgerRuntime
{
    private readonly string _dataPath;
    private readonly string _legacyWorldPath;
    private readonly string _worldIdPath;
    private CreditLedgerData _data;

    internal IReadOnlyList<CreditLedgerEntry> Entries => _data.Entries;

    internal static void RecordFromPatch(long delta, string type, string detail) =>
        AIWorldPawnshopMod.Instance?.OnNativeCreditChanged(delta, type, detail);

    internal CreditLedgerRuntime(string dataRoot)
    {
        System.IO.Directory.CreateDirectory(dataRoot);
        var worlds = Path.Combine(dataRoot, "Worlds");
        System.IO.Directory.CreateDirectory(worlds);
        _worldIdPath = Path.Combine(dataRoot, "world-id.txt");
        var worldId = File.Exists(_worldIdPath) ? File.ReadAllText(_worldIdPath).Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(worldId))
        {
            worldId = Guid.NewGuid().ToString("N");
            File.WriteAllText(_worldIdPath, worldId);
        }
        _legacyWorldPath = Path.Combine(worlds, worldId + ".json");
        _dataPath = Path.Combine(dataRoot, "CreditLedger.json");
        _data = Load();
    }

    internal void RecordItemBought(GameItem? item, int cost) =>
        Record("支出", cost, "向顾客购入", DescribeItem(item), NegotiationDetails(false));

    internal void RecordItemSold(GameItem? item, int amount) =>
        Record("收入", amount, "售予顾客", DescribeItem(item), NegotiationDetails(true));

    internal void RecordInformationPurchase(int amount, string customerName) =>
        Record("支出", amount, "购买情报", "顾客情报服务", customerNameOverride: customerName);

    internal void RecordCreditChange(long delta, string transactionType, string detail)
    {
        if (delta == 0) return;
        var amount = (int)Math.Min(Math.Abs(delta), int.MaxValue);
        Record(delta > 0 ? "收入" : "支出", amount, transactionType, detail);
    }

    internal void OnSave(int saveSlotId)
    {
        if (saveSlotId >= 0)
            _data.SaveSnapshots[LatestSaveKey(saveSlotId)] = Clone(_data.Entries);
        Save();
    }

    internal void OnSaveRotated(int saveSlotId, string? historyFile)
    {
        if (saveSlotId < 0 || string.IsNullOrWhiteSpace(historyFile)) return;
        if (_data.SaveSnapshots.TryGetValue(LatestSaveKey(saveSlotId), out var snapshot))
            _data.SaveSnapshots[HistorySaveKey(historyFile)] = Clone(snapshot);
        Save();
    }

    internal void OnGameLoaded(int saveSlotId, string? saveFile)
    {
        if (saveSlotId < 0) return;
        var key = string.IsNullOrWhiteSpace(saveFile)
            ? LatestSaveKey(saveSlotId)
            : HistorySaveKey(Path.GetFileName(saveFile));
        _data.Entries = _data.SaveSnapshots.TryGetValue(key, out var snapshot)
            ? Clone(snapshot)
            : new List<CreditLedgerEntry>();
        Save();
    }

    private CreditLedgerData Load()
    {
        if (File.Exists(_dataPath))
        {
            try
            {
                var saved = JsonConvert.DeserializeObject<CreditLedgerData>(File.ReadAllText(_dataPath));
                if (saved is not null)
                {
                    saved.Entries ??= new List<CreditLedgerEntry>();
                    saved.SaveSnapshots ??= new Dictionary<string, List<CreditLedgerEntry>>(StringComparer.Ordinal);
                    return saved;
                }
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning($"[账本] 读取记录失败：{ex.Message}"); }
        }

        var migrated = new CreditLedgerData();
        if (File.Exists(_legacyWorldPath))
        {
            try
            {
                var world = JObject.Parse(File.ReadAllText(_legacyWorldPath));
                migrated.Entries = world["CreditLedger"]?.ToObject<List<CreditLedgerEntry>>() ?? new();
                migrated.SaveSnapshots = world["CreditLedgerSaveSnapshots"]?.ToObject<Dictionary<string, List<CreditLedgerEntry>>>()
                    ?? new Dictionary<string, List<CreditLedgerEntry>>(StringComparer.Ordinal);
                Save();
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning($"[账本] 迁移旧记录失败：{ex.Message}"); }
        }
        return migrated;
    }

    private void Save()
    {
        var temporary = _dataPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonConvert.SerializeObject(_data, Formatting.Indented), Encoding.UTF8);
            if (File.Exists(_dataPath))
            {
                File.Replace(temporary, _dataPath, _dataPath + ".bak", true);
            }
            else
            {
                File.Move(temporary, _dataPath);
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"[账本] 保存记录失败：{ex.Message}");
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private void Record(
        string direction, int amount, string transactionType, string detail,
        string negotiation = "", string? customerNameOverride = null)
    {
        if (amount <= 0) return;
        var customerName = string.IsNullOrWhiteSpace(customerNameOverride) ? "未识别顾客" : customerNameOverride;
        try
        {
            var client = LedgerGameAccess.GetClient(PlayerStore.Instance?.currentClientInstance);
            if (string.IsNullOrWhiteSpace(customerNameOverride) && client is not null)
                customerName = string.IsNullOrWhiteSpace(client.realName) ? client.displayName : client.realName;
        }
        catch { }

        _data.Entries.Add(new CreditLedgerEntry
        {
            Day = StoreStation.Instance != null ? StoreStation.Instance.dayCounter : 0,
            Direction = direction,
            Amount = amount,
            CustomerName = customerName,
            TransactionType = transactionType,
            Detail = detail,
            Negotiation = negotiation
        });
        if (_data.Entries.Count > 1000) _data.Entries.RemoveRange(0, _data.Entries.Count - 1000);
        Save();
    }

    private static string LatestSaveKey(int slotId) => $"slot:{slotId}";
    private static string HistorySaveKey(string fileName) => $"file:{fileName}";

    private static List<CreditLedgerEntry> Clone(IEnumerable<CreditLedgerEntry> source) => source.Select(entry => new CreditLedgerEntry
    {
        EntryId = entry.EntryId, Day = entry.Day, Direction = entry.Direction, Amount = entry.Amount,
        CustomerName = entry.CustomerName, TransactionType = entry.TransactionType,
        Detail = entry.Detail, Negotiation = entry.Negotiation
    }).ToList();

    private static string NegotiationDetails(bool sellingToCustomer)
    {
        try
        {
            var bargain = BargainUIManager.Instance;
            if (bargain == null) return string.Empty;
            var details = new List<string>();
            if (sellingToCustomer)
            {
                if (bargain.DiscountOffered) details.Add($"折扣 {bargain.DiscountPercent}%");
                if (bargain.MarkupOffered && LedgerGameAccess.GetMarkupApplied(bargain))
                    details.Add($"加价 {bargain.MarkupPercent}%");
            }
            else
            {
                if (bargain.BuyingDiscountOffered && LedgerGameAccess.GetBuyingDiscountApplied(bargain))
                    details.Add($"折扣 {bargain.BuyingDiscountPercent}%");
                if (bargain.BuyingMarkupOffered) details.Add($"加价 {bargain.BuyingMarkupPercent}%");
            }
            return string.Join("、", details);
        }
        catch { return string.Empty; }
    }

    private static string DescribeItem(GameItem? item)
    {
        if (item is null) return "物品信息不可用";
        try
        {
            var name = item.GetDisplayName();
            return string.IsNullOrWhiteSpace(name) ? item.identifier : $"{name}（{item.identifier}）";
        }
        catch { return "物品信息不可用"; }
    }

    internal static string DescribeModCashSource(long delta)
    {
        var caller = new StackTrace().GetFrames()?.Select(frame => frame.GetMethod())
            .FirstOrDefault(method => method?.DeclaringType != typeof(PlayerStore) &&
                method?.DeclaringType != typeof(CreditLedgerPatchSupport) &&
                method?.DeclaringType?.Namespace != typeof(AIWorldPawnshopMod).Namespace);
        var type = caller?.DeclaringType?.Name ?? string.Empty;
        var methodName = caller?.Name ?? string.Empty;
        var category = type switch
        {
            "AugHelper" => "改造体结算", "CommissaryUIManager" => "兑换信用点",
            "GunOrderData" => "枪械订单结算", "LotteryHelper" => "彩票结算",
            "StartingPerkContent" => "特质结算", "StoreClientListAppraisal" => "鉴定服务结算",
            "StoreClientListSecurity" => "安保结算", "StoreClientListWanted" => "悬赏结算",
            "StoreClientListCartel" => "势力交易结算", "StoreClientListMinor" or "StoreClientListSpec" => "特殊事件结算",
            "StoreClientUniqueList" => "特殊顾客结算", "StoreRequest" => "委托结算",
            "VendingMachineHelper" => "售货机结算", "StoreClientList" => "顾客事件结算",
            _ => delta < 0 ? "其他支出" : "其他收入"
        };
        var detail = string.IsNullOrWhiteSpace(type) ? "系统信用点变动" : $"{type} · {methodName}";
        return category + "|" + detail;
    }
}
