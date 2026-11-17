// Minimal deterministic host; models game trade ordering, not rendering.
public sealed class GameItem
{
    public string identifier = "water_large";
    public string name = "瓶装水（大）";
    public int unitCount = 1;
    public readonly List<ItemFeature> itemFeatures = new();
    public readonly List<GameItem> Children = new();
    public readonly Dictionary<string, int> Tags = new();
    public bool ApplyMarkup;
    public string GetDisplayName() => name;
    public bool IsTag(string tag) => Tags.ContainsKey(tag);
    public TagState GetTagReadonly(string tag) => new(Tags.GetValueOrDefault(tag));
    public List<GameItem> FindAllChildItems() => Children;
    public bool IsApplyMarkUp() => ApplyMarkup;
    public ItemFeature? FindItemFeatureByID(string id) => itemFeatures.FirstOrDefault(f => f.identifier == id);
}
public sealed class TagState
{
    private readonly int _value;
    public TagState(int value) => _value = value;
    public int GetInt() => _value;
}
public sealed class ItemFeature
{
    public enum FeatureType { Normale, Event, Special, Temporary, TemporaryBuying, TemporarySelling, ClientTemporary }
    public enum ValueStage { Innate, Market, Final }
    public bool isDisabled;
    public FeatureType featureType;
    public ValueStage Stage;
    public int Modifier;
    public string Label = "价格修正";
    public string identifier = "test";
    public int GetCurrentValueMod() => Modifier;
    public ValueStage GetValueStage() => Stage;
    public string GetBaseCurrentDisplay() => Label;
}
public sealed class StoreClientInstance
{
    public StoreClient Client = new();
    public StoreClient GetClientBlueprint() => Client;
}
public sealed class StoreClient
{
    public string realName = "测试顾客";
    public string displayName = "顾客";
    public int wholesaleDiscount;
    public bool isOfferWholesale;
}
public sealed class StoreStation
{
    public static StoreStation? Instance;
    public int dayCounter = 2;
    public StoreEventManager storeEventManager = new();
}
public sealed class StoreEventManager
{
    public List<StoreEvent> Events = new();
    public List<StoreEvent> GetActiveEvents() => Events;
}
public sealed class StoreEvent { public List<NegociationData> negociationDatas = new(); }
public sealed class NegociationData { public int value; public string itemType = "WATER"; public string reason = "水价事件"; }
public sealed class PlayerStore
{
    public static PlayerStore? Instance;
    public int playerCash = 1000;
    public int retailMarkup = 15;
    public StoreClientInstance? currentClientInstance = new();
    public GameItem? currentNegociatedItem;
    public void BuyItem() { }
    public void OnItemBought() { }
    public void SellItem() { }
    public void ModCash() { }
}
public static class VendingMachineHelper { public static void TransferCashToPlayer() { } }
public sealed class StoreRequest
{
    public string title = "市场订单", requesterName = "委托人", requiredItem = "water_large", requestedItem = "";
    public bool useCustomItemName;
    public int requiredItemCount = 3;
    public void DispenseReward() { }
}
public static class GeneralHelper
{
    public static bool FailLookup;
    public static string GetDisplayNameFromID(string id) => FailLookup ? throw new Exception("missing definition") : "瓶装水（大）";
}
public static class WaterHelper
{
    public static int GetCurrentCapacityUL(GameItem item) => item.Tags.GetValueOrDefault("TEST_CURRENT_UL");
    public static int GetTotalCapacity(GameItem item) => item.Tags.GetValueOrDefault("LIQUID_CONTAINER_CAPACITY");
}
public sealed class BargainUIManager
{
    public bool MarkupOffered => true;
    public bool BuyingDiscountOffered => true;
}
public interface IMod { }
public sealed class ModManifest { }
public static class AppraisalServiceHelper
{
    public static void GetAcceptEstimateDialog() { }
    public static void GiveAppraisalFee() { }
}
public class NegociationUIManager
{
    public void SellMultiple() { }
    public void BuyMultipleItem() { }
    public void OnWholesaleClick() { }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class HarmonyPatch : Attribute
    {
        public Type Type; public string Name;
        public HarmonyPatch(Type type, string name) { Type = type; Name = name; }
    }
}
namespace UnityEngine
{
    public static class Application { public static string persistentDataPath = ""; }
    public static class Debug
    {
        public static bool AllowExpectedErrors;
        public static readonly List<string> Errors = new();
        public static void LogWarning(string text) => throw new Exception(text);
        public static void LogError(string text) { if (!AllowExpectedErrors) throw new Exception(text); Errors.Add(text); }
    }
}
namespace AIWorldPawnshop
{
    internal static class CreditLedgerPatchSupport
    {
        internal static bool IsNightSettlement;
        internal static bool IsObservedAction;
        internal static void Record(long delta, string type, string detail) =>
            AIWorldPawnshopMod.Instance?.OnNativeCreditChanged(delta, type, detail);
    }
}
namespace AIWorldPawnshop.UI
{
    internal static class CreditChangeToast
    {
        internal static readonly List<long> Shown = new();
        internal static void Show(long delta) { if (delta != 0) Shown.Add(delta); }
        internal static void Reset() => Shown.Clear();
        internal static void BeginBatch() { }
        internal static void EndBatch(long? expense = null) { }
    }
}
