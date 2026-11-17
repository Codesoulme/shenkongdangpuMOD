using System.Reflection;
using AIWorldPawnshop;
using AIWorldPawnshop.Compatibility;
using AIWorldPawnshop.Models;
using AIWorldPawnshop.UI;
using AIWorldPawnshop.Runtime;

var root = Path.Combine(Path.GetTempPath(), "LedgerRegression-" + Guid.NewGuid().ToString("N"));
var cases = new (string, Action)[]
{
    ("transactions append only new records and never rewrite snapshot archive", () =>
    {
        Sell(Bottle(), 1); Mod().OnNativeSave(1);
        var archive = File.ReadAllBytes(DataPath());
        Sell(Bottle(), 2);
        Check(archive.SequenceEqual(File.ReadAllBytes(DataPath())), "single trade rewrote archive");
        var lines = File.ReadAllLines(JournalPath());
        Check(lines.Length == 1 && !lines[0].Contains("SaveSnapshots"), "journal contains history");
        BatchOrder("SellMultiple", () => { for (var i = 0; i < 100; i++) Sell(Bottle(), 3); });
        Check(archive.SequenceEqual(File.ReadAllBytes(DataPath())) && File.ReadAllLines(JournalPath()).Length == 2, "bulk rewrote archive or appended per item");
        Check(Saved().Entries.Count == 102 && Saved().Entries.Last().Amount == 3, "durable journal replay lost entries");
    }),
    ("checkpoint committed before journal cleanup never duplicates stale transactions", () =>
    {
        Sell(Bottle(), 7); var staleJournal = File.ReadAllText(JournalPath());
        Mod().OnNativeSave(1);
        File.WriteAllText(JournalPath(), staleJournal, new System.Text.UTF8Encoding(false));
        Check(Saved().Entries.Count == 1, "stale journal replayed after committed checkpoint");
        var restarted = new CreditLedgerRuntime(DataRoot());
        restarted.RecordCreditChange(8, "测试", "新世代");
        Check(new CreditLedgerRuntime(DataRoot()).Entries.Select(e => e.Amount).SequenceEqual(new[] {7, 8}), "new generation after stale journal lost");
    }),
    ("interrupted journal tail preserves completed batches and accepts next trade", () =>
    {
        Sell(Bottle(), 7); var committed = File.ReadAllBytes(JournalPath());
        File.AppendAllText(JournalPath(), "{\"Generation\":\"unfinished中文", new System.Text.UTF8Encoding(false));
        var restarted = new CreditLedgerRuntime(DataRoot());
        Check(restarted.Entries.Single().Amount == 7 && committed.SequenceEqual(File.ReadAllBytes(JournalPath())), "partial tail lost completed batch");
        restarted.RecordCreditChange(8, "测试", "尾部恢复");
        Check(new CreditLedgerRuntime(DataRoot()).Entries.Select(e => e.Amount).SequenceEqual(new[] {7, 8}), "append after recovery lost");
    }),
    ("load switches timeline durably while retaining latest and rotated snapshots", () =>
    {
        Sell(Bottle(), 7); Mod().OnNativeSave(1);
        Mod().OnNativeSaveRotated(1, "save_1_old.es3");
        Sell(Bottle(), 8); Mod().OnNativeSave(1);
        Sell(Bottle(), 9);
        Mod().OnNativeGameLoaded(1, "save_1_old.es3");
        Check(Saved().Entries.Select(e => e.Amount).SequenceEqual(new[] {7}), "old timeline leaked into replay");
        Sell(Bottle(), 10);
        Mod().OnNativeGameLoaded(1, null);
        Check(Saved().Entries.Select(e => e.Amount).SequenceEqual(new[] {7, 8}), "latest snapshot overwritten by history load");
    }),
    ("legacy full ledger migrates without losing snapshots or item metadata", () =>
    {
        var legacy = new CreditLedgerData {
            Entries = new() { new CreditLedgerEntry { Amount = 7, ItemName = "旧瓶（大）", BalanceAfter = 107 } },
            SaveSnapshots = new() { ["slot:9"] = new() { new CreditLedgerEntry { Amount = 8, Negotiation = "事件折扣" } } }
        };
        var folder = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "CreditLedger.json"), Newtonsoft.Json.JsonConvert.SerializeObject(legacy));
        var migrated = new CreditLedgerRuntime(folder);
        Check(migrated.Entries.Single().ItemName == "旧瓶（大）", "migration metadata lost");
        migrated.RecordCreditChange(10, "测试", "迁移后"); migrated.OnGameLoaded(9, null);
        Check(new CreditLedgerRuntime(folder).Entries.Single().Negotiation == "事件折扣", "migration snapshot lost");
    }),
    ("journal replay caps current records while retaining saved history", () =>
    {
        Sell(Bottle(), 7); Mod().OnNativeSave(1);
        BatchOrder("SellMultiple", () => { for (var i = 0; i < 1100; i++) Sell(Bottle(), i + 1); });
        Check(Saved().Entries.Count == 1000 && Saved().Entries.First().Amount == 101 && Saved().Entries.Last().Amount == 1100, "replay cap differs from runtime");
        Mod().OnNativeGameLoaded(1, null);
        Check(Saved().Entries.Single().Amount == 7, "cap removed saved snapshot");
    }),
    ("failed append retains pending records and retry removes partial write without duplicates", () =>
    {
        Sell(Bottle(), 7);
        UnityEngine.Debug.AllowExpectedErrors = true; UnityEngine.Debug.Errors.Clear();
        try
        {
            using (var locked = new FileStream(JournalPath(), FileMode.Open, FileAccess.Read, FileShare.None))
                Sell(Bottle(), 8);
            Check(UnityEngine.Debug.Errors.Count == 1, "failed append not reported");
        }
        finally { UnityEngine.Debug.AllowExpectedErrors = false; }
        File.AppendAllText(JournalPath(), "partial write", new System.Text.UTF8Encoding(false));
        Sell(Bottle(), 9);
        Check(Saved().Entries.Select(e => e.Amount).SequenceEqual(new[] {7, 8, 9}), "failed append retry duplicated or lost records");
    }),
    ("failed checkpoint preserves previous generation and journal for continued recording", () =>
    {
        Sell(Bottle(), 7); var generation = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(DataPath()))["JournalGeneration"]!.ToString();
        Directory.CreateDirectory(DataPath() + ".tmp");
        UnityEngine.Debug.AllowExpectedErrors = true; UnityEngine.Debug.Errors.Clear();
        try { Mod().OnNativeSave(1); Check(UnityEngine.Debug.Errors.Count == 1, "checkpoint failure not reported"); }
        finally { UnityEngine.Debug.AllowExpectedErrors = false; Directory.Delete(DataPath() + ".tmp"); }
        Check(Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(DataPath()))["JournalGeneration"]!.ToString() == generation, "failed checkpoint committed new generation");
        Sell(Bottle(), 8);
        Check(Saved().Entries.Select(e => e.Amount).SequenceEqual(new[] {7, 8}), "checkpoint failure lost durable journal");
        Mod().OnNativeSave(1); Mod().OnNativeGameLoaded(1, null);
        Check(Saved().Entries.Count == 2, "checkpoint retry lost entries");
    }),
    ("failed load boundary retries checkpoint instead of replaying abandoned batch", () =>
    {
        Sell(Bottle(), 7); Mod().OnNativeSave(1);
        Mod().Runtime!.BeginBatch(); Sell(Bottle(), 8);
        Directory.CreateDirectory(DataPath() + ".tmp");
        UnityEngine.Debug.AllowExpectedErrors = true;
        try { Mod().OnNativeGameLoaded(1, null); }
        finally { UnityEngine.Debug.AllowExpectedErrors = false; Directory.Delete(DataPath() + ".tmp"); }
        Sell(Bottle(), 9);
        Check(Saved().Entries.Select(e => e.Amount).SequenceEqual(new[] {7, 9}), "failed load boundary resurrected old pending batch");
    }),
    ("legacy world file migration archives after runtime is initialized", () =>
    {
        var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Worlds"));
        File.WriteAllText(Path.Combine(folder, "world-id.txt"), "legacy-world");
        File.WriteAllText(Path.Combine(folder, "Worlds", "legacy-world.json"),
            "{\"CreditLedger\":[{\"Amount\":7,\"ItemName\":\"旧记录\"}],\"CreditLedgerSaveSnapshots\":{\"slot:1\":[{\"Amount\":8}]}}");
        var runtime = new CreditLedgerRuntime(folder);
        Check(runtime.Entries.Single().ItemName == "旧记录", "legacy world entry lost");
        runtime.OnGameLoaded(1, null);
        Check(new CreditLedgerRuntime(folder).Entries.Single().Amount == 8, "legacy world snapshot lost");
    }),
    ("all bulk entry points defer disk writes and nested batches flush once", () =>
    {
        Sell(Bottle(), 1);
        var before = Disk();
        BatchOrder("BuyMultipleItem", () =>
        {
            for (var i = 0; i < 100; i++) Buy(Bottle(), 2);
            BatchOrder("OnWholesaleClick", () => { Callback(Bottle(), 3); Check(Disk() == before, "nested wholesale wrote early"); });
            Check(Disk() == before, "purchase wrote early");
        });
        Check(Saved().Entries.Count == 102 && Saved().Entries.Last().BalanceAfter == 801, "nested batch not persisted");
    }),
    ("partial failed order persists completed items and releases batch", () =>
    {
        var failure = new InvalidOperationException("partial batch");
        try { BatchOrder("SellMultiple", () => { Sell(Bottle(), 7); throw failure; }); }
        catch (InvalidOperationException ex) { Check(ReferenceEquals(ex, failure), "exception replaced"); }
        Check(Saved().Entries.Single().Amount == 7, "completed item lost");
        Sell(Bottle(), 8);
        Check(Saved().Entries.Count == 2, "batch leaked");
    }),
    ("game save inside batch flushes and load restores its boundary", () =>
    {
        BatchOrder("SellMultiple", () =>
        {
            Sell(Bottle(), 7); Mod().OnNativeSave(1);
            Check(Saved().Entries.Single().Amount == 7, "game save not durable");
            Sell(Bottle(), 8);
        });
        Mod().OnNativeGameLoaded(1, null);
        Check(Entries().Single().Amount == 7 && Saved().Entries.Count == 1, "save boundary lost");
        Sell(Bottle(), 9); Check(Saved().Entries.Count == 2, "load kept batch");
    }),
    ("disable flushes pending order without losing entries", () =>
    {
        Mod().Runtime!.BeginBatch(); Sell(Bottle(), 7);
        var mod = Mod(); mod.OnDisable(); mod.OnEnable();
        Check(Entries().Single().Amount == 7 && Saved().Entries.Count == 1, "disable lost pending record");
    }),
    ("vending withdrawal records device and actual income exactly once", () =>
    {
        var machine = Bottle(); machine.name = "自动贩卖机·A"; machine.identifier = "vending_A";
        Source(typeof(VendingIncomeSourcePatch), machine, () => Cash(180));
        var entry = Entries().Single();
        Check(entry.Amount == 180 && entry.BalanceAfter == 1180 && entry.CustomerName == "自动贩卖机", "vending balance or customer");
        Check(entry.TransactionType == "自动贩卖机收入" && entry.ItemName == machine.name && entry.Detail.Contains("提取累计营业收入"), "vending source lost");
        Check(CreditLedgerUi.DetailText(Page().Entries.Single()).Contains("提取收入 180"), "vending income labeled as unit price");
        Check(CreditChangeToast.Shown.SequenceEqual(new long[] {180}), "vending duplicate toast");
        Source(typeof(VendingIncomeSourcePatch), machine, () => Cash(0));
        Check(Entries().Count == 1 && LedgerCashSource.Current is null, "empty machine recorded or context leaked");
    }),
    ("market reward records requester title full item and delivery quantity", () =>
    {
        var request = new StoreRequest { title = "紧急补水", requesterName = "绿洲公司", requiredItemCount = 6 };
        Source(typeof(MarketplaceIncomeSourcePatch), request, () => Cash(600));
        var entry = Entries().Single();
        Check(entry.Amount == 600 && entry.BalanceAfter == 1600 && entry.CustomerName == "绿洲公司", "reward balance or requester");
        Check(entry.TransactionType == "市场委托收入" && entry.Detail.Contains("紧急补水") && entry.ItemName == "瓶装水（大）" && entry.ItemSpecification == "交付数量 6", "request details missing");
        Check(CreditLedgerUi.DetailText(Page().Entries.Single()).Contains("委托奖励 600"), "reward labeled as unit price");
        Check(CreditChangeToast.Shown.SequenceEqual(new long[] {600}), "reward duplicated");
        Mod().OnNativeSave(1); Cash(10); Mod().OnNativeGameLoaded(1, null);
        Check(Entries().Single().ItemSpecification == "交付数量 6", "request snapshot lost");
    }),
    ("custom market item names retained and separate rewards never folded", () =>
    {
        var request = new StoreRequest { useCustomItemName = true, requestedItem = "指定饮品（小瓶）", requesterName = "" };
        Source(typeof(MarketplaceIncomeSourcePatch), request, () => Cash(120));
        Source(typeof(MarketplaceIncomeSourcePatch), request, () => Cash(120));
        Check(Entries().All(e => e.ItemName == "指定饮品（小瓶）" && e.CustomerName == "市场委托") && Page().Entries.Count == 2, "request folded or misnamed");
    }),
    ("missing market item definition does not block original payment", () =>
    {
        GeneralHelper.FailLookup = true;
        try { Source(typeof(MarketplaceIncomeSourcePatch), new StoreRequest(), () => Cash(100)); }
        finally { GeneralHelper.FailLookup = false; }
        Check(Entries().Single().ItemName == "water_large" && Entries().Single().Amount == 100, "missing definition prevented payout");
    }),
    ("nested and failed income scopes restore previous source", () =>
    {
        var failure = new InvalidOperationException("no payout");
        try { Source(typeof(MarketplaceIncomeSourcePatch), new StoreRequest(), () => throw failure); }
        catch (InvalidOperationException ex) { Check(ReferenceEquals(ex, failure), "source swallowed failure"); }
        Check(Entries().Count == 0 && LedgerCashSource.Current is null, "failed source leaked");
        Source(typeof(MarketplaceIncomeSourcePatch), new StoreRequest(), () =>
        {
            Source(typeof(VendingIncomeSourcePatch), Bottle(), () => Cash(20)); Cash(30);
        });
        Cash(40);
        Check(Entries()[0].TransactionType == "自动贩卖机收入" && Entries()[1].TransactionType == "市场委托收入" && Entries()[2].TransactionType == "其他收入", "nested sources mixed");
        Check(!Entries().Last().Detail.Contains("DescribeModCashSource"), "unknown source exposes method");
    }),
    ("night and observed settlement suppress duplicate cash entries", () =>
    {
        CreditLedgerPatchSupport.IsNightSettlement = true;
        Source(typeof(VendingIncomeSourcePatch), Bottle(), () => Cash(30));
        CreditLedgerPatchSupport.IsNightSettlement = false;
        CreditLedgerPatchSupport.IsObservedAction = true;
        Source(typeof(MarketplaceIncomeSourcePatch), new StoreRequest(), () => Cash(40));
        CreditLedgerPatchSupport.IsObservedAction = false;
        Check(Entries().Count == 0 && LedgerCashSource.Current is null, "suppressed settlement duplicated");
        Cash(50); Check(Entries().Single().Amount == 50, "suppression leaked");
    }),
    ("legacy invalid source is readable without inventing historical details", () =>
    {
        var old = new CreditLedgerEntry { Day = 2, Direction = "收入", Amount = 100, TransactionType = "其他收入", Detail = "CreditLedgerRuntime · DescribeModCashSource" };
        var row = CreditLedgerUi.Read(new[] {old}, 2, 0, 4).Entries.Single();
        Check(CreditLedgerUi.DetailText(row) == "历史记录未保存信用点来源" && old.Detail.Contains("CreditLedgerRuntime"), "historical source invented or modified");
    }),
    ("bulk sale defers whole-ledger writes until the order ends", () =>
    {
        Sell(Bottle(), 1);
        var path = Path.Combine(UnityEngine.Application.persistentDataPath, ModConstants.DataDirectoryName, "CreditLedger.json");
        var before = File.ReadAllText(path);
        BatchOrder("SellMultiple", () =>
        {
            for (var i = 0; i < 100; i++)
            {
                Sell(Bottle(), 10);
                Check(File.ReadAllText(path) == before, "whole ledger rewritten during bulk sale");
            }
            Check(Entries().Count == 101, "in-memory bulk records lost");
        });
        var saved = Saved();
        Check(saved.Entries.Count == 101 && saved.Entries.Last().BalanceAfter == 2001, "batch not flushed");
    }),
    ("cash source does not identify the ledger as its own caller", () =>
    {
        var source = Mod().Runtime!.GetType().GetMethod("DescribeModCashSource", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { 100L })!.ToString()!;
        Check(!source.Contains("CreditLedgerRuntime") && !source.Contains("DescribeModCashSource"), "source detected its own runtime method: " + source);
    }),
    ("appraisal identifies the accepted item instead of the generated slip", () =>
    {
        var item = Bottle(250000, 500000);
        Appraise(item, () => GiveFee(120));
        var entry = Entries().Single();
        Check(entry.ItemName == "瓶装水（大）" && entry.ItemIdentifier == "water_large" && entry.ItemSpecification == "250 / 500 ml", "appraised item missing");
        Check(entry.Amount == 120 && entry.BalanceAfter == 1120 && entry.TransactionType == "鉴定服务收入", "appraisal settlement changed");
        Check(CreditLedgerUi.ItemText(Page().Entries.Single()).Contains("瓶装水（大）"), "item column missing");
        Check(CreditLedgerUi.DetailText(Page().Entries.Single()).Contains("鉴定服务费 120"), "service fee labeled as sale price");
        Check(entry.Negotiation == "", "item market modifiers are not appraisal fee modifiers");
    }),
    ("appraisal snapshot survives item mutation and later save load", () =>
    {
        var item = Bottle();
        item.itemFeatures.Add(Feature("只影响物品的市场事件", 30, ItemFeature.FeatureType.Event));
        Appraise(item, () => { item.name = "被改变的物品名"; GiveFee(80); });
        Mod().OnNativeSave(1); Sell(Bottle(), 20); Mod().OnNativeGameLoaded(1, null);
        var entry = Entries().Single();
        Check(entry.ItemName == "瓶装水（大）" && entry.ItemSpecification == "500 / 500 ml" && entry.BalanceAfter == 1080 && entry.Negotiation == "", "appraisal snapshot lost");
    }),
    ("two appraisals keep their own item and do not fold service fees", () =>
    {
        Appraise(Bottle(), () => GiveFee(100));
        var other = Bottle(); other.name = "瓶装水（小）"; other.identifier = "water_small";
        Appraise(other, () => GiveFee(100)); Appraise(other, () => GiveFee(100));
        Check(Entries().Count == 3 && Page().Entries.Count == 3 && Entries()[0].ItemName == "瓶装水（大）" && Entries()[1].ItemName == "瓶装水（小）", "appraisal items reused or folded");
        Check(CreditChangeToast.Shown.SequenceEqual(new long[] { 100, 100, 100 }), "duplicate appraisal income toast");
    }),
    ("appraisal without payment creates no entry", () =>
    {
        Appraise(Bottle(), () => GiveFee(0));
        Check(Entries().Count == 0 && CreditChangeToast.Shown.Count == 0, "unpaid appraisal recorded");
    }),
    ("failed appraisal clears context without inventing income", () =>
    {
        var failure = new InvalidOperationException("appraisal failed");
        try { Appraise(Bottle(), () => throw failure); }
        catch (InvalidOperationException ex) { Check(ReferenceEquals(ex, failure), "failure changed"); }
        Check(Entries().Count == 0 && AppraisalItemContextPatch.Current is null, "failed appraisal leaked");
        GiveFee(30);
        Check(Entries().Single().ItemName == "", "unrelated fee inherited old item");
    }),
    ("nested appraisal restores outer item context", () =>
    {
        var inner = Bottle(); inner.name = "另一件物品";
        Appraise(Bottle(), () => { Appraise(inner, () => GiveFee(20)); GiveFee(40); });
        Check(Entries()[0].ItemName == "另一件物品" && Entries()[1].ItemName == "瓶装水（大）" && AppraisalItemContextPatch.Current is null, "nested context restore");
    }),
    ("direct appraisal fee without item retains income without guessing from receipt", () =>
    {
        GiveFee(50);
        var entry = Entries().Single();
        Check(entry.ItemName == "" && entry.Amount == 50 && entry.BalanceAfter == 1050, "fallback service fee");
        Check(CreditLedgerUi.ItemText(Page().Entries.Single()) == "—", "fallback guessed wrong item");
    }),
    ("normal purchase records post-payment balance and pre-cleanup modifiers", () =>
    {
        var item = Bottle(250000, 500000);
        item.itemFeatures.Add(Feature("水价下跌", -20, ItemFeature.FeatureType.Event));
        item.itemFeatures.Add(Feature("压价", -10, stage: ItemFeature.ValueStage.Final));
        Buy(item, 72);
        var entry = Entries().Single();
        Check(entry.BalanceAfter == 928 && entry.Amount == 72, "purchase balance");
        Check(entry.Negotiation.Contains("事件·水价下跌 折扣 20%") && entry.Negotiation.Contains("议价·压价 折扣 10%"), "lost modifiers");
        Check(entry.ItemName == "瓶装水（大）" && entry.ItemSpecification == "250 / 500 ml", "container identity");
    }),
    ("sale records post-payment balance and all active sources", () =>
    {
        var item = Bottle(1000000, 1000000);
        item.itemFeatures.Add(Feature("水价上涨", 30, ItemFeature.FeatureType.Event));
        item.itemFeatures.Add(Feature("顾客偏好", 15, ItemFeature.FeatureType.ClientTemporary, ItemFeature.ValueStage.Market));
        item.itemFeatures.Add(Feature("抬价", 10, stage: ItemFeature.ValueStage.Final));
        Sell(item, 140);
        var entry = Entries().Single();
        Check(entry.BalanceAfter == 1140, "sale balance");
        Check(entry.Negotiation.Contains("事件·水价上涨 加价 30%") && entry.Negotiation.Contains("顾客偏好 加价 15%") && entry.Negotiation.Contains("议价·抬价 加价 10%"), "price sources");
    }),
    ("disabled and zero modifiers omitted", () =>
    {
        var item = Bottle();
        var disabled = Feature("未生效抬价", 50); disabled.isDisabled = true;
        item.itemFeatures.Add(disabled); item.itemFeatures.Add(Feature("未应用", 0));
        Check(LedgerTradeDetails.Capture(item).PriceModifiers == "", "inactive modifier shown");
    }),
    ("unaccepted bargain does not leak across items", () =>
    {
        Sell(Bottle(), 100);
        Check(Entries().Single().Negotiation == "", "unapplied bargain recorded");
    }),
    ("wholesale balance is settled once", () =>
    {
        PlayerStore.Instance!.currentClientInstance!.Client.wholesaleDiscount = 10;
        PlayerStore.Instance.currentClientInstance.Client.isOfferWholesale = true;
        PlayerStore.Instance.playerCash -= 180;
        Callback(Bottle(), 90); Callback(Bottle(), 90);
        Check(Entries().Count == 2 && Entries().All(e => e.BalanceAfter == 820), "wholesale balance");
        Check(Entries().All(e => e.Negotiation.Contains("批发折扣 10%")), "wholesale source");
        var row = Page().Entries.Single();
        Check(row.Amount == 180 && row.FoldCount == 2 && row.BalanceAfter == 820, "wholesale fold");
    }),
    ("wholesale runtime keeps ledger items but emits actual payment once", () =>
    {
        var state = Call(typeof(WholesaleCreditToastPatch), "Prefix", (object?)null);
        try
        {
            PlayerStore.Instance!.playerCash -= 181;
            Callback(Bottle(), 90); Callback(Bottle(), 90);
        }
        finally { Invoke(typeof(WholesaleCreditToastPatch), "Finalizer", null, state); }
        Check(Entries().Count == 2 && Entries().All(e => e.Amount == 90 && e.BalanceAfter == 819), "item records changed");
        Check(CreditChangeToast.Shown.SequenceEqual(new long[] { -181 }), "wholesale runtime did not route to order total");
    }),
    ("generic income and expense have actual balances", () =>
    {
        PlayerStore.Instance!.playerCash += 30;
        Mod().OnNativeCreditChanged(30, "事件收入", "任务奖励");
        PlayerStore.Instance.playerCash -= 10;
        Mod().OnNativeInformationPurchased(10, "情报商");
        Check(Entries()[0].BalanceAfter == 1030 && Entries()[1].BalanceAfter == 1020, "generic balance");
    }),
    ("equal names with different prices never fold", () =>
    {
        Sell(Bottle(), 100); Sell(Bottle(), 120);
        Check(Page().Entries.Count == 2, "different prices folded");
    }),
    ("identical adjacent sales fold with last closing balance", () =>
    {
        Sell(Bottle(), 100); Sell(Bottle(), 100); Sell(Bottle(), 100);
        var row = Page().Entries.Single();
        Check(row.Amount == 300 && row.UnitPrice == 100 && row.FoldCount == 3 && row.BalanceAfter == 1300, "fold closing balance");
        Check(CreditLedgerUi.DetailText(row).Contains("成交单价 100"), "unit price missing");
    }),
    ("different fill levels never fold", () =>
    {
        Sell(Bottle(100000, 500000), 100); Sell(Bottle(200000, 500000), 100);
        Check(Page().Entries.Count == 2, "different contents folded");
    }),
    ("different container sizes and identifiers never fold", () =>
    {
        var small = Bottle(100000, 100000); small.name = "瓶装水（小）"; small.identifier = "water_small";
        Sell(small, 100); Sell(Bottle(), 100);
        Check(Page().Entries.Count == 2 && Page().Entries[1].ItemName == "瓶装水（小）", "container size lost");
    }),
    ("different price sources never fold", () =>
    {
        var eventItem = Bottle(); eventItem.itemFeatures.Add(Feature("水价上涨", 10, ItemFeature.FeatureType.Event));
        Sell(eventItem, 100); Sell(Bottle(), 100);
        Check(Page().Entries.Count == 2, "different sources folded");
    }),
    ("separated item trades keep chronological order", () =>
    {
        Sell(Bottle(), 100);
        PlayerStore.Instance!.playerCash -= 20; Mod().OnNativeCreditChanged(-20, "付款", "服务");
        Sell(Bottle(), 100);
        var rows = Page().Entries;
        Check(rows.Count == 3 && rows[0].BalanceAfter == 1180 && rows[1].BalanceAfter == 1080 && rows[2].BalanceAfter == 1100, "chronology");
    }),
    ("non-item payments are not folded", () =>
    {
        Mod().OnNativeCreditChanged(-10, "付款", "服务"); Mod().OnNativeCreditChanged(-10, "付款", "服务");
        Check(Page().Entries.Count == 2, "payments folded");
    }),
    ("save restore clones all new fields", () =>
    {
        var item = Bottle(); item.itemFeatures.Add(Feature("事件", -20, ItemFeature.FeatureType.Event));
        Buy(item, 80); Mod().OnNativeSave(1); Sell(Bottle(), 100);
        Mod().OnNativeGameLoaded(1, null);
        var entry = Entries().Single();
        Check(entry.BalanceAfter == 920 && entry.ItemName == "瓶装水（大）" && entry.ItemSpecification == "500 / 500 ml" && entry.Negotiation.Contains("折扣 20%"), "snapshot lost fields");
        var mod = Mod(); mod.OnDisable(); mod.OnEnable();
        Check(Entries().Single().BalanceAfter == 920 && Entries().Single().ItemIdentifier == "water_large", "disk round trip");
    }),
    ("old records preserve size and leave missing balance unknown", () =>
    {
        var old = new CreditLedgerEntry { Day = 2, Direction = "收入", Amount = 100, TransactionType = "售予顾客", Detail = "瓶装水（大）（water_large）" };
        var row = CreditLedgerUi.Read(new[] { old }, 2, 0, 4).Entries.Single();
        Check(row.ItemName == "瓶装水（大）" && row.BalanceAfter == null, "legacy size or balance");
    }),
    ("old non-item detail parentheses preserved", () =>
    {
        var old = new CreditLedgerEntry { Day = 2, Direction = "收入", Amount = 100, TransactionType = "事件", Detail = "任务奖励（额外部分）" };
        var row = CreditLedgerUi.Read(new[] { old }, 2, 0, 4).Entries.Single();
        Check(row.Detail == old.Detail && row.ItemName == "", "non-item detail altered");
    }),
    ("small liquid volumes retain fractions of ml", () =>
        Check(LedgerTradeDetails.Capture(Bottle(125, 1000)).Specification == "0.125 / 1 ml", "fraction truncated")),
    ("empty bottle retains capacity", () =>
        Check(LedgerTradeDetails.Capture(Bottle(0, 500000)).Specification == "0 / 500 ml", "empty capacity")),
    ("energy storage records current and maximum energy", () =>
    {
        var item = new GameItem(); item.Tags["power_source_item"] = 1; item.Tags["power_source_item_energy"] = 20; item.Tags["power_source_item_max_energy"] = 100;
        Check(LedgerTradeDetails.Capture(item).Specification == "20 / 100 kJ", "energy capacity");
    }),
    ("child prices use live event source without duplicating stale features", () =>
    {
        var parent = new GameItem(); var child = Bottle(); child.Tags["WATER"] = 1;
        child.itemFeatures.Add(Feature("过期事件", 5, ItemFeature.FeatureType.Event)); parent.Children.Add(child);
        StoreStation.Instance!.storeEventManager.Events.Add(new StoreEvent { negociationDatas = { new NegociationData { value = 30 } } });
        var text = LedgerTradeDetails.Capture(parent).PriceModifiers;
        Check(text.Contains("水价事件 加价 30%") && !text.Contains("过期事件"), "child event pricing");
    }),
    ("locked child and ignored contents are not price sources", () =>
    {
        var parent = new GameItem(); var child = Bottle(); child.Tags["ITEM_BEHIND_LOCK_TAG"] = 1;
        child.itemFeatures.Add(Feature("锁内物品", 50)); parent.Children.Add(child);
        Check(LedgerTradeDetails.Capture(parent).PriceModifiers == "", "locked child included");
        child.Tags.Remove("ITEM_BEHIND_LOCK_TAG"); parent.Tags["IGNORE_CHILD_VALUE"] = 1;
        Check(LedgerTradeDetails.Capture(parent).PriceModifiers == "", "ignored child included");
    }),
    ("implicit child retail markup is recorded once", () =>
    {
        var parent = new GameItem(); var child = Bottle(); child.ApplyMarkup = true; parent.Children.Add(child);
        Check(LedgerTradeDetails.Capture(parent).PriceModifiers.Contains("零售加价 15%"), "implicit retail markup omitted");
        child.itemFeatures.Add(new ItemFeature { identifier = "retailMarkUp", Label = "零售", Modifier = 15 });
        Check(!LedgerTradeDetails.Capture(parent).PriceModifiers.Contains("零售加价 15%"), "retail markup repeated");
    }),
    ("pagination keeps daily totals and excludes yesterday", () =>
    {
        for (var i = 1; i <= 6; i++) Sell(Bottle(), i);
        StoreStation.Instance!.dayCounter = 1; Sell(Bottle(), 100);
        var page = CreditLedgerUi.Read(Entries(), 2, 99, 4);
        Check(page.Page == 1 && page.PageCount == 2 && page.Income == 21 && page.TotalCount == 6 && page.Entries.Count == 2, "pagination");
    }),
    ("failed purchase releases scope and records no fake payment", () =>
    {
        var state = Call(typeof(PlayerStoreBuyItemLedgerPatch), "Prefix", PlayerStore.Instance, Bottle(), null);
        var failure = new Exception("failed trade");
        Check(ReferenceEquals(Invoke(typeof(PlayerStoreBuyItemLedgerPatch), "Finalizer", PlayerStore.Instance, failure, state), failure), "exception swallowed");
        Check(!PlayerStoreBuyItemLedgerPatch.IsBuying && Entries().Count == 0, "purchase scope leaked");
        Callback(Bottle(), 50); Check(Entries().Count == 1, "callback disabled after failure");
    }),
    ("payment is recorded if later inventory transfer fails", () =>
    {
        var state = Call(typeof(PlayerStoreBuyItemLedgerPatch), "Prefix", PlayerStore.Instance, Bottle(), null);
        PlayerStore.Instance!.playerCash -= 80;
        var failure = new Exception("inventory transfer failed");
        Check(ReferenceEquals(Invoke(typeof(PlayerStoreBuyItemLedgerPatch), "Finalizer", PlayerStore.Instance, failure, state), failure), "exception changed");
        Check(Entries().Single().BalanceAfter == 920 && Entries().Single().Amount == 80 && !PlayerStoreBuyItemLedgerPatch.IsBuying, "real spending lost");
    }),
};
try
{
    var passed = 0;
    foreach (var (name, test) in cases)
    {
        UnityEngine.Application.persistentDataPath = Path.Combine(root, Guid.NewGuid().ToString("N"));
        PlayerStore.Instance = new PlayerStore(); StoreStation.Instance = new StoreStation();
        var mod = new AIWorldPawnshopMod(); mod.Init(new ModManifest()); mod.OnEnable();
        try
        {
            if (args.Contains("--benchmark"))
            {
                Benchmark(); break;
            }
            if (args.Contains("--source") && !name.StartsWith("cash source")) continue;
            test(); passed++; Console.WriteLine("PASS " + name);
        }
        finally { mod.OnDisable(); }
    }
    if (!args.Contains("--benchmark")) Console.WriteLine($"{passed} ledger cases passed using production recording, trade patches, persistence and presentation sources.");
}
finally
{
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(root).StartsWith("LedgerRegression-", StringComparison.Ordinal) && Directory.Exists(root))
        Directory.Delete(root, true);
}
AIWorldPawnshopMod Mod() => AIWorldPawnshopMod.Instance!;
IReadOnlyList<CreditLedgerEntry> Entries() => Mod().Runtime!.Entries;
CreditLedgerUi.PageData Page() => CreditLedgerUi.Read(Entries(), 2, 0, 20);
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
GameItem Bottle(int current = 500000, int capacity = 500000)
{
    var item = new GameItem(); item.Tags["LIQUID_CONTAINER_TAG"] = 1;
    item.Tags["LIQUID_CONTAINER_CAPACITY"] = capacity; item.Tags["TEST_CURRENT_UL"] = current;
    return item;
}
ItemFeature Feature(string name, int value, ItemFeature.FeatureType type = ItemFeature.FeatureType.Normale, ItemFeature.ValueStage stage = ItemFeature.ValueStage.Market) =>
    new() { Label = name, Modifier = value, featureType = type, Stage = stage };
