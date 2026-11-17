using AIWorldPawnshop.Runtime;
using HarmonyLib;

namespace AIWorldPawnshop;

[HarmonyPatch(typeof(NegociationUIManager), nameof(NegociationUIManager.SellMultiple))]
internal static class LedgerSalePersistenceBatchPatch
{
    private static void Prefix(out CreditLedgerRuntime? __state)
    {
        __state = AIWorldPawnshopMod.Instance?.Runtime;
        __state?.BeginBatch();
    }

    private static Exception? Finalizer(Exception? __exception, CreditLedgerRuntime? __state)
    {
        // Flush completed entries even when a later item in the order failed.
        __state?.EndBatch();
        return __exception;
    }
}

// Each entry point needs its own patch class: repeated HarmonyPatch attributes
// on one class merge target metadata rather than patching three methods.
[HarmonyPatch(typeof(NegociationUIManager), nameof(NegociationUIManager.BuyMultipleItem))]
internal static class LedgerPurchasePersistenceBatchPatch
{
    private static void Prefix(out CreditLedgerRuntime? __state)
    {
        __state = AIWorldPawnshopMod.Instance?.Runtime;
        __state?.BeginBatch();
    }

    private static Exception? Finalizer(Exception? __exception, CreditLedgerRuntime? __state)
    {
        __state?.EndBatch();
        return __exception;
    }
}

[HarmonyPatch(typeof(NegociationUIManager), nameof(NegociationUIManager.OnWholesaleClick))]
internal static class LedgerWholesalePersistenceBatchPatch
{
    private static void Prefix(out CreditLedgerRuntime? __state)
    {
        __state = AIWorldPawnshopMod.Instance?.Runtime;
        __state?.BeginBatch();
    }

    private static Exception? Finalizer(Exception? __exception, CreditLedgerRuntime? __state)
    {
        __state?.EndBatch();
        return __exception;
    }
}
