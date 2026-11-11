using AIWorldPawnshop.Runtime;
using AIWorldPawnshop.UI;
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
        Runtime = null;
        if (Instance == this) Instance = null;
    }

    internal void OnNativeSave(int slot) => Runtime?.OnSave(slot);
    internal void OnNativeSaveRotated(int slot, string? historyFile) => Runtime?.OnSaveRotated(slot, historyFile);
    internal void OnNativeGameLoaded(int slot, string? saveFile) => Runtime?.OnGameLoaded(slot, saveFile);
    internal void OnNativeItemBought(GameItem? item, int cost) => Runtime?.RecordItemBought(item, cost);
    internal void OnNativeItemSold(GameItem? item, int amount) => Runtime?.RecordItemSold(item, amount);
    internal void OnNativeInformationPurchased(int amount, string customerName) => Runtime?.RecordInformationPurchase(amount, customerName);
    internal void OnNativeCreditChanged(long delta, string type, string detail) => Runtime?.RecordCreditChange(delta, type, detail);
}
