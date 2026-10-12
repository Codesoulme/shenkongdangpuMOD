using System.Text.Json.Serialization;

namespace AIWorldPawnshop.Models;

public sealed class ConversationRequest
{
    [JsonPropertyName("conversation_id")]
    public string ConversationId { get; set; } = string.Empty;
    [JsonPropertyName("turn_id")]
    public int TurnId { get; set; }
    [JsonPropertyName("character_key")]
    public string CharacterKey { get; set; } = string.Empty;
    [JsonPropertyName("character_display_name")]
    public string CharacterDisplayName { get; set; } = string.Empty;
    [JsonPropertyName("faction_key")]
    public string FactionKey { get; set; } = string.Empty;
    [JsonPropertyName("scene")]
    public string Scene { get; set; } = "store";
    [JsonPropertyName("player_text")]
    public string PlayerText { get; set; } = string.Empty;
    [JsonPropertyName("off_topic_streak")]
    public int OffTopicStreak { get; set; }
    [JsonPropertyName("recent_history")]
    public List<string> RecentHistory { get; set; } = new();
    [JsonPropertyName("game_context")]
    public GameConversationContext GameContext { get; set; } = new();
}

public sealed class ConversationResponse
{
    [JsonPropertyName("npc_reply")]
    public string NpcReply { get; set; } = string.Empty;
    [JsonPropertyName("conversation_mode")]
    public string ConversationMode { get; set; } = "continue";
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = "trade";
    [JsonPropertyName("task_relevance")]
    public double TaskRelevance { get; set; }
    [JsonPropertyName("mood")]
    public string Mood { get; set; } = "calm";
    [JsonPropertyName("signals")]
    public ConversationSignals Signals { get; set; } = new();
    [JsonPropertyName("memory_candidates")]
    public List<MemoryCandidate> MemoryCandidates { get; set; } = new();
    [JsonPropertyName("trade_proposal")]
    public TradeProposal TradeProposal { get; set; } = new();
    [JsonPropertyName("procurement_proposal")]
    public ProcurementProposal ProcurementProposal { get; set; } = new();
    [JsonPropertyName("return_visit_proposal")]
    public ReturnVisitProposal ReturnVisitProposal { get; set; } = new();
    [JsonPropertyName("carried_inventory_proposal")]
    public CarriedInventoryProposal CarriedInventoryProposal { get; set; } = new();
    [JsonPropertyName("barter_proposal")]
    public BarterProposal BarterProposal { get; set; } = new();
    [JsonPropertyName("wallet_transfer")]
    public WalletTransferProposal WalletTransfer { get; set; } = new();
    [JsonPropertyName("world_modifications")]
    public List<WorldModificationProposal> WorldModifications { get; set; } = new();
    [JsonPropertyName("third_party_task")]
    public ThirdPartyTaskProposal ThirdPartyTask { get; set; } = new();
}

public sealed class ThirdPartyTaskProposal
{
    [JsonPropertyName("explicit")] public bool Explicit { get; set; }
    [JsonPropertyName("accepted")] public bool Accepted { get; set; }
    [JsonPropertyName("target_character_key")] public string TargetCharacterKey { get; set; } = string.Empty;
    [JsonPropertyName("target_display_name")] public string TargetDisplayName { get; set; } = string.Empty;
    [JsonPropertyName("purpose")] public string Purpose { get; set; } = string.Empty;
    [JsonPropertyName("due_in_days")] public int DueInDays { get; set; } = 1;
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    [JsonPropertyName("decision")] public string Decision { get; set; } = "none";
}