object? Invoke(Type type, string method, params object?[] args) => type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
object? Call(Type type, string method, params object?[] args) { Invoke(type, method, args); return args[^1]; }
void Callback(GameItem item, int cost)
{
    var state = Call(typeof(PlayerStoreItemBoughtPatch), "Prefix", item, null);
    item.itemFeatures.Clear();
    Invoke(typeof(PlayerStoreItemBoughtPatch), "Postfix", PlayerStore.Instance, cost, state);
}
void Buy(GameItem item, int cost)
{
    var state = Call(typeof(PlayerStoreBuyItemLedgerPatch), "Prefix", PlayerStore.Instance, item, null);
    try
    {
        Callback(item, cost); PlayerStore.Instance!.playerCash -= cost;
    }
    finally { Invoke(typeof(PlayerStoreBuyItemLedgerPatch), "Finalizer", PlayerStore.Instance, null, state); }
}
void Sell(GameItem item, int income)
{
    var state = Call(typeof(PlayerStoreItemSoldLedgerPatch), "Prefix", PlayerStore.Instance, item, null);
    item.itemFeatures.Clear(); PlayerStore.Instance!.playerCash += income;
    Invoke(typeof(PlayerStoreItemSoldLedgerPatch), "Postfix", PlayerStore.Instance, state);
}
void Appraise(GameItem item, Action action)
{
    var patch = typeof(AppraisalServiceCreditLedgerPatch).Assembly.GetTypes().FirstOrDefault(type =>
        type.GetCustomAttributes<HarmonyLib.HarmonyPatch>().Any(a => a.Type == typeof(AppraisalServiceHelper) && a.Name == "GetAcceptEstimateDialog"));
    if (patch is null) { action(); return; }
    var state = Call(patch, "Prefix", item, null);
    Exception? failure = null;
    try { action(); }
    catch (Exception ex) { failure = ex; }
    var returned = Invoke(patch, "Finalizer", failure, state);
    Check(ReferenceEquals(returned, failure), "appraisal exception swallowed");
    if (failure is not null) throw failure;
}
void GiveFee(int amount)
{
    var state = Call(typeof(AppraisalServiceCreditLedgerPatch), "Prefix", (object?)null);
    PlayerStore.Instance!.playerCash += amount;
    // GiveAppraisalFee adds a paper slip after crediting cash. The ledger
    // must use the accepted item's snapshot rather than a mutable selection.
    PlayerStore.Instance.currentNegociatedItem = new GameItem { identifier = "appraisal_slip", name = "鉴定单" };
    Invoke(typeof(AppraisalServiceCreditLedgerPatch), "Postfix", state);
}
void BatchOrder(string name, Action action)
{
    var patch = typeof(PlayerStoreBuyItemLedgerPatch).Assembly.GetTypes().FirstOrDefault(type =>
        type.Name.Contains("PersistenceBatchPatch") && type.GetCustomAttributes<HarmonyLib.HarmonyPatch>()
            .Any(a => a.Type == typeof(NegociationUIManager) && a.Name == name));
    if (patch is null) { action(); return; }
    var state = Call(patch, "Prefix", (object?)null);
    Exception? failure = null;
    try { action(); } catch (Exception ex) { failure = ex; }
    Check(ReferenceEquals(Invoke(patch, "Finalizer", failure, state), failure), "batch changed exception");
    if (failure is not null) throw failure;
}
string DataRoot() => Path.Combine(UnityEngine.Application.persistentDataPath, ModConstants.DataDirectoryName);
string DataPath() => Path.Combine(DataRoot(), "CreditLedger.json");
string JournalPath() => Path.Combine(DataRoot(), "CreditLedger.transactions.jsonl");
string Disk() => File.ReadAllText(DataPath()) + "\n" + File.ReadAllText(JournalPath());
CreditLedgerData Saved() => new() { Entries = new CreditLedgerRuntime(DataRoot()).Entries.ToList() };
void Cash(int delta)
{
    var state = Call(typeof(PlayerStoreModCashLedgerPatch), "Prefix", PlayerStore.Instance, null);
    PlayerStore.Instance!.playerCash += delta;
    Invoke(typeof(PlayerStoreModCashLedgerPatch), "Postfix", PlayerStore.Instance, state);
}
void Source(Type patch, object source, Action action)
{
    var state = Call(patch, "Prefix", source, null);
    Exception? failure = null;
    try { action(); } catch (Exception ex) { failure = ex; }
    Check(ReferenceEquals(Invoke(patch, "Finalizer", failure, state), failure), "source changed exception");
    if (failure is not null) throw failure;
}
void Benchmark()
{
    if (args.Contains("--performance")) { PerformanceMatrix(); return; }
    for (var i = 0; i < 100; i++) Sell(Bottle(), 1);
    for (var i = 0; i < 8; i++) Mod().OnNativeSave(i);
    var timer = System.Diagnostics.Stopwatch.StartNew();
    BatchOrder("SellMultiple", () => { for (var i = 0; i < 100; i++) Sell(Bottle(), 10); });
    timer.Stop();
    Console.WriteLine($"BENCH 100 item sale / 8 snapshots of 100 entries: {timer.Elapsed.TotalMilliseconds:F1} ms; ledger entries {Entries().Count}");
}

