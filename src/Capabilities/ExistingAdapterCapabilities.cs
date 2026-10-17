using AIWorldPawnshop.Models;
using AIWorldPawnshop.Runtime;

namespace AIWorldPawnshop.Capabilities;

/// <summary>
/// Phase-one bridge for the existing native adapters. It gives orchestration a
/// single, registered boundary without changing the adapters' proven behavior.
/// </summary>
internal sealed class ExistingAdapterCapabilities
{
    private readonly CapabilityRegistry _registry;

    public ExistingAdapterCapabilities(CapabilityRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public ReturnVisitApplication RequestReturnVisit(
        WorldState world,
        ConversationRequest request,
        ConversationResponse response)
    {
        Require("visit.request.self");
        return NativeReturnVisitAdapter.ApplyConversation(world, request, response);
    }

    public ProcurementApplication RequestGoods(
        WorldState world,
        ConversationRequest request,
        ConversationResponse response,
        ProcurementResolution resolution)
    {
        Require("procurement.request.goods");
        return NativeProcurementAdapter.ApplyConversation(world, request, response, resolution);
    }

    public BarterApplication OpenBarter(
        ConversationRequest request,
        ConversationResponse response)
    {
        Require("barter.open.native");
        return NativeBarterAdapter.ApplyConversation(request, response);
    }

    public CarriedInventoryApplication RevealCarriedInventory(
        WorldState world,
        ConversationRequest request,
        ConversationResponse response)
    {
        Require("inventory.reveal.carried");
        return TemporaryClientInventoryAdapter.ApplyConversation(world, request, response);
    }

    public NativeTradeApplication AdjustTrade(
        ConversationRequest request,
        ConversationResponse response)
    {
        Require("trade.adjust.selected");
        return NativeTradeAdapter.Apply(request, response);
    }

    public WalletTransferApplication TransferWallet(
        ConversationRequest request,
        ConversationResponse response)
    {
        Require("wallet.transfer.native");
        return NativeWalletAdapter.ApplyConversation(request, response);
    }

    public void RequireThirdPartyContact()
    {
        Require("visit.request.third_party");
    }

    private void Require(string capabilityKey)
    {
        if (!_registry.TryGet(capabilityKey, out var capability) ||
            !capability.Descriptor.ImplementationStatus.Equals(
                "implemented", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Capability has no native adapter: {capabilityKey}");
    }
}
