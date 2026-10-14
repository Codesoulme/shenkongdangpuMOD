using AIWorldPawnshop.Models;
using AIWorldPawnshop.GM;
using Il2Cpp;
using MelonLoader;

namespace AIWorldPawnshop.Runtime;

internal static class NativeConversationContextBuilder
{
    private const int MaxDialogueFacts = 8;
    private const int MaxWorldEvents = 8;
    private const int MaxClientInventoryItems = 24;

    public static GameConversationContext Build(
        string scene,
        string characterKey,
        string characterName,
        WorldState world,
        StoreClient? explicitClient = null,
        StoreClientInstance? explicitInstance = null)
    {
        var context = new GameConversationContext();
        try
        {
            if (scene.Equals("phone", StringComparison.OrdinalIgnoreCase))
                BuildPhoneContext(context, characterKey, characterName);
            else
                BuildStoreContext(
                    context,
                    characterKey,
                    characterName,
                    world,
                    explicitClient,
                    explicitInstance);

            OriginalWorldLoreCatalog.AddPublicKnowledge(context.OriginalCharacterProfile);
            AddRelationshipContext(context, characterKey, world);
            AddProcurementContext(context, characterKey, world);
            AddWorldEvents(context, world);
            context.GmKnowledge = GmKnowledgeCatalog.Build(world);
            OriginalWorldLoreCatalog.AddGmKnowledge(context.GmKnowledge);
            context.KnownCharacters = world.Characters.Values
                .Where(x => !string.IsNullOrWhiteSpace(x.CharacterKey) &&
                            !x.CharacterKey.Equals(characterKey, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.LastInteractionDay ?? -1)
                .ThenByDescending(x => x.InteractionCount)
                .Take(48)
                .Select(x => new KnownCharacterContext
                {
                    Key = x.CharacterKey,
                    DisplayName = x.DisplayName,
                    NativeIdentifier = x.OriginalIdentifier,
                    FactionKey = x.FactionKey,
                    CanReturn = x.CanReturn
                })
                .ToList();
            var nativeIdentity = context.Character.OriginalIdentifier;
            var canonicalIdentity = context.Character.Key;
            context.ThirdPartyTasks = world.ThirdPartyTasks
                .Where(x => IsOpenThirdPartyStatus(x.Status) &&
                            (x.SourceCharacterKey.Equals(characterKey, StringComparison.OrdinalIgnoreCase) ||
                             x.SourceCharacterKey.Equals(canonicalIdentity, StringComparison.OrdinalIgnoreCase) ||
                             x.TargetCharacterKey.Equals(characterKey, StringComparison.OrdinalIgnoreCase) ||
                             x.TargetCharacterKey.Equals(canonicalIdentity, StringComparison.OrdinalIgnoreCase) ||
                             (!string.IsNullOrWhiteSpace(nativeIdentity) &&
                              x.TargetNativeIdentifier.Equals(nativeIdentity, StringComparison.OrdinalIgnoreCase))))
                .Take(8)
                .Select(x =>
                {
                    var sourceName = world.Characters.TryGetValue(x.SourceCharacterKey, out var source)
                        ? FirstNotBlank(source.DisplayName, x.SourceCharacterKey)
                        : x.SourceCharacterKey;
                    return $"{sourceName}请你处理联系请求：{x.Purpose}，状态 {x.Status}，期限第 {x.DueDay} 天";
                })
                .ToList();

            // Feed back only public facts or facts that explicitly mention the
            // current person. Private memories remain character-scoped; this
            // is the small read-back seam that lets a later conversation react
            // to a real native outcome without leaking another character's
            // private dialogue.
            var currentNames = new[]
            {
                context.Character.Key,
                context.Character.DisplayName,
                context.Character.OriginalIdentifier
            }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            context.RecentWorldFacts = world.Facts
                .Where(f => f.Visibility.Equals("public", StringComparison.OrdinalIgnoreCase) ||
                            currentNames.Any(name => f.Summary.Contains(name, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(f => f.Day)
                .Take(12)
                .Select(f => $"第 {f.Day} 天：{f.Summary}")
                .ToList();
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[当铺世界] 读取原版对话上下文失败，保留基础上下文：{ex.Message}");
            context.Character.Key = characterKey;
            context.Character.DisplayName = characterName;
        }

        return context;
    }

    private static void BuildStoreContext(
        GameConversationContext context,
        string fallbackKey,
        string fallbackName,
        WorldState world,
        StoreClient? explicitClient,
        StoreClientInstance? explicitInstance)
    {
        var playerStore = PlayerStore.Instance;
        var instance = explicitInstance ?? playerStore?.currentClientInstance;
        var client = explicitClient;
        if (client is null && instance is not null)
        {
            try
            {
                client = instance.GetClientBlueprint();
            }
            catch
            {
                client = instance.storeClient;
            }
        }

        if (client is null)
        {
            context.Character.Key = fallbackKey;
            context.Character.DisplayName = fallbackName;
            return;
        }

        var key = FirstNotBlank(client.identifier, fallbackKey, client.displayName);
        var displayName = FirstNotBlank(client.realName, client.displayName, fallbackName, key);
        var character = context.Character;
        character.Key = key;
        character.DisplayName = displayName;
        character.OriginalIdentifier = client.identifier ?? string.Empty;
        character.OriginalRealName = client.realName ?? string.Empty;
        character.FactionKey = client.clientFaction ?? string.Empty;
        character.Intent = client.clientIntent.ToString();
        character.IsNamedSpecialCharacter = IsNamedSpecialCharacter(client);
        character.CanReturn = client.CanReturn;
        character.CanReturnAsBuyer = client.CanReturnAsBuyer;
        character.IsIntroduced = client.isIntroduced;
        character.BusinessComplete = client.isBusinessComplete;
        character.IsSecurity = client.isSecurity;
        character.IsWanted = client.isWanted;
        character.IsAugmented = client.isAug;
        character.AugmentationTier = client.augTier;
        character.AugmentationScanned = client.isAugScanned;
        character.Tags = CopyStrings(client.tag);
        character.BuyingItemTypes = CopyStrings(client.clientBuyingTagList);
        character.BuyingItemKeys = CopyStrings(client.clientBuyingIdList);
        character.RefusingItemTypes = CopyStrings(client.clientBlackTagList);
        character.RefusingItemKeys = CopyStrings(client.clientBlackIdList);

        PopulateFactionContext(character, playerStore);
        PopulateOriginalStoryFacts(context, playerStore, client, key, displayName);
        PopulateDialogueContext(context, client, DialogUIManager.Instance?.currentDialog);
        PopulateOriginalCharacterProfile(context, client);
        PopulateTradeContext(context.Trade, playerStore, client, instance, world);
    }

    private static void BuildPhoneContext(
        GameConversationContext context,
        string fallbackKey,
        string fallbackName)
    {
        var phoneManager = PhoneUIManager.Instance;
        var phone = phoneManager?.currentPhoneClient;
        var character = context.Character;
        character.Key = fallbackKey;
        character.DisplayName = fallbackName;
        character.Intent = "PHONE_CALL";
        character.IsNamedSpecialCharacter = true;

        if (phone is not null)
        {
            var localizedName = Safe(() => phone.GetLocalizedDisplayName(), string.Empty);
            character.Key = FirstNotBlank(
                fallbackKey,
                $"phone:{phone.dialogFuncId}",
                $"phone:{phoneManager?.currentNumber}");
            character.DisplayName = FirstNotBlank(localizedName, phone.displayName, fallbackName);
            character.OriginalIdentifier = phone.dialogFuncId ?? string.Empty;
            character.OriginalRealName = phone.displayName ?? string.Empty;
            character.Tags.Add(phone.phoneClientType.ToString());
            context.OriginalStoryFacts.Add($"原版电话状态：{phone.phoneState}");
            context.OriginalStoryFacts.Add($"此前是否联系过：{phone.dialedBefore}");
            context.OriginalStoryFacts.Add($"玩家曾通过该联系人消费：{phone.amountSpent}");
            if (phone.currentCooldown > 0)
                context.OriginalStoryFacts.Add($"该联系人的服务冷却还剩 {phone.currentCooldown} 天");
        }

        PopulateDialogueContext(
            context,
            null,
            DialoguePhoneUIManager.Instance?.currentDialog);
        PopulatePhoneCharacterProfile(context);
        context.Trade.Phase = "phone_call";
        context.Trade.Direction = "none";
        context.Trade.Available = false;
    }

    private static void PopulateFactionContext(
        NativeCharacterContext character,
        PlayerStore? playerStore)
    {
        var reputations = playerStore?.storeReputations;
        if (reputations is null || string.IsNullOrWhiteSpace(character.FactionKey))
            return;

        for (var i = 0; i < reputations.Count; i++)
        {
            var reputation = reputations[i];
            if (reputation is null ||
                !string.Equals(reputation.factionId, character.FactionKey, StringComparison.OrdinalIgnoreCase))
                continue;

            character.FactionDisplayName = reputation.factionDisplay ?? string.Empty;
            character.OriginalFactionReputation = Safe<int?>(() => reputation.GetReputation(), null);
            return;
        }
    }

    private static void PopulateOriginalStoryFacts(
        GameConversationContext context,
        PlayerStore? playerStore,
        StoreClient client,
        string key,
        string displayName)
    {
        if (playerStore is null)
            return;

        var manager = playerStore.storeClientManager;
        if (manager is not null)
        {
            var visits = Safe(() => StoreClientManager.GetCustomerTotalVisitedCount(key), 0);
            context.Character.VisitCount = visits;
            context.OriginalStoryFacts.Add($"该身份在原版记录中的累计来访次数：{visits}");
            if (Contains(manager.arrestedClients, key))
                context.OriginalStoryFacts.Add("原版记录：这个身份曾被玩家交给治安部门");
        }

        AddOriginalTradeHistory(context.OriginalStoryFacts, playerStore, key, displayName);

        if (client.isWanted)
        {
            context.OriginalStoryFacts.Add(
                $"原版身份：通缉对象；是否已被玩家举报：{client.reported}；悬赏：{client.reportReward}");
        }

        if (client.isAug && client.isAugScanned)
        {
            context.OriginalStoryFacts.Add(
                $"原版身份：改造体，等级 {client.augTier}；玩家已完成扫描");
        }

        if (client.isSecurity ||
            string.Equals(client.clientFaction, StoreClient.FACTION_SECURITY, StringComparison.OrdinalIgnoreCase))
        {
            context.OriginalStoryFacts.Add(
                $"玩家原版行为记录：错误举报 {playerStore.wrongfulReportCount} 次；" +
                $"今日举报文件是否正确：{playerStore.correctReportFileToday}");
        }

        var identity = $"{key} {displayName} {client.displayName} {client.realName}".ToLowerInvariant();
        var story = Safe(() => PlayerStore.GetStoreClientData(), playerStore.storeClientData);
        if (story is not null)
            AddCharacterSpecificStoryFacts(context.OriginalStoryFacts, identity, story, playerStore);

        if (ContainsAny(identity, "businesspermit", "permitofficial", "许可证", "执照官"))
            context.OriginalStoryFacts.Add($"玩家当前是否持有营业许可：{playerStore.hasBusinessPermit}");

        if (ContainsAny(identity, "criminal", "blackmarket", "shady", "thief", "wild", "罪犯", "黑市"))
        {
            context.OriginalStoryFacts.Add(
                $"玩家本轮是否交易过赃物或违禁品：{playerStore.soldStolenOrContrabandThisRun}");
        }
    }

    private static void AddCharacterSpecificStoryFacts(
        List<string> facts,
        string identity,
        StoreClientData story,
        PlayerStore playerStore)
    {
        if (ContainsAny(identity, "lun", "伦警官", "伦官员"))
        {
            facts.Add($"伦的原版经历：成瘾阶段 {story.lunAddictionLevel}");
            facts.Add($"伦的原版经历：是否曾被逮捕 {story.officerLunArrested}");
        }

        if (ContainsAny(identity, "juan", "lottery", "lucky", "胡安", "彩票"))
        {
            facts.Add($"胡安的原版经历：玩家是否见过他 {story.isJuanMet}");
            facts.Add($"胡安的原版经历：彩票相关见面次数 {story.lotteryMetTime}");
        }

        if (ContainsAny(identity, "eleanor", "埃莉诺", "艾莉诺"))
        {
            facts.Add($"埃莉诺的原版经历：玩家提供的资金 {story.eleanorFund}");
            facts.Add($"埃莉诺的原版经历：玩家帮助次数 {story.eleanorHelpedTimes}");
        }

        if (ContainsAny(identity, "jackson", "inventor", "杰克逊", "发明家"))
        {
            facts.Add($"杰克逊的原版经历：投资额 {story.jacksonInvestment}");
            facts.Add($"杰克逊的原版经历：是否正式认识玩家 {story.isJacksonIntroduced}");
            facts.Add($"杰克逊的原版经历：模块是否已介绍 {story.isModuleIntroduced}");
        }

        if (ContainsAny(identity, "businesspermit", "permitofficial", "许可证", "执照官"))
        {
            facts.Add($"许可官剧情阶段：{story.businessPermitOfficialState}");
            facts.Add($"许可官记录的贿赂金额：{story.businessPermitBribeAmount}");
        }

        if (ContainsAny(identity, "georgio", "乔治奥", "乔治欧"))
        {
            facts.Add($"乔治奥的原版经历：已处理核废料 {story.nuclearWasteDumped}");
            facts.Add($"乔治奥的原版经历：是否到访过核废料交易 {story.georgioNuclearWasteVisited}");
            facts.Add($"乔治奥的原版经历：留在柜台的核废料 {story.nuclearWasteLeftOnCounter}");
            facts.Add(
                $"乔治奥的借贷记录：状态 {playerStore.loanOfferedToGeorgio}，" +
                $"发生日 {playerStore.dayWhenLoanOfferedGeorgio}");
        }
    }

    private static void AddOriginalTradeHistory(
        List<string> facts,
        PlayerStore playerStore,
        string key,
        string displayName)
    {
        var soldItems = playerStore.soldItemsTracker;
        if (soldItems is null)
            return;

        var matches = new List<string>();
        for (var i = soldItems.Count - 1; i >= 0 && matches.Count < 5; i--)
        {
            var sold = soldItems[i];
            if (sold is null ||
                (!string.Equals(sold.clientId, key, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(sold.clientName, displayName, StringComparison.OrdinalIgnoreCase)))
                continue;

            var itemName = FirstNotBlank(sold.itemName, sold.itemId, "未命名物品");
            var feedback = sold.feedbackLevel == 0
                ? string.Empty
                : $"，后续反馈等级 {sold.feedbackLevel}";
            matches.Add(
                $"原版交易记忆：玩家曾以 {sold.soldPrice} 出售 {itemName}{feedback}");
        }

        facts.AddRange(matches);
    }

    private static void PopulateDialogueContext(
        GameConversationContext context,
        StoreClient? client,
        Dialogue? currentDialogue)
    {
        AddDialogueChain(context.OriginalDialogueContext, "当前原版台词", currentDialogue, 3);
        if (client is not null && currentDialogue is null)
            AddDialogueChain(context.OriginalDialogueContext, "本次来访主台词", client.mainDialogue, 6);
        if (client?.repeatDialogue is not null)
            AddDialogue(context.OriginalDialogueContext, "原版重复交谈语境", client.repeatDialogue);
        if (client?.acceptDealDialogue is not null)
            AddDialogue(context.OriginalDialogueContext, "原版接受交易语气", client.acceptDealDialogue);
        if (client?.allDoneDialogue is not null)
            AddDialogue(context.OriginalDialogueContext, "原版结束交易语气", client.allDoneDialogue);
        if (client?.placedWrongItemWhenSellingToDialogue is not null)
            AddDialogue(context.OriginalDialogueContext, "原版拒绝错误物品语气", client.placedWrongItemWhenSellingToDialogue);
        if (client?.placeRightItemWhenSellingToDialogue is not null)
            AddDialogue(context.OriginalDialogueContext, "原版确认目标物品语气", client.placeRightItemWhenSellingToDialogue);
        if (client?.interogationDialogue is not null)
            AddDialogue(context.OriginalDialogueContext, "原版受询问语气", client.interogationDialogue);
        if (client?.customOnArrestDialogue is not null)
            AddDialogue(context.OriginalDialogueContext, "原版被捕相关语气", client.customOnArrestDialogue);
    }

    private static void PopulateOriginalCharacterProfile(
        GameConversationContext context,
        StoreClient client)
    {
        var character = context.Character;
        var profile = context.OriginalCharacterProfile;
        profile.CanonicalIdentity =
            $"{character.DisplayName}（原版标识：{character.OriginalIdentifier}）";
        profile.ImmutableConstraints.Add(
            $"当前人物只能是 {character.DisplayName}，不得改名、自称为其他原版人物或更换身份。");
        profile.ImmutableConstraints.Add(
            $"原版来访目的为 {character.Intent}；自由交谈只能影响态度和允许的交易提案，不能重写本次来意。");

        if (!string.IsNullOrWhiteSpace(character.FactionKey))
        {
            var faction = FirstNotBlank(character.FactionDisplayName, character.FactionKey);
            profile.RoleAndBackground.Add($"原版顾客分类：{faction}；不自动等于组织成员身份。");
            profile.ImmutableConstraints.Add("不得仅凭顾客分类自称某组织成员，或冒用其他阵营的权限。");
        }

        AddGeneralRoleFacts(profile, client);
        AddNamedCharacterProfile(profile, context, client);

        profile.CurrentStoryState.AddRange(context.OriginalStoryFacts);
        foreach (var line in context.OriginalDialogueContext.Take(MaxDialogueFacts))
            profile.SpeechStyleSamples.Add(line);

        profile.KnowledgeBoundaries.Add(
            "只知道当前人物亲历的原版剧情、交易、关系记忆，以及在场可见物品、公开背景和已公开的世界事件。");
        profile.KnowledgeBoundaries.Add(
            "不得把其他顾客、电话联系人或全局对话记录当成自己的经历。");
        profile.KnowledgeBoundaries.Add(
            "未在档案、当前台词或摘要记忆中出现的身世细节，应保持含糊而不是编造。");
        if (client.isAug && !client.isAugScanned)
            profile.KnowledgeBoundaries.Add("真实身份尚未被玩家确认；不要主动揭露改造体身份或复述导师笔记。只能按当前人物原版台词及已发生的情节回应。");
    }

    private static void PopulatePhoneCharacterProfile(GameConversationContext context)
    {
        var character = context.Character;
        var profile = context.OriginalCharacterProfile;
        profile.CanonicalIdentity =
            $"{character.DisplayName}（原版电话标识：{character.OriginalIdentifier}）";
        profile.RoleAndBackground.Add("这是当前电话线路对应的原版联系人，不是店内顾客。");
        profile.CurrentStoryState.AddRange(context.OriginalStoryFacts);
        profile.ImmutableConstraints.Add(
            $"只能以电话联系人 {character.DisplayName} 的身份回复，不得借用店内顾客的姓名、库存或来意。");
        foreach (var line in context.OriginalDialogueContext.Take(MaxDialogueFacts))
            profile.SpeechStyleSamples.Add(line);
        profile.KnowledgeBoundaries.Add("电话联系人可以谈公开背景，但只能叙述自己的原版服务、经历与当前通话内容。");
    }

    private static void AddGeneralRoleFacts(
        OriginalCharacterProfileContext profile,
        StoreClient client)
    {
        if (client.isSecurity)
            profile.RoleAndBackground.Add("原版职业身份：治安或安保人员。");
        if (client.isWanted)
            profile.RoleAndBackground.Add("原版身份状态：通缉对象。");
        if (client.isAug && client.isAugScanned)
            profile.RoleAndBackground.Add($"原版身体状态：{client.augTier} 级改造体，玩家已完成扫描。");
        if (client.IsClientAddict)
            profile.RoleAndBackground.Add("原版人物状态：存在成瘾相关设定。");
        if (client.IsPersonalConsumption)
            profile.RoleAndBackground.Add("本次需求主要用于个人消费。");
        if (client.isOfferWholesale)
            profile.RoleAndBackground.Add("原版交易身份：可提供批量或批发交易。");
        if (client.noAcceptingContraband)
            profile.RoleAndBackground.Add("原版交易立场：不接受违禁品。");
        if (client.AcceptingContrabandOverride)
            profile.RoleAndBackground.Add("原版交易立场：此人物被明确允许接触违禁品。");
        if (client.arrestable)
            profile.RoleAndBackground.Add("原版规则允许该人物被举报或逮捕。");

        if (client.clientBuyingTagList is not null && client.clientBuyingTagList.Count > 0)
            profile.RoleAndBackground.Add("原版采购偏好来自 character.buying_item_types。");
        if (client.clientBlackTagList is not null && client.clientBlackTagList.Count > 0)
            profile.RoleAndBackground.Add("原版拒收边界来自 character.refusing_item_types。");
    }

    private static void AddNamedCharacterProfile(
        OriginalCharacterProfileContext profile,
        GameConversationContext context,
        StoreClient client)
    {
        var identity = $"{client.identifier} {client.displayName} {client.realName}".ToLowerInvariant();
        if (ContainsAny(identity, "lun", "伦警官", "伦官员"))
            profile.RoleAndBackground.Add("原版专属人物：伦；其身份与治安工作、成瘾经历及后续任职状态相连。");
        if (ContainsAny(identity, "juan", "lottery", "lucky", "胡安", "彩票"))
            profile.RoleAndBackground.Add("原版专属人物：胡安；其个人故事与彩票相关会面相连。");
        if (ContainsAny(identity, "eleanor", "埃莉诺", "艾莉诺"))
            profile.RoleAndBackground.Add("原版专属人物：埃莉诺；其个人故事由玩家提供资金和帮助的记录推进。");
        if (ContainsAny(identity, "jackson", "inventor", "杰克逊", "发明家"))
            profile.RoleAndBackground.Add("原版专属人物：杰克逊；发明家，其故事与投资、发明模块和仓储业务相连。");
        if (ContainsAny(identity, "georgio", "乔治奥", "乔治欧"))
            profile.RoleAndBackground.Add("原版专属人物：乔治奥；其故事与核废料处置和借贷往来相连。");
        if (ContainsAny(identity, "businesspermit", "permitofficial", "许可证", "执照官"))
            profile.RoleAndBackground.Add("原版专属身份：营业许可官员，负责许可与相关费用。");
        if (ContainsAny(identity, "mentor", "导师"))
            profile.RoleAndBackground.Add("原版专属身份：玩家的店务导师，负责介绍和提醒原版经营机制。");
        if (ContainsAny(identity, "landlord", "房东"))
            profile.RoleAndBackground.Add("原版专属身份：房东，其言行应围绕租务及其原版交易安排。");
        if (ContainsAny(identity, "loanshark", "loan_shark", "高利贷"))
            profile.RoleAndBackground.Add("原版专属身份：放贷者，其言行应符合债务和催收关系。");
        if (ContainsAny(identity, "blackmailer", "blackmail", "勒索"))
            profile.RoleAndBackground.Add("原版专属身份：勒索者，其言行应符合掌握把柄并索取利益的处境。");

        if (context.Character.IsNamedSpecialCharacter && profile.RoleAndBackground.Count == 0)
        {
            profile.RoleAndBackground.Add(
                "这是有固定姓名的原版人物；姓名、阵营、交易偏好和原版台词共同构成其身份，缺失的背景不得自行补写。");
        }
    }

    private static void AddDialogueChain(
        List<string> target,
        string label,
        Dialogue? dialogue,
        int maxLines)
    {
        var current = dialogue;
        for (var i = 0; i < maxLines && current is not null; i++)
        {
            AddDialogue(target, i == 0 ? label : $"{label}后续", current);
            current = Safe<Dialogue?>(() => current.nextDialogue, null);
        }
    }

    private static void AddDialogue(List<string> target, string label, Dialogue? dialogue)
    {
        if (dialogue is null)
            return;

        var title = Clean(dialogue.title, 80);
        var text = Clean(dialogue.plainText, 360);
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(text))
            return;

        target.Add($"{label}：{title} {text}".Trim());
    }

    private static void PopulateTradeContext(
        NativeTradeContext trade,
        PlayerStore? playerStore,
        StoreClient client,
        StoreClientInstance? instance,
        WorldState world)
    {
        trade.ClientCash = client.clientCash;
        trade.ClientBudget = client.useClientBudget ? client.clientBudget : null;
        trade.ClientBuyPriceModifier = client.buyPriceModifier;
        trade.ClientSellPriceModifier = client.sellPriceModifier;
        trade.ClientImpatience = client.clientImpatience;

        // Capture ownership once so items moved from a customer's inventory to
        // the counter keep their original owner in every part of the context.
        var counterOwnership = CounterOwnershipSnapshot.Capture(playerStore, instance);
        PopulateClientInventory(trade, client, instance, world, counterOwnership);
        PopulatePlayerOfferedItems(trade, playerStore, client, world, counterOwnership);

        var negotiation = NegociationUIManager.Instance;
        var item = negotiation?.currentNegociatedItem ?? playerStore?.currentNegociatedItem;
        if (item is null)
        {
            trade.Phase = client.isBusinessComplete ? "business_complete" : "waiting_for_item";
            return;
        }

        var itemOwner = ResolveItemOwnership(
            negotiation,
            playerStore,
            item,
            counterOwnership);
        var playerOwnsItem = itemOwner == CounterItemOwner.Player;
        trade.Available = true;
        trade.Phase = negotiation?.uiObject is not null && negotiation.uiObject.activeInHierarchy
            ? "negotiating"
            : "item_placed";
        trade.Direction = itemOwner switch
        {
            CounterItemOwner.Player => "player_sells_to_client",
            CounterItemOwner.Client => "player_buys_from_client",
            _ => "unknown"
        };
        trade.ClientWantsItem = Safe<bool?>(() => client.IsClientBuyingThisItem(item), null);
        trade.ClientRefusesItem = Safe<bool?>(() => client.IsClientRefusingItem(item), null);
        trade.ClientHasBudget = playerOwnsItem
            ? Safe<bool?>(() => client.HasBudgetLeftToBuy(item), null)
            : null;
        trade.ClientCanInspectItem = Safe<bool?>(() => client.CanClientExposeAnyFeature(item), null);
        trade.FinalOfferValue = negotiation is not null
            ? Safe<long?>(() => negotiation.GetFinalOfferValue(item), null)
            : Safe<long?>(() => item.GetNegociatedValue(), null);
        trade.Item = BuildItemContext(
            item,
            client,
            trade.ClientCanInspectItem == true,
            itemOwner switch
            {
                CounterItemOwner.Player => "player",
                CounterItemOwner.Client => "client",
                _ => "unknown"
            },
            NativeProcurementAdapter.IsDeliveredItem(world, item.uniqueId)
                ? "mod_procurement"
                : "original",
            true);
        if (itemOwner == CounterItemOwner.Client &&
            trade.ClientCarriedItems.Count < MaxClientInventoryItems &&
            !trade.ClientCarriedItems.Any(x => x.RuntimeKey == item.uniqueId))
        {
            // During the hand-off between the customer inventory and the
            // front counter, both inventory readers can briefly miss the
            // object.  The native negotiation ownership flag still gives us
            // a safe customer-side fallback for the conversation contract.
            AddClientCarriedItem(
                trade,
                client,
                world,
                item,
                item.uniqueId);
            trade.ClientInventoryCount++;
        }
        if (itemOwner == CounterItemOwner.Client)
        {
            MelonLogger.Msg(
                $"[当铺世界] 柜台归属确认：当前物品 {item.uniqueId} 属于顾客；" +
                $"顾客物品 {trade.ClientCarriedItems.Count} 件，玩家侧物品 {trade.PlayerOfferedItems.Count} 件");
        }
        else if (itemOwner == CounterItemOwner.Unknown)
        {
            MelonLogger.Warning(
                $"[当铺世界] 柜台归属暂时无法确认：当前物品 {item.uniqueId}；" +
                "不会把它加入玩家交换物清单");
        }
        PopulateBargainContext(trade.Bargain);
    }

    private static void PopulateClientInventory(
        NativeTradeContext trade,
        StoreClient client,
        StoreClientInstance? instance,
        WorldState world,
        CounterOwnershipSnapshot ownership)
    {
        var items = instance?.gridInv?.items;
        var countSeen = new HashSet<int>();
        var clientInventoryCount = 0;
        if (items is not null)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item is not null && (item.uniqueId <= 0 || countSeen.Add(item.uniqueId)))
                    clientInventoryCount++;
            }
        }
        foreach (var item in ownership.ClientCounterItems)
        {
            if (item is not null && (item.uniqueId <= 0 || countSeen.Add(item.uniqueId)))
                clientInventoryCount++;
        }
        trade.ClientInventoryCount = clientInventoryCount;
        trade.ClientInventoryTruncated = trade.ClientInventoryCount > MaxClientInventoryItems;
        var currentRuntimeKey = Safe(
            () => NegociationUIManager.Instance?.currentNegociatedItem?.uniqueId ?? 0,
            0);
        var seen = new HashSet<int>();
        if (items is not null)
        {
            for (var i = 0; i < items.Count && trade.ClientCarriedItems.Count < MaxClientInventoryItems; i++)
            {
                var item = items[i];
                if (item is null || (item.uniqueId > 0 && !seen.Add(item.uniqueId)))
                    continue;

                AddClientCarriedItem(trade, client, world, item, currentRuntimeKey);
            }
        }

        // Customer goods can leave gridInv when the native UI places them on
        // the counter.  Keep them in the client list, never the player list.
        for (var i = 0; i < ownership.ClientCounterItems.Count &&
                         trade.ClientCarriedItems.Count < MaxClientInventoryItems; i++)
        {
            var item = ownership.ClientCounterItems[i];
            if (item is null || (item.uniqueId > 0 && !seen.Add(item.uniqueId)))
                continue;

            AddClientCarriedItem(trade, client, world, item, currentRuntimeKey);
        }
    }

    private static void PopulatePlayerOfferedItems(
        NativeTradeContext trade,
        PlayerStore? playerStore,
        StoreClient client,
        WorldState world,
        CounterOwnershipSnapshot ownership)
    {
        if (playerStore is null || ownership.PlayerCounterItems.Count == 0)
            return;

        try
        {
            for (var i = 0; i < ownership.PlayerCounterItems.Count &&
                             trade.PlayerOfferedItems.Count < MaxClientInventoryItems; i++)
            {
                var item = ownership.PlayerCounterItems[i];
                if (item is null || trade.PlayerOfferedItems.Any(x => x.RuntimeKey == item.uniqueId))
                    continue;

                var canInspect = Safe(() => client.CanClientExposeAnyFeature(item), false);
                trade.PlayerOfferedItems.Add(BuildItemContext(
                    item,
                    client,
                    canInspect,
                    "player",
                    NativeProcurementAdapter.IsDeliveredItem(world, item.uniqueId)
                        ? "mod_procurement"
                        : "original",
                    item.uniqueId == playerStore.currentNegociatedItem?.uniqueId));
            }
        }
        catch
        {
            // Counter inventory is optional during scene transitions.
        }
    }

    private static void AddClientCarriedItem(
        NativeTradeContext trade,
        StoreClient client,
        WorldState world,
        GameItem item,
        int currentRuntimeKey)
    {
        var canInspect = Safe(() => client.CanClientExposeAnyFeature(item), false);
        trade.ClientCarriedItems.Add(BuildItemContext(
            item,
            client,
            canInspect,
            "client",
            NativeProcurementAdapter.IsDeliveredItem(world, item.uniqueId)
                ? "mod_procurement"
                : TemporaryClientInventoryAdapter.IsGeneratedItem(world, item.uniqueId)
                    ? "mod_carried_inventory"
                    : "original",
            item.uniqueId == currentRuntimeKey));
    }

    private static CounterItemOwner ResolveItemOwnership(
        NegociationUIManager? negotiation,
        PlayerStore? playerStore,
        GameItem item,
        CounterOwnershipSnapshot ownership)
    {
        // Counter placement must not erase the native owner.  Prefer the
        // snapshot before consulting UI state, which may describe the item
        // after it has left the customer's grid inventory.
        var snapshotOwner = ownership.Resolve(item);
        if (snapshotOwner == CounterItemOwner.Client)
            return CounterItemOwner.Client;
        if (snapshotOwner == CounterItemOwner.Player)
            return CounterItemOwner.Player;
        if (ownership.IsConflicted(item))
        {
            // A contradictory native snapshot is stronger than the
            // negotiation UI's transient flag.  Falling back here would turn
            // an unresolved object into a fabricated customer/player owner.
            return CounterItemOwner.Unknown;
        }

        if (negotiation is not null && negotiation.currentNegociatedItem is not null)
        {
            // The native negotiation flag is an ownership signal.  The
            // client's buying intent is not: SELLNBUY clients can buy and
            // sell in the same visit, so it must never be used as a fallback
            // for item ownership.
            try
            {
                return negotiation.IsNegociatingItemOwned()
                    ? CounterItemOwner.Player
                    : CounterItemOwner.Client;
            }
            catch
            {
                // Continue with the independent inventory evidence below.
            }
        }

        var clientItems = playerStore?.currentClientInstance?.gridInv?.items;
        if (clientItems is not null)
        {
            for (var i = 0; i < clientItems.Count; i++)
            {
                var candidate = clientItems[i];
                if (candidate is not null && candidate.uniqueId == item.uniqueId)
                    return CounterItemOwner.Client;
            }
        }

        // We could not prove ownership.  Keep the item unknown so downstream
        // bargain and barter paths fail closed instead of guessing from the
        // client's broader visit intent.
        return CounterItemOwner.Unknown;
    }

    private static NativeItemContext BuildItemContext(
        GameItem item,
        StoreClient client,
        bool clientCanInspectAny,
        string owner,
        string origin,
        bool isCurrentNegotiatedItem)
    {
        var result = new NativeItemContext
        {
            RuntimeKey = item.uniqueId,
            ItemKey = item.identifier ?? string.Empty,
            Identifier = item.identifier ?? string.Empty,
            InternalName = item.name ?? string.Empty,
            DisplayName = FirstNotBlank(
                Safe(() => item.GetDisplayName(true), string.Empty),
                item.name,
                item.identifier),
            Owner = owner,
            Origin = origin,
            IsCurrentNegotiatedItem = isCurrentNegotiatedItem,
            Description = Clean(
                FirstNotBlank(item.shortDescription, item.longDescription, item.flavorText),
                280),
            Quantity = item.unitCount,
            BaseValue = Safe(() => item.GetBaseValue(), item.unitBaseValue),
            CurrentValue = Safe(() => item.GetCurrentValue(false, true, true, false, 0), item.unitValue),
            NegotiatedValue = Safe(() => item.GetNegociatedValue(), item.unitValue)
        };

        result.ItemTypes = CopyStrings(item.itemTypes);
        result.Tags = result.ItemTypes.ToList();
        result.ConditionIdentifier = ReadConditionIdentifier(item);
        var allRelevantFeaturesKnown = true;
        var features = item.itemFeatures;
        if (features is not null)
        {
            for (var i = 0; i < features.Count && result.Features.Count < 16; i++)
            {
                var feature = features[i];
                if (feature is null || feature.isDisabled)
                    continue;

                var exposed = Safe(() => feature.IsExposed(), feature.isFeatureExposed);
                var discovered = Safe(() => feature.IsFeatureDiscovered(), feature.isFeatureDiscovered);
                var canDetect = Safe(() => client.CanClientExposeThisFeature(feature), false);
                var known = exposed || discovered || canDetect;
                allRelevantFeaturesKnown &= known;
                result.Features.Add(new NativeItemFeatureContext
                {
                    Category = FirstNotBlank(feature.category, feature.identifier),
                    PublicDescription = Clean(
                        Safe(() => feature.GetPublicDisplay(), feature.publicDisplay ?? string.Empty),
                        160),
                    CharacterKnownDescription = known
                        ? Clean(Safe(() => feature.GetActualDisplay(), feature.actualDisplay ?? string.Empty), 160)
                        : string.Empty,
                    Exposed = exposed,
                    Discovered = discovered,
                    CharacterCanDetect = canDetect
                });
            }
        }

        if (clientCanInspectAny || allRelevantFeaturesKnown)
            result.CharacterKnownCorrectBaseValue = Safe<long?>(() => AppraisalHelper.GetCorrectBaseValue(item), null);

        return result;
    }

    private static string ReadConditionIdentifier(GameItem item)
    {
        try
        {
            var features = item.itemFeatures;
            if (features is null)
                return string.Empty;

            for (var i = 0; i < features.Count; i++)
            {
                var feature = features[i];
                if (feature is null || feature.isDisabled)
                    continue;

                var condition = feature.GetCurrentItemCondition();
                var identifier = condition?.identifier;
                if (!string.IsNullOrWhiteSpace(identifier))
                    return identifier;
            }
        }
        catch
        {
            // Conditions are optional; an absent identifier is safer than a
            // fabricated discriminator.
        }

        return string.Empty;
    }

    private static void PopulateBargainContext(NativeBargainContext result)
    {
        var bargain = BargainUIManager.Instance;
        if (bargain is null)
            return;

        result.CanOfferDiscount = Safe(() => bargain.CanOfferDiscount(), false);
        result.CanOfferMarkup = Safe(() => bargain.CanOfferMarkup(), false);
        result.CanOfferBuyingMarkup = Safe(() => bargain.CanOfferBuyingMarkup(), false);
        result.CanDemandBuyingDiscount = Safe(() => bargain.CanDemandBuyingDiscount(), false);
        result.DiscountPercent = bargain.DiscountOffered ? bargain.DiscountPercent : null;
        result.MarkupPercent = bargain.MarkupOffered ? bargain.MarkupPercent : null;
        result.BuyingMarkupPercent = bargain.BuyingMarkupOffered ? bargain.BuyingMarkupPercent : null;
        result.BuyingDiscountPercent = bargain.BuyingDiscountOffered
            ? bargain.BuyingDiscountPercent
            : null;
    }

    private static void AddRelationshipContext(
        GameConversationContext context,
        string requestedCharacterKey,
        WorldState world)
    {
        var key = FirstNotBlank(context.Character.Key, requestedCharacterKey);
        if (world.Characters.TryGetValue(key, out var character))
        {
            context.Relationship.PersonalRelationship =
                context.Character.IsNamedSpecialCharacter ? character.Relationship : null;
            context.Relationship.InteractionCount = character.InteractionCount;
            context.Relationship.MemorySummaries = character.Memories
                .OrderByDescending(x => x.Importance)
                .ThenByDescending(x => x.CreatedDay)
                .Take(8)
                .Select(x => x.Summary)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        var factionKey = context.Character.FactionKey;
        if (!string.IsNullOrWhiteSpace(factionKey) &&
            world.Factions.TryGetValue(factionKey, out var faction))
        {
            context.Relationship.ModFactionReputation = faction.Reputation;
            context.Relationship.FactionMemorySummaries = faction.Memories
                .OrderByDescending(x => x.Importance)
                .ThenByDescending(x => x.CreatedDay)
                .Take(6)
                .Select(x => x.Summary)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }
    }

    private static bool IsOpenThirdPartyStatus(string status) =>
        status.Equals("accepted", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("proposed", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("arrived", StringComparison.OrdinalIgnoreCase);

    private static void AddProcurementContext(
        GameConversationContext context,
        string requestedCharacterKey,
        WorldState world)
    {
        var procurement = context.Procurement;
        procurement.SupportedCategories =
            ProcurementRequestResolver.SupportedCategoryDescriptions.ToList();
        var intent = context.Character.Intent;
        procurement.CanRequestFutureGoods =
            !string.IsNullOrWhiteSpace(context.Character.OriginalIdentifier) &&
            (intent.Equals("BUY", StringComparison.OrdinalIgnoreCase) ||
             intent.Equals("SELL", StringComparison.OrdinalIgnoreCase) ||
             intent.Equals("SELLNBUY", StringComparison.OrdinalIgnoreCase));
        var access = ProcurementAccessPolicy.EvaluateAll(context);
        procurement.CategoryAccess = access
            .Select(x => new ProcurementCategoryAccessContext
            {
                Category = x.Category,
                Allowed = x.Allowed,
                AccessLevel = x.AccessLevel,
                AccessLabel = x.AccessLabel,
                MaxRarity = x.MaxRarityLabel,
                Reason = x.Reason
            })
            .ToList();
        procurement.AllowedCategories = access
            .Where(x => x.Allowed)
            .Select(x => x.Category)
            .ToList();
        procurement.RarityBasis =
            "具体物品由原版掉落表权重决定；低权重物品更稀有，且受人物渠道上限约束。";

        var key = FirstNotBlank(context.Character.Key, requestedCharacterKey);
        var originalIdentifier = context.Character.OriginalIdentifier;
        procurement.RequestsWithThisCharacter = world.ProcurementRequests
            .Where(x =>
                x.CharacterKey.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(originalIdentifier) &&
                 x.OriginalClientIdentifier.Equals(
                     originalIdentifier,
                     StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.CreatedDay)
            .Take(6)
            .Select(x => new ProcurementRequestContext
            {
                RequestKey = x.RequestKey,
                Category = x.Category,
                RequestedDescription = x.RequestedDescription,
                CreatedDay = x.CreatedDay,
                DueDay = x.DueDay,
                Status = x.Status,
                SearchChancePercent = x.SearchChancePercent,
                PromisedPremiumPercent = x.PromisedPremiumPercent,
                ResolvedItemName = x.ResolvedItemName,
                ResolvedRarity = x.ResolvedRarityLabel,
                OutcomeSummary = x.OutcomeSummary
            })
            .ToList();
    }

    private static void AddWorldEvents(GameConversationContext context, WorldState world)
    {
        try
        {
            var originalEvents = StoreStation.Instance?.storeEventManager?.activeEvents;
            if (originalEvents is not null)
            {
                for (var i = 0; i < originalEvents.Count && context.ActiveWorldEvents.Count < MaxWorldEvents; i++)
                {
                    var current = originalEvents[i];
                    if (current is null || current.isHidden || !current.IsKnown)
                        continue;

                    var name = FirstNotBlank(
                        Safe(() => current.GetDisplayName(), string.Empty),
                        current.displayName,
                        current.newsName);
                    var description = Clean(current.newsDescription, 220);
                    context.ActiveWorldEvents.Add(
                        $"原版事件：{name}；{description}；剩余约 {Math.Max(0, current.duration - current.currentDay)} 天");
                }
            }
        }
        catch
        {
            // Original events enrich dialogue but are not required for a valid request.
        }

        foreach (var current in world.ActiveEvents
                     .Where(x => world.CurrentDay < x.StartDay + x.DurationDays)
                     .Take(MaxWorldEvents - context.ActiveWorldEvents.Count))
        {
            context.ActiveWorldEvents.Add(
                $"当铺世界事件：{current.Title}；{current.Summary}；影响物资 {current.ItemGroup}");
        }
    }

    private static bool IsNamedSpecialCharacter(StoreClient client)
    {
        if (!string.IsNullOrWhiteSpace(client.realName))
            return true;

        var key = client.identifier ?? string.Empty;
        return ContainsAny(
            key.ToLowerInvariant(),
            "georgio", "lun", "eleanor", "jackson", "juan", "mentor", "landlord",
            "businesspermit", "wanted", "inventor");
    }

    private static bool Contains(
        Il2CppSystem.Collections.Generic.List<string>? values,
        string target)
    {
        if (values is null || string.IsNullOrWhiteSpace(target))
            return false;

        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], target, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static List<string> CopyStrings(Il2CppSystem.Collections.Generic.List<string>? source)
    {
        var result = new List<string>();
        if (source is null)
            return result;

        for (var i = 0; i < source.Count; i++)
        {
            var value = source[i];
            if (!string.IsNullOrWhiteSpace(value))
                result.Add(value);
        }

        return result;
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static string FirstNotBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string Clean(string? value, int maxLength)
    {
        var result = string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.ReplaceLineEndings(" ").Trim();
        return result.Length > maxLength ? result[..maxLength] : result;
    }

    private static T Safe<T>(Func<T> getter, T fallback)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }
}