public sealed class GameConversationContext
{
    [JsonPropertyName("authority")]
    public string Authority { get; set; } =
        "原版状态是事实来源；扩展关系和摘要只补充人物经历，不得覆盖原版事实。";
    [JsonPropertyName("character")]
    public NativeCharacterContext Character { get; set; } = new();
    [JsonPropertyName("original_character_profile")]
    public OriginalCharacterProfileContext OriginalCharacterProfile { get; set; } = new();
    [JsonPropertyName("trade")]
    public NativeTradeContext Trade { get; set; } = new();
    [JsonPropertyName("original_story_facts")]
    public List<string> OriginalStoryFacts { get; set; } = new();
    [JsonPropertyName("original_dialogue_context")]
    public List<string> OriginalDialogueContext { get; set; } = new();
    [JsonPropertyName("relationship")]
    public RelationshipContext Relationship { get; set; } = new();
    [JsonPropertyName("procurement")]
    public ProcurementConversationContext Procurement { get; set; } = new();
    [JsonPropertyName("active_world_events")]
    public List<string> ActiveWorldEvents { get; set; } = new();
    [JsonPropertyName("gm_knowledge")]
    public List<string> GmKnowledge { get; set; } = new();
    [JsonPropertyName("third_party_tasks")]
    public List<string> ThirdPartyTasks { get; set; } = new();
    [JsonPropertyName("known_characters")]
    public List<KnownCharacterContext> KnownCharacters { get; set; } = new();
}

public sealed class KnownCharacterContext
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("native_identifier")] public string NativeIdentifier { get; set; } = string.Empty;
    [JsonPropertyName("faction_key")] public string FactionKey { get; set; } = string.Empty;
    [JsonPropertyName("can_return")] public bool CanReturn { get; set; }
}

public sealed class WorldModificationProposal
{
    [JsonPropertyName("target_key")] public string TargetKey { get; set; } = string.Empty;
    [JsonPropertyName("field")] public string Field { get; set; } = string.Empty;
    [JsonPropertyName("operation")] public string Operation { get; set; } = "add";
    [JsonPropertyName("value")] public double Value { get; set; }
    [JsonPropertyName("duration_days")] public int DurationDays { get; set; }
    [JsonPropertyName("permanent")] public bool Permanent { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
    [JsonPropertyName("evidence")] public string Evidence { get; set; } = string.Empty;
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("goal")] public string Goal { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
}

public sealed class OriginalCharacterProfileContext
{
    [JsonPropertyName("canonical_identity")]
    public string CanonicalIdentity { get; set; } = string.Empty;
    [JsonPropertyName("role_and_background")]
    public List<string> RoleAndBackground { get; set; } = new();
    [JsonPropertyName("current_story_state")]
    public List<string> CurrentStoryState { get; set; } = new();
    [JsonPropertyName("immutable_constraints")]
    public List<string> ImmutableConstraints { get; set; } = new();
    [JsonPropertyName("knowledge_boundaries")]
    public List<string> KnowledgeBoundaries { get; set; } = new();
    [JsonPropertyName("speech_style_samples")]
    public List<string> SpeechStyleSamples { get; set; } = new();
}

public sealed class NativeCharacterContext
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("original_identifier")]
    public string OriginalIdentifier { get; set; } = string.Empty;
    [JsonPropertyName("original_real_name")]
    public string OriginalRealName { get; set; } = string.Empty;
    [JsonPropertyName("faction_key")]
    public string FactionKey { get; set; } = string.Empty;
    [JsonPropertyName("faction_display_name")]
    public string FactionDisplayName { get; set; } = string.Empty;
    [JsonPropertyName("original_faction_reputation")]
    public int? OriginalFactionReputation { get; set; }
    [JsonPropertyName("intent")]
    public string Intent { get; set; } = "unknown";
    [JsonPropertyName("is_named_special_character")]
    public bool IsNamedSpecialCharacter { get; set; }
    [JsonPropertyName("can_return")]
    public bool CanReturn { get; set; }
    [JsonPropertyName("can_return_as_buyer")]
    public bool CanReturnAsBuyer { get; set; }
    [JsonPropertyName("is_introduced")]
    public bool IsIntroduced { get; set; }
    [JsonPropertyName("business_complete")]
    public bool BusinessComplete { get; set; }
    [JsonPropertyName("is_security")]
    public bool IsSecurity { get; set; }
    [JsonPropertyName("is_wanted")]
    public bool IsWanted { get; set; }
    [JsonPropertyName("is_augmented")]
    public bool IsAugmented { get; set; }
    [JsonPropertyName("augmentation_tier")]
    public int AugmentationTier { get; set; }
    [JsonPropertyName("augmentation_scanned")]
    public bool AugmentationScanned { get; set; }
    [JsonPropertyName("visit_count")]
    public int VisitCount { get; set; }
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();
    [JsonPropertyName("buying_item_types")]
    public List<string> BuyingItemTypes { get; set; } = new();
    [JsonPropertyName("buying_item_keys")]
    public List<string> BuyingItemKeys { get; set; } = new();
    [JsonPropertyName("refusing_item_types")]
    public List<string> RefusingItemTypes { get; set; } = new();
    [JsonPropertyName("refusing_item_keys")]
    public List<string> RefusingItemKeys { get; set; } = new();
}

