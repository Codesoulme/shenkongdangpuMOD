using HarmonyLib;
using AIWorldPawnshop.Runtime;

namespace AIWorldPawnshop;

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.ModCash))]
internal static class PlayerStoreModCashLedgerPatch
{
    private static void Prefix(PlayerStore __instance, out int __state) => __state = __instance.playerCash;

    private static void Postfix(PlayerStore __instance, int __state)
    {
        if (CreditLedgerPatchSupport.IsNightSettlement || CreditLedgerPatchSupport.IsObservedAction) return;
        var delta = (long)__instance.playerCash - __state;
        if (delta == 0) return;
        if (LedgerCashSource.Current is { } knownSource)
        {
            AIWorldPawnshopMod.Instance?.Runtime?.RecordCashChange(delta, knownSource);
            return;
        }
        var source = CreditLedgerRuntime.DescribeModCashSource(delta).Split('|');
        CreditLedgerPatchSupport.Record(delta, source[0], source[1]);
    }
}
