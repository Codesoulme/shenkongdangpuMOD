using AIWorldPawnshop.Runtime;
using HarmonyLib;
using AIWorldPawnshop.Compatibility;

namespace AIWorldPawnshop.UI;

[HarmonyPatch(typeof(NegociationUIManager), nameof(NegociationUIManager.UpdateUI), new[] { typeof(GameItem) })]
internal static class NegotiationAgreementItemUiPatch
{
    private const string Marker = "\n\n对话约定：\n";

    private static void Postfix(NegociationUIManager __instance, GameItem gameItem)
    {
        var header = FormalGameAccess.GetNegotiationSubtitle(__instance);
        if (header is null)
            return;

        var text = header.text ?? string.Empty;
        var markerIndex = text.IndexOf(Marker, StringComparison.Ordinal);
        if (markerIndex >= 0)
            text = text[..markerIndex];
        var section = BargainAgreementRegistry.BuildUiSection(gameItem);
        header.text = text + section;
    }
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.PlacedItemForSelling))]
internal static class ConversationAgreementSellingItemPatch
{
    private static void Postfix(GameItem targetItem) =>
        BargainAgreementRegistry.ApplyToNewItem(targetItem, "player_sells_to_client");
}

[HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.PlacedItemForBuying))]
internal static class ConversationAgreementBuyingItemPatch
{
    private static void Postfix(GameItem targetItem) =>
        BargainAgreementRegistry.ApplyToNewItem(targetItem, "player_buys_from_client");
}