public sealed class NativeTradeContext
{
    [JsonPropertyName("available")]
    public bool Available { get; set; }
    [JsonPropertyName("phase")]
    public string Phase { get; set; } = "none";
    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "none";
    [JsonPropertyName("client_cash")]
    public int ClientCash { get; set; }
    [JsonPropertyName("client_budget")]
    public int? ClientBudget { get; set; }
    [JsonPropertyName("client_buy_price_modifier")]
    public int ClientBuyPriceModifier { get; set; }
    [JsonPropertyName("client_sell_price_modifier")]
    public int ClientSellPriceModifier { get; set; }
    [JsonPropertyName("client_impatience")]
    public int ClientImpatience { get; set; }
    [JsonPropertyName("item")]
    public NativeItemContext? Item { get; set; }
    [JsonPropertyName("client_inventory_count")]
    public int ClientInventoryCount { get; set; }
    [JsonPropertyName("client_inventory_truncated")]
    public bool ClientInventoryTruncated { get; set; }
    [JsonPropertyName("client_carried_items")]
    public List<NativeItemContext> ClientCarriedItems { get; set; } = new();
    [JsonPropertyName("player_offered_items")]
    public List<NativeItemContext> PlayerOfferedItems { get; set; } = new();
    [JsonPropertyName("final_offer_value")]
    public long? FinalOfferValue { get; set; }
    [JsonPropertyName("client_wants_item")]
    public bool? ClientWantsItem { get; set; }
    [JsonPropertyName("client_refuses_item")]
    public bool? ClientRefusesItem { get; set; }
    [JsonPropertyName("client_has_budget")]
    public bool? ClientHasBudget { get; set; }
    [JsonPropertyName("client_can_inspect_item")]
    public bool? ClientCanInspectItem { get; set; }
    [JsonPropertyName("bargain")]
    public NativeBargainContext Bargain { get; set; } = new();
}

public sealed class NativeItemContext
{
    [JsonPropertyName("runtime_key")]
    public int RuntimeKey { get; set; }
    [JsonPropertyName("item_key")]
    public string ItemKey { get; set; } = string.Empty;
    [JsonPropertyName("identifier")]
    public string Identifier { get; set; } = string.Empty;
    [JsonPropertyName("internal_name")]
    public string InternalName { get; set; } = string.Empty;
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("owner")]
    public string Owner { get; set; } = "unknown";
    [JsonPropertyName("origin")]
    public string Origin { get; set; } = "original";
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();
    [JsonPropertyName("condition_identifier")]
    public string ConditionIdentifier { get; set; } = string.Empty;
    [JsonPropertyName("is_current_negotiated_item")]
    public bool IsCurrentNegotiatedItem { get; set; }
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }
    [JsonPropertyName("base_value")]
    public long BaseValue { get; set; }
    [JsonPropertyName("current_value")]
    public long CurrentValue { get; set; }
    [JsonPropertyName("negotiated_value")]
    public long NegotiatedValue { get; set; }
    [JsonPropertyName("character_known_correct_base_value")]
    public long? CharacterKnownCorrectBaseValue { get; set; }
    [JsonPropertyName("item_types")]
    public List<string> ItemTypes { get; set; } = new();
    [JsonPropertyName("features")]
    public List<NativeItemFeatureContext> Features { get; set; } = new();
}