void PerformanceMatrix()
{
    var fixture = args.SkipWhile(a => a != "--fixture").Skip(1).FirstOrDefault();
    var template = fixture is null ? new CreditLedgerData() :
        Newtonsoft.Json.JsonConvert.DeserializeObject<CreditLedgerData>(File.ReadAllText(fixture))!;
    if (template.Entries.Count == 0)
        template.Entries.Add(new CreditLedgerEntry { Day = 2, Amount = 10, Direction = "收入", ItemName = "瓶装水（大）", ItemSpecification = "500 / 500 ml", Negotiation = "事件·水价上涨 加价 20%" });
    var scenarios = new List<(string, CreditLedgerData)> { ("actual fixture", template) };
    foreach (var count in new[] {0, 33, 100})
        scenarios.Add(($"{count} snapshots x 1000", new CreditLedgerData {
            Entries = Enumerable.Range(0, 126).Select(i => template.Entries[i % template.Entries.Count]).ToList(),
            SaveSnapshots = Enumerable.Range(0, count).ToDictionary(i => "slot:" + i,
                i => Enumerable.Range(0, 1000).Select(j => template.Entries[j % template.Entries.Count]).ToList())
        }));
    foreach (var (name, data) in scenarios)
    {
        var activeMod = Mod(); activeMod.OnDisable();
        var path = Path.Combine(UnityEngine.Application.persistentDataPath, ModConstants.DataDirectoryName, "CreditLedger.json");
        File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented));
        activeMod.OnEnable();
        Sell(Bottle(), 1); // JIT, serializer and filesystem warmup outside measurements.
        var bytes = new FileInfo(path).Length;
        var serialization = Measure(() => Newtonsoft.Json.JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented));
        var payload = Newtonsoft.Json.JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented);
        var probe = Path.Combine(UnityEngine.Application.persistentDataPath, "write-probe.json");
        File.WriteAllText(probe, payload, System.Text.Encoding.UTF8);
        var write = Measure(() => {
            File.WriteAllText(probe + ".tmp", payload, System.Text.Encoding.UTF8);
            File.Replace(probe + ".tmp", probe, probe + ".bak", true);
        });
        var single = Measure(() => Sell(Bottle(), 10));
        var batch = Measure(() => BatchOrder("SellMultiple", () => { for (var i = 0; i < 100; i++) Sell(Bottle(), 10); }));
        Console.WriteLine($"PERF {name}: JSON={bytes:N0} bytes; single {single}; batch100 {batch}");
        Console.WriteLine($"PERF {name}: serialize-only {serialization}; atomic-write-only {write}");
    }
    Console.WriteLine("PERF capture100: " + Measure(() => { for (var i = 0; i < 100; i++) LedgerTradeDetails.Capture(Bottle()); }));
    Console.WriteLine("PERF source100: " + Measure(() => { for (var i = 0; i < 100; i++) CreditLedgerRuntime.DescribeModCashSource(10); }));
}
string Measure(Action action)
{
    var times = new List<double>(); var allocations = new List<long>();
    for (var sample = 0; sample < 9; sample++)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var timer = System.Diagnostics.Stopwatch.StartNew(); action(); timer.Stop();
        times.Add(timer.Elapsed.TotalMilliseconds); allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before);
    }
    times.Sort(); allocations.Sort();
    return $"median={times[4]:F2}ms max={times[^1]:F2}ms alloc={allocations[4]:N0}B";
}
