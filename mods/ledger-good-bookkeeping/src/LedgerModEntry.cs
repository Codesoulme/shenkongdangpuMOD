using AIWorldPawnshop.Runtime;
using AIWorldPawnshop.UI;
using AIWorldPawnshop.Compatibility;
using UnityEngine;

namespace AIWorldPawnshop;

public sealed class AIWorldPawnshopMod : IMod
{
    internal static AIWorldPawnshopMod? Instance { get; private set; }
    internal CreditLedgerRuntime? Runtime { get; private set; }

    public void Init(ModManifest manifest) => Instance = this;

    public void OnEnable()
    {
        Instance = this;
        Runtime ??= new CreditLedgerRuntime(Path.Combine(Application.persistentDataPath, ModConstants.DataDirectoryName));
    }

    public void OnDisable()
    {
        CreditChangeToast.Reset();
        Runtime?.Checkpoint();
        Runtime = null;
        if (Instance == this) Instance = null;
    }

    internal void OnNativeSave(int slot) => Runtime?.OnSave(slot);
    internal void OnNativeSaveRotated(int slot, string? historyFile) => Runtime?.OnSaveRotated(slot, historyFile);
    internal void OnNativeGameLoaded(int slot, string? saveFile) => Runtime?.OnGameLoaded(slot, saveFile);
    internal void OnNativeItemBought(LedgerTradeDetails item, int cost, long? balanceAfter = null) => Runtime?.RecordItemBought(item, cost, balanceAfter);
    internal void OnNativeItemSold(LedgerTradeDetails item, int amount, long? balanceAfter = null) => Runtime?.RecordItemSold(item, amount, balanceAfter);
    internal void OnNativeInformationPurchased(int amount, string customerName) => Runtime?.RecordInformationPurchase(amount, customerName);
    internal void OnNativeAppraisalIncome(long delta, LedgerTradeDetails? item) => Runtime?.RecordAppraisalIncome(delta, item);
    internal void OnNativeCreditChanged(long delta, string type, string detail) => Runtime?.RecordCreditChange(delta, type, detail);
}