public sealed class NativeItemFeatureContext
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;
    [JsonPropertyName("public_description")]
    public string PublicDescription { get; set; } = string.Empty;
    [JsonPropertyName("character_known_description")]
    public string CharacterKnownDescription { get; set; } = string.Empty;
    [JsonPropertyName("exposed")]
    public bool Exposed { get; set; }
    [JsonPropertyName("discovered")]
    public bool Discovered { get; set; }
    [JsonPropertyName("character_can_detect")]
    public bool CharacterCanDetect { get; set; }
}

public sealed class NativeBargainContext
{
    [JsonPropertyName("can_offer_discount")]
    public bool CanOfferDiscount { get; set; }
    [JsonPropertyName("can_offer_markup")]
    public bool CanOfferMarkup { get; set; }
    [JsonPropertyName("can_offer_buying_markup")]
    public bool CanOfferBuyingMarkup { get; set; }
    [JsonPropertyName("can_demand_buying_discount")]
    public bool CanDemandBuyingDiscount { get; set; }
    [JsonPropertyName("discount_percent")]
    public int? DiscountPercent { get; set; }
    [JsonPropertyName("markup_percent")]
    public int? MarkupPercent { get; set; }
    [JsonPropertyName("buying_markup_percent")]
    public int? BuyingMarkupPercent { get; set; }
    [JsonPropertyName("buying_discount_percent")]
    public int? BuyingDiscountPercent { get; set; }
}

public sealed class RelationshipContext
{
    [JsonPropertyName("personal_relationship")]
    public int? PersonalRelationship { get; set; }
    [JsonPropertyName("mod_faction_reputation")]
    public int ModFactionReputation { get; set; }
    [JsonPropertyName("interaction_count")]
    public int InteractionCount { get; set; }
    [JsonPropertyName("memory_summaries")]
    public List<string> MemorySummaries { get; set; } = new();
    [JsonPropertyName("faction_memory_summaries")]
    public List<string> FactionMemorySummaries { get; set; } = new();
}

public sealed class TradeProposal
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "none";
    [JsonPropertyName("percent")]
    public int Percent { get; set; }
    [JsonPropertyName("target_amount")]
    public long? TargetAmount { get; set; }
    [JsonPropertyName("explicit")]
    public bool Explicit { get; set; }
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "unspecified";
    [JsonPropertyName("scope_reason")]
    public string ScopeReason { get; set; } = string.Empty;
}

public sealed class ProcurementProposal
{
    [JsonPropertyName("explicit")]
    public bool Explicit { get; set; }
    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }
    [JsonPropertyName("category")]
    public string Category { get; set; } = "none";
    [JsonPropertyName("category_confidence")]
    public double CategoryConfidence { get; set; }
    [JsonPropertyName("clarification_needed")]
    public bool ClarificationNeeded { get; set; }
    [JsonPropertyName("requested_description")]
    public string RequestedDescription { get; set; } = string.Empty;
    [JsonPropertyName("due_in_days")]
    public int DueInDays { get; set; } = 1;
    [JsonPropertyName("promised_premium_percent")]
    public int PromisedPremiumPercent { get; set; }
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

public sealed class ReturnVisitProposal
{
    [JsonPropertyName("explicit")]
    public bool Explicit { get; set; }
    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }
    [JsonPropertyName("due_in_days")]
    public int DueInDays { get; set; } = 1;
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

