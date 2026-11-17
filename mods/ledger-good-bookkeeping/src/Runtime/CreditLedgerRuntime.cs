using System.Diagnostics;
using System.Text;
using AIWorldPawnshop.Compatibility;
using AIWorldPawnshop.Models;
using AIWorldPawnshop.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIWorldPawnshop.Runtime;

internal sealed class CreditLedgerRuntime
{
    private readonly string _dataPath;
    private readonly string _journalPath;
    private readonly string _legacyWorldPath;
    private readonly string _worldIdPath;
    private CreditLedgerData _data;
    private int _batchDepth;
    private bool _dirty;
    private bool _checkpointRequired;
    private readonly List<CreditLedgerEntry> _pending = new();
    private long _committedJournalLength;

    internal void BeginBatch() => _batchDepth++;
    internal void EndBatch()
    {
        if (_batchDepth > 0 && --_batchDepth == 0) Flush();
    }

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
        _journalPath = Path.Combine(dataRoot, "CreditLedger.transactions.jsonl");
        _data = Load();
        if (string.IsNullOrEmpty(_data.JournalGeneration)) Checkpoint();
        LoadJournal();
    }

    internal void RecordItemBought(LedgerTradeDetails item, int cost, long? balanceAfter = null) =>
        Record("支出", cost, "向顾客购入", "", item.PriceModifiers,
            item: item, balanceAfter: balanceAfter);

    internal void RecordItemSold(LedgerTradeDetails item, int amount, long? balanceAfter = null) =>
        Record("收入", amount, "售予顾客", "", item.PriceModifiers,
            item: item, balanceAfter: balanceAfter);

    internal void RecordInformationPurchase(int amount, string customerName) =>
        Record("支出", amount, "购买情报", "顾客情报服务", customerNameOverride: customerName);

    internal void RecordAppraisalIncome(long delta, LedgerTradeDetails? item)
    {
        if (delta <= 0) return;
        // Appraised-item value modifiers are not modifiers of the service fee.
        Record("收入", (int)Math.Min(delta, int.MaxValue), "鉴定服务收入", "提供物品鉴定", item: item);
    }

    internal void RecordCreditChange(long delta, string transactionType, string detail)
    {
        if (delta == 0) return;
        var amount = (int)Math.Min(Math.Abs(delta), int.MaxValue);
        Record(delta > 0 ? "收入" : "支出", amount, transactionType, detail);
    }

    internal void RecordCashChange(long delta, LedgerCashSource source)
    {
        if (delta == 0) return;
        Record(delta > 0 ? "收入" : "支出", (int)Math.Min(Math.Abs(delta), int.MaxValue),
            source.Type, source.Detail, customerNameOverride: source.Customer,
            item: source.Item, balanceAfter: PlayerStore.Instance?.playerCash);
    }

    internal void OnSave(int saveSlotId)
    {
        if (saveSlotId >= 0)
            _data.SaveSnapshots[LatestSaveKey(saveSlotId)] = Clone(_data.Entries);
        Checkpoint(); // Game saves commit pending entries and the matching snapshot together.
    }

    internal void OnSaveRotated(int saveSlotId, string? historyFile)
    {
        if (saveSlotId < 0 || string.IsNullOrWhiteSpace(historyFile)) return;
        if (_data.SaveSnapshots.TryGetValue(LatestSaveKey(saveSlotId), out var snapshot))
            _data.SaveSnapshots[HistorySaveKey(historyFile)] = Clone(snapshot);
        Checkpoint();
    }

    internal void OnGameLoaded(int saveSlotId, string? saveFile)
    {
        CreditChangeToast.Reset();
        _batchDepth = 0;
        if (saveSlotId < 0) return;
        var key = string.IsNullOrWhiteSpace(saveFile)
            ? LatestSaveKey(saveSlotId)
            : HistorySaveKey(Path.GetFileName(saveFile));
        _data.Entries = _data.SaveSnapshots.TryGetValue(key, out var snapshot)
            ? Clone(snapshot)
            : new List<CreditLedgerEntry>();
        _pending.Clear();
        Checkpoint(); // New epoch prevents abandoned-timeline transactions being replayed.
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
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning($"[账本] 迁移旧记录失败：{ex.Message}"); }
        }
        return migrated;
    }

    private void Save()
    {
        _dirty = true;
        if (_batchDepth == 0) Flush();
    }

    internal void Flush()
    {
        if (!_dirty) return;
        // Only new entries are written on a transaction's call stack. Never
        // serialize historical save snapshots here, even for a single trade.
        try
        {
            if (_checkpointRequired || string.IsNullOrEmpty(_data.JournalGeneration))
            {
                Checkpoint();
                return;
            }
            var bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(
                new CreditLedgerJournalBatch { Generation = _data.JournalGeneration, Entries = _pending },
                Formatting.None) + "\n");
            using (var stream = new FileStream(_journalPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                // A failed/partial append must be removed before retrying.
                stream.SetLength(_committedJournalLength);
                stream.Position = _committedJournalLength;
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            _committedJournalLength += bytes.Length;
            _pending.Clear();
            _dirty = false;
        }
        catch (Exception ex) { UnityEngine.Debug.LogError($"[账本] 保存增量流水失败：{ex.Message}"); }
    }

    // Called at save/load/disable boundaries, never on an ordinary transaction.
    // Commit the new epoch BEFORE clearing the journal: interruption between
    // those steps leaves an old journal that is safely ignored on next load.
    internal void Checkpoint()
    {
        // If committing a save/load boundary fails, future writes must retry
        // the boundary, not append new-timeline entries to the old epoch.
        _checkpointRequired = true;
        _dirty = true;
        var temporary = _dataPath + ".tmp";
        var previousGeneration = _data.JournalGeneration;
        try
        {
            _data.JournalGeneration = Guid.NewGuid().ToString("N");
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
            _data.JournalGeneration = previousGeneration;
            UnityEngine.Debug.LogError($"[账本] 保存记录失败：{ex.Message}");
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            return;
        }
        _pending.Clear();
        _checkpointRequired = false;
        _dirty = false;
        try
        {
            File.WriteAllText(_journalPath, string.Empty, new UTF8Encoding(false));
            _committedJournalLength = 0;
        }
        catch (Exception ex)
        {
            // The checkpoint already committed. Old-generation lines can stay
            // in place; new appends follow them until the next checkpoint.
            _committedJournalLength = new FileInfo(_journalPath).Length;
            UnityEngine.Debug.LogWarning($"[账本] 清理已归档流水失败，将在下次归档时重试：{ex.Message}");
        }
    }

    private void LoadJournal()
    {
        if (!File.Exists(_journalPath)) return;
        var text = File.ReadAllText(_journalPath, Encoding.UTF8);
        var end = 0;
        while (end < text.Length)
        {
            var newline = text.IndexOf('\n', end);
            if (newline < 0) break; // An interrupted last write is not a committed batch.
            try
            {
                var batch = JsonConvert.DeserializeObject<CreditLedgerJournalBatch>(text.Substring(end, newline - end));
                if (batch is null || batch.Entries is null || string.IsNullOrEmpty(batch.Generation)) break;
                if (batch.Generation == _data.JournalGeneration)
                {
                    if (batch.Entries.Any(entry => entry is null)) break;
                    _data.Entries.AddRange(batch.Entries);
                    if (_data.Entries.Count > 1000) _data.Entries.RemoveRange(0, _data.Entries.Count - 1000);
                }
                end = newline + 1;
            }
            catch (JsonException) { break; }
        }
        _committedJournalLength = Encoding.UTF8.GetByteCount(text.Substring(0, end));
        if (end != text.Length)
        {
            // Remove only an incomplete/corrupt tail in this mod's own journal.
            // Valid earlier batches remain available across repeated restarts.
            using (var stream = new FileStream(_journalPath, FileMode.Open, FileAccess.Write, FileShare.Read))
                stream.SetLength(_committedJournalLength);
        }
    }

    private void Record(
        string direction, int amount, string transactionType, string detail,
        string negotiation = "", string? customerNameOverride = null,
        LedgerTradeDetails? item = null, long? balanceAfter = null)
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

        var entry = new CreditLedgerEntry
        {
            Day = StoreStation.Instance != null ? StoreStation.Instance.dayCounter : 0,
            Direction = direction,
            Amount = amount,
            CustomerName = customerName,
            TransactionType = transactionType,
            Detail = detail,
            Negotiation = negotiation,
            BalanceAfter = balanceAfter ?? PlayerStore.Instance?.playerCash,
            ItemName = item?.Name ?? string.Empty,
            ItemIdentifier = item?.Identifier ?? string.Empty,
            ItemSpecification = item?.Specification ?? string.Empty
        };
        _data.Entries.Add(entry);
        _pending.Add(entry);
        if (_data.Entries.Count > 1000) _data.Entries.RemoveRange(0, _data.Entries.Count - 1000);
        Save();
        if (direction != "支出" || item is null || !WholesaleCreditToastPatch.TryCollectItemExpense(amount))
            CreditChangeToast.Show(direction == "收入" ? amount : -amount);
    }

    private static string LatestSaveKey(int slotId) => $"slot:{slotId}";
    private static string HistorySaveKey(string fileName) => $"file:{fileName}";

    private static List<CreditLedgerEntry> Clone(IEnumerable<CreditLedgerEntry> source) => source.Select(entry => new CreditLedgerEntry
    {
        EntryId = entry.EntryId, Day = entry.Day, Direction = entry.Direction, Amount = entry.Amount,
        CustomerName = entry.CustomerName, TransactionType = entry.TransactionType,
        Detail = entry.Detail, Negotiation = entry.Negotiation,
        BalanceAfter = entry.BalanceAfter, ItemName = entry.ItemName,
        ItemIdentifier = entry.ItemIdentifier, ItemSpecification = entry.ItemSpecification
    }).ToList();

    internal static string DescribeModCashSource(long delta)
    {
        // Runtime helpers live in subnamespaces. Filtering only the entry
        // namespace used to select this very method as the money source.
        var frames = new StackTrace(false).GetFrames();
        var sourceType = frames?.Select(frame => frame.GetMethod()?.DeclaringType)
            .FirstOrDefault(type => type is not null && type != typeof(PlayerStore) &&
                type.Assembly != typeof(CreditLedgerRuntime).Assembly);
        while (sourceType?.DeclaringType is not null) sourceType = sourceType.DeclaringType;
        var type = sourceType?.Name ?? string.Empty;
        var category = type switch
        {
            "AugHelper" => "改造体结算", "CommissaryUIManager" => "兑换信用点",
            "GunOrderData" => "枪械订单结算", "LotteryHelper" => "彩票结算",
            "StartingPerkContent" => "特质结算", "StoreClientListAppraisal" => "鉴定服务结算",
            "StoreClientListSecurity" => "安保结算", "StoreClientListWanted" => "悬赏结算",
            "StoreClientListCartel" => "势力交易结算", "StoreClientListMinor" or "StoreClientListSpec" => "特殊事件结算",
            "StoreClientUniqueList" => "特殊顾客结算", "StoreRequest" => "委托结算",
            "VendingMachineHelper" or "VendingFountainHelper" => "售货机结算", "StoreClientList" => "顾客事件结算",
            _ => delta < 0 ? "其他支出" : "其他收入"
        };
        var detail = type switch
        {
            "StoreRequest" => "领取市场委托信用点奖励",
            "VendingMachineHelper" or "VendingFountainHelper" => "提取自动贩卖设备营业收入",
            _ => category is "其他收入" or "其他支出" ? "系统或其他模组的信用点变动" : category
        };
        return category + "|" + detail;
    }
}
