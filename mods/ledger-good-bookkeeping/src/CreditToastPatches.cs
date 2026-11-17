using AIWorldPawnshop.UI;
using HarmonyLib;

namespace AIWorldPawnshop;

[HarmonyPatch(typeof(NegociationUIManager), nameof(NegociationUIManager.SellMultiple))]
internal static class MultipleSaleCreditToastPatch
{
    private static void Prefix(out bool __state)
    {
        __state = false;
        CreditChangeToast.BeginBatch();
        __state = true;
    }

    private static Exception? Finalizer(Exception? __exception, bool __state)
    {
        // Release even a partially completed sale. Only amounts already
        // recorded by SellItem are present in the batch.
        if (__state) CreditChangeToast.EndBatch();
        return __exception;
    }
}

[HarmonyPatch(typeof(NegociationUIManager), nameof(NegociationUIManager.BuyMultipleItem))]
internal static class MultiplePurchaseCreditToastPatch
{
    private static void Prefix(out bool __state)
    {
        __state = false;
        CreditChangeToast.BeginBatch();
        __state = true;
    }

    private static Exception? Finalizer(Exception? __exception, bool __state)
    {
        if (__state) CreditChangeToast.EndBatch();
        return __exception;
    }
}

[HarmonyPatch(typeof(NegociationUIManager), nameof(NegociationUIManager.OnWholesaleClick))]
internal static class WholesaleCreditToastPatch
{
    private static State? _current;
    internal sealed class State
    {
        internal State? Previous;
        internal long? CashBefore;
        internal long? Paid;
        internal long ItemCosts;
        internal bool Shown;
    }

    private static void Prefix(out State __state)
    {
        __state = new State { Previous = _current, CashBefore = PlayerStore.Instance?.playerCash };
        _current = __state;
        CreditChangeToast.BeginBatch();
    }

    internal static void ObservePayment()
    {
        // The first per-item callback follows the single wholesale deduction,
        // before completion dialogue can produce unrelated income or expense.
        if (_current is null || _current.Paid.HasValue || !_current.CashBefore.HasValue || PlayerStore.Instance == null) return;
        _current.Paid = Math.Max(0L, _current.CashBefore.Value - PlayerStore.Instance.playerCash);
    }

    internal static bool TryCollectItemExpense(long amount)
    {
        if (_current is null) return false;
        _current.ItemCosts += amount;
        if (!_current.Shown && _current.Paid.HasValue)
        {
            CreditChangeToast.Show(-_current.Paid.Value);
            _current.Shown = true;
        }
        return true;
    }

    private static Exception? Finalizer(Exception? __exception, State? __state)
    {
        if (__state is null) return __exception;
        _current = __state.Previous;
        if (!__state.Shown && __state.ItemCosts > 0)
            CreditChangeToast.Show(-(__state.Paid ?? __state.ItemCosts));
        CreditChangeToast.EndBatch();
        return __exception;
    }
}
