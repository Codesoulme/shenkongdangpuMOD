using HarmonyLib;
using AIWorldPawnshop.Compatibility;

namespace AIWorldPawnshop;

[HarmonyPatch(typeof(AppraisalServiceHelper), nameof(AppraisalServiceHelper.GetAcceptEstimateDialog))]
internal static class AppraisalItemContextPatch
{
    [ThreadStatic] private static LedgerTradeDetails? _current;
    internal static LedgerTradeDetails? Current => _current;

    private static void Prefix(GameItem __0, out LedgerTradeDetails? __state)
    {
        __state = _current;
        _current = __0 is null ? null : LedgerTradeDetails.Capture(__0);
    }

    private static Exception? Finalizer(Exception? __exception, LedgerTradeDetails? __state)
    {
        // A scoped snapshot survives the generated receipt and is cleared on
        // failure too, so a later service cannot inherit an earlier item.
        _current = __state;
        return __exception;
    }
}

[HarmonyPatch(typeof(AppraisalServiceHelper), nameof(AppraisalServiceHelper.GiveAppraisalFee))]
internal static class AppraisalServiceCreditLedgerPatch
{
    internal sealed class State
    {
        internal int Cash;
        internal LedgerTradeDetails? Item;
    }
    private static void Prefix(out State __state) => __state = new State
    {
        Cash = PlayerStore.Instance?.playerCash ?? 0,
        Item = AppraisalItemContextPatch.Current
    };
    private static void Postfix(State __state)
    {
        var delta = (long)(PlayerStore.Instance?.playerCash ?? __state.Cash) - __state.Cash;
        AIWorldPawnshopMod.Instance?.OnNativeAppraisalIncome(delta, __state.Item);
    }
}