public sealed class CarriedInventoryProposal
{
    [JsonPropertyName("explicit")]
    public bool Explicit { get; set; }
    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

public sealed class BarterProposal
{
    [JsonPropertyName("explicit")]
    public bool Explicit { get; set; }
    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }
    [JsonPropertyName("client_item_runtime_key")]
    public int? ClientItemRuntimeKey { get; set; }
    [JsonPropertyName("client_item_name")]
    public string ClientItemName { get; set; } = string.Empty;
    [JsonPropertyName("required_player_item_key")]
    public string RequiredPlayerItemKey { get; set; } = string.Empty;
    [JsonPropertyName("required_player_item_runtime_key")]
    public int? RequiredPlayerItemRuntimeKey { get; set; }
    [JsonPropertyName("required_player_item_runtime_keys")]
    public List<int> RequiredPlayerItemRuntimeKeys { get; set; } = new();
    [JsonPropertyName("required_player_item_name")]
    public string RequiredPlayerItemName { get; set; } = string.Empty;
    [JsonPropertyName("required_player_item_keys")]
    public List<string> RequiredPlayerItemKeys { get; set; } = new();
    [JsonPropertyName("required_player_item_names")]
    public List<string> RequiredPlayerItemNames { get; set; } = new();
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

public sealed class WalletTransferProposal
{
    [JsonPropertyName("explicit")]
    public bool Explicit { get; set; }
    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }
    [JsonPropertyName("amount")]
    public int Amount { get; set; }
    [JsonPropertyName("source")]
    public string Source { get; set; } = "client_budget";
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

public sealed class ProcurementConversationContext
{
    [JsonPropertyName("can_request_future_goods")]
    public bool CanRequestFutureGoods { get; set; }
    [JsonPropertyName("supported_categories")]
    public List<string> SupportedCategories { get; set; } = new();
    [JsonPropertyName("allowed_categories")]
    public List<string> AllowedCategories { get; set; } = new();
    [JsonPropertyName("category_access")]
    public List<ProcurementCategoryAccessContext> CategoryAccess { get; set; } = new();
    [JsonPropertyName("rarity_basis")]
    public string RarityBasis { get; set; } = string.Empty;
    [JsonPropertyName("requests_with_this_character")]
    public List<ProcurementRequestContext> RequestsWithThisCharacter { get; set; } = new();
}

public sealed class ProcurementCategoryAccessContext
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;
    [JsonPropertyName("allowed")]
    public bool Allowed { get; set; }
    [JsonPropertyName("access_level")]
    public int AccessLevel { get; set; }
    [JsonPropertyName("access_label")]
    public string AccessLabel { get; set; } = string.Empty;
    [JsonPropertyName("max_rarity")]
    public string MaxRarity { get; set; } = string.Empty;
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

public sealed class ProcurementRequestContext
{
    [JsonPropertyName("request_key")]
    public string RequestKey { get; set; } = string.Empty;
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;
    [JsonPropertyName("requested_description")]
    public string RequestedDescription { get; set; } = string.Empty;
    [JsonPropertyName("created_day")]
    public int CreatedDay { get; set; }
    [JsonPropertyName("due_day")]
    public int DueDay { get; set; }
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
    [JsonPropertyName("search_chance_percent")]
    public int? SearchChancePercent { get; set; }
    [JsonPropertyName("promised_premium_percent")]
    public int PromisedPremiumPercent { get; set; }
    [JsonPropertyName("resolved_item_name")]
    public string ResolvedItemName { get; set; } = string.Empty;
    [JsonPropertyName("resolved_rarity")]
    public string ResolvedRarity { get; set; } = string.Empty;
    [JsonPropertyName("outcome_summary")]
    public string OutcomeSummary { get; set; } = string.Empty;
}

public sealed class ConversationSignals
{
    [JsonPropertyName("warmth")]
    public int Warmth { get; set; }
    [JsonPropertyName("respect")]
    public int Respect { get; set; }
    [JsonPropertyName("trust")]
    public int Trust { get; set; }
    [JsonPropertyName("offense")]
    public int Offense { get; set; }
    [JsonPropertyName("threat")]
    public int Threat { get; set; }
    [JsonPropertyName("cooperation")]
    public int Cooperation { get; set; }
    [JsonPropertyName("credibility")]
    public int Credibility { get; set; }
    [JsonPropertyName("trade_willingness")]
    public int TradeWillingness { get; set; }
}

public sealed class MemoryCandidate
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "information";
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;
    [JsonPropertyName("importance")]
    public double Importance { get; set; }
}
