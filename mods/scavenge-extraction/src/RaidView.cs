using System.Collections;
using ScavengeExtraction.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ScavengeExtraction;

// A single embedded upper-stage surface. The native ground and backpack stay usable.
internal sealed class RaidView : MonoBehaviour
{
    private RaidRuntime raid = null!;
    private PixelWindow window = null!;
    private RaidSurface surface = null!, infoSurface = null!;
    private RectTransform panel = null!, map = null!, battle = null!, hand = null!, enemyRow = null!, interactionMenu = null!, factionHolder = null!, damageLayer = null!;
    private TMP_FontAsset font = null!;
    private TextMeshProUGUI status = null!, log = null!, enemyInfo = null!, enemyName = null!;
    private MachineryLabel factionLabel = null!;
    private readonly List<MachineryButton> nodes = new(), actions = new();
    private readonly List<RectTransform> nodeHolders = new(), actionHolders = new();
    private readonly List<TextMeshProUGUI> nodeLabels = new();
    private readonly List<RectTransform> currentMarkers = new();
    private readonly List<(MachineryButton button, RectTransform holder)> enemyButtons = new();
    private readonly List<Action> dynamicActions = new();
    private Image enemyImage = null!, wound = null!, fallen = null!, muzzle = null!, hurt = null!, gunImage = null!;
    private readonly List<NpcCard> cards = new();
    private readonly List<DamageNumber> damageNumbers = new();
    private Material damageMaterial = null!;
    private EnemyState? menuEnemy;
    private int displayedNode = -1;
    private bool interactionsOpen;
    private Vector3 windowScale;
    private string actionSignature = "";
    private float damageUntil;
    private static readonly Color Ink = MachineryStyle.BODY_TEXT, Accent = MachineryStyle.VALUE_TEXT;

    internal static RaidView Create(RaidRuntime runtime)
    {
        var obj = new GameObject("拾荒地图控制");
        var view = obj.AddComponent<RaidView>(); view.raid = runtime;
        view.Build(); return view;
    }
    private void Build()
    {
        surface = new RaidSurface();
        infoSurface = new RaidSurface(190, 340);
        window = new PixelWindow(false, "拾荒", WindowStyle.Machinery);
        window.Attach(new MachineryPanelRow(MachineHelper.Panel("信息", infoSurface, "raid.info"),
            MachineHelper.Panel(null, surface, "raid.map")));
        window.Show();
        windowScale = window.rectTransform.localScale;
        font = MapUIManager.Instance.scavTimeRemainingTMP?.font ?? TMP_Settings.defaultFontAsset;
        panel = Rect("拾荒区域", surface.rectTransform, 0, 0, 1, 1);
        panel.gameObject.AddComponent<RectMask2D>();
        map = Box("交互地图", panel, .018f, .37f, .964f, .61f, new Color(.10f, .11f, .12f));
        map.gameObject.AddComponent<RectMask2D>();
        BlockPointerThrough(map);
        BuildMap();
        status = Text("", infoSurface.rectTransform, .04f, .75f, .92f, .23f, 11, Accent, TextAlignmentOptions.TopLeft);
        status.enableAutoSizing = false;
        Box("信息分隔", infoSurface.rectTransform, .04f, .72f, .92f, .004f, Accent);
        log = Text("", infoSurface.rectTransform, .04f, .025f, .92f, .68f, 11, Ink, TextAlignmentOptions.TopLeft);
        log.enableAutoSizing = false; log.paragraphSpacing = 5;
        for (int i = 0; i < 3; i++)
        {
            int slot = i;
            dynamicActions.Add(() => { });
            var holder = Rect("操作按钮", panel, .018f + i * .324f, .282f, .313f, .07f);
            actionHolders.Add(holder);
            actions.Add(NativeButton("", holder, () => dynamicActions[slot](), 90, 20));
        }
        BuildEnemyRow();
        BuildBattle();
        hurt = Image("受击反馈", panel, .018f, .37f, .964f, .61f, null);
        hurt.color = new Color(.8f, .1f, .08f, 0); hurt.raycastTarget = false;
        BuildDamageLayer();
        Refresh(); LayoutWindow();
    }
    private void BuildMap()
    {
        foreach (var node in Map.Nodes.Select((n, i) => (n, i)))
            foreach (int link in node.n.Links.Where(i => i > node.i))
            {
                var other = Map.Nodes[link];
                var line = Box("路线", map, 0, 0, 0, 0, new Color(.3f, .37f, .36f));
                line.anchorMin = line.anchorMax = Vector2.zero;
                var layout = line.gameObject.AddComponent<RaidMapLine>();
                layout.Parent = map; layout.From = new Vector2(node.n.X, node.n.Y); layout.To = new Vector2(other.X, other.Y);
            }
        for (int i = 0; i < Map.Nodes.Length; i++)
        {
            int index = i; var n = Map.Nodes[i];
            var holder = Rect("地图地点", map, n.X - .085f, n.Y - .06f, .17f, .12f);
            nodeHolders.Add(holder);
            nodes.Add(NativeButton("", holder, () => { if (raid.State.CanMove(index) && !raid.Busy) raid.Move(index); }, 74, 22));
            nodeLabels.Add(Text(n.Name, holder, .01f, .05f, .98f, .90f, 8, Ink, TextAlignmentOptions.Center));
            var marker = Box("当前位置高亮", holder, -.03f, -.08f, 1.06f, 1.16f, Accent);
            marker.SetAsFirstSibling();
            Box("当前位置底色", marker, .02f, .07f, .96f, .86f, new Color(.11f, .17f, .16f));
            Text("▼ 你在这里", marker, -.1f, 1.0f, 1.2f, .42f, 8, Accent, TextAlignmentOptions.Center).fontStyle = FontStyles.Bold;
            currentMarkers.Add(marker);
        }
    }
    private void BuildEnemyRow()
    {
        enemyRow = Box("附近人物", panel, .018f, .018f, .964f, .25f, new Color(.12f, .13f, .14f));
        BlockPointerThrough(enemyRow);
        interactionMenu = Box("人物互动", enemyRow, 0, 0, 0, 0, new Color(.13f, .14f, .15f));
        interactionMenu.anchorMin = interactionMenu.anchorMax = new Vector2(.5f, 0);
        interactionMenu.pivot = new Vector2(.5f, 0); interactionMenu.sizeDelta = new Vector2(156, 18);
        interactionMenu.anchoredPosition = new Vector2(0, 1);
        var choices = new (string text, Action action)[] { ("对话", raid.Talk), ("攻击", raid.Provoke), ("偷窃", raid.Steal) };
        for (int i = 0; i < choices.Length; i++)
        {
            var holder = Rect("人物操作", interactionMenu, i * .337f, .04f, .32f, .92f);
            var action = choices[i].action;
            enemyButtons.Add((NativeButton(choices[i].text, holder, () =>
            {
                var target = menuEnemy;
                interactionsOpen = false;
                if (target != null && raid.State.SelectEnemy(target)) action();
                Refresh();
            }, 48, 16), holder));
        }
        interactionMenu.gameObject.SetActive(false);
    }
    private NpcCard MakeCard()
    {
        var card = new NpcCard();
        card.Holder = Rect("人物卡片", enemyRow, 0, 1, 0, 0);
        card.Holder.anchorMin = card.Holder.anchorMax = new Vector2(.5f, 1);
        card.Holder.pivot = new Vector2(.5f, 1); card.Holder.sizeDelta = new Vector2(64, 64);
        card.Button = NativeButton("", card.Holder, () => ToggleInteractions(card.Target), 64, 64);
        card.Frame = Box("阵营卡框", card.Holder, .025f, .225f, .95f, .75f, Ink).GetComponent<Image>();
        Box("照片底色", card.Holder, .06f, .26f, .88f, .68f, new Color(.07f, .08f, .09f));
        card.Photo = Image("清晰原色头像", card.Holder, .08f, .27f, .84f, .66f, null);
        card.Photo.preserveAspect = true; card.Photo.color = Color.white;
        card.NamePlate = Box("姓名底板", card.Holder, .025f, .015f, .95f, .20f, Ink).GetComponent<Image>();
        card.Name = Text("", card.Holder, .04f, .02f, .92f, .19f, 7.5f, Color.white, TextAlignmentOptions.Center);
        card.Name.fontStyle = FontStyles.Bold;
        cards.Add(card);
        interactionMenu.SetAsLastSibling();
        return card;
    }
    private void ToggleInteractions(EnemyState? target)
    {
        if (raid.Busy || target == null || !raid.State.SelectEnemy(target)) return;
        interactionsOpen = !interactionsOpen || !ReferenceEquals(menuEnemy, target);
        menuEnemy = target; raid.Save(); Refresh();
    }
    private void BuildBattle()
    {
        battle = Box("第一人称交战", panel, .018f, .37f, .964f, .61f, new Color(.04f, .05f, .06f));
        battle.gameObject.AddComponent<RectMask2D>();
        BlockPointerThrough(battle);
        factionHolder = Rect("阵营标签", battle, .025f, .91f, .24f, .075f);
        factionLabel = new MachineryLabel();
        HostWidget(factionLabel, factionHolder);
        enemyName = Text("", battle, .27f, .89f, .46f, .10f, 11, Accent, TextAlignmentOptions.Center);
        enemyName.fontStyle = FontStyles.Bold;
        enemyInfo = Text("", battle, .025f, .81f, .95f, .075f, 9, Ink, TextAlignmentOptions.Center);
        enemyImage = Image("原版阵营角色", battle, .14f, -.12f, .72f, .92f, null);
        enemyImage.preserveAspect = true;
        wound = Image("原版受击帧", battle, .34f, .12f, .32f, .54f, null); wound.preserveAspect = true;
        fallen = Image("原版倒地帧", battle, .15f, -.08f, .70f, .75f, null); fallen.preserveAspect = true;
        hand = Rect("持枪", battle, .54f, -.08f, .46f, .44f);
        gunImage = Image("原版第一人称手臂", hand, 0, 0, 1, 1, null); gunImage.preserveAspect = true;
        muzzle = Image("原版枪口火光", hand, -.04f, .48f, .40f, .48f, StoreUIManager.Instance.muzzleFlash?.sprite);
        muzzle.preserveAspect = true;
        muzzle.gameObject.SetActive(false); wound.gameObject.SetActive(false); fallen.gameObject.SetActive(false);
        battle.gameObject.SetActive(false);
    }
    internal void ShowBattle()
    {
        battle.gameObject.SetActive(true);
        var enemy = raid.State.Enemy;
        if (enemy != null && SpriteDict.Instance != null) enemyImage.sprite = SpriteDict.Instance.GetSprite(enemy.Sprite);
        enemyImage.gameObject.SetActive(true); fallen.gameObject.SetActive(false); wound.gameObject.SetActive(false);
        RefreshGun(); Refresh();
    }
    internal void HideBattle() { battle.gameObject.SetActive(false); Refresh(); }
    private void RefreshGun()
    {
        var nativeArm = StoreUIManager.Instance.arm;
        var renderers = nativeArm != null ? nativeArm.GetComponentsInChildren<SpriteRenderer>(true) : Array.Empty<SpriteRenderer>();
        var sprite = renderers.Where(r => r.sprite != null && !r.name.ToLowerInvariant().Contains("flash"))
            .OrderByDescending(r => r.sprite.rect.width * r.sprite.rect.height).FirstOrDefault()?.sprite;
        gunImage.sprite = sprite;
        gunImage.gameObject.SetActive(sprite != null && raid.Weapon?.IsGameItemType("FIREARM") == true);
    }
    internal void Refresh()
    {
        if (raid == null || panel == null) return;
        var state = raid.State; var current = Map.Nodes[state.Node];
        raid.Panels?.Refresh();
        for (int i = 0; i < nodes.Count; i++)
        {
            bool visible = state.Nodes[i].Visited || current.Links.Contains(i) || i == state.Node;
            var e = state.Nodes[i].GetEnemies().FirstOrDefault(n => !n.Settled);
            string name = visible ? Map.Nodes[i].Name : "未知区域";
            string marker = i == state.Node ? "当前位置" : e != null && !e.Settled ? "有人" : state.Nodes[i].Visited ? "已探索" : "未探索";
            nodeLabels[i].text = name;
            nodeLabels[i].color = i == state.Node ? Accent : new Color(.92f, .93f, .91f);
            nodeLabels[i].fontStyle = i == state.Node ? FontStyles.Bold : FontStyles.Normal;
            currentMarkers[i].gameObject.SetActive(i == state.Node);
            nodes[i].SetDescription(marker);
            nodes[i].SetDisabled(!state.CanMove(i) || raid.Busy);
        }
        var enemy = state.Enemy;
        status.text = state.Phase switch
        {
            RaidPhase.Exploring => $"{current.Name}\n今夜剩余拾荒 {Math.Max(0, ScavHelper.GetScavTimeLeft())} 次",
            RaidPhase.Searching => "搜索进行中……",
            RaidPhase.Combat => "当前武器：" + (raid.Weapon?.GetDisplayName(true) ?? "徒手"),
            RaidPhase.Victory => "对方已倒下但尚未死亡，请选择处置方式。",
            RaidPhase.Defeated => "你已被击败。求饶可能失去全部随身物品。",
            _ => "本次拾荒已经结束。"
        };
        RefreshLog();
        bool fighting = state.Phase is RaidPhase.Combat or RaidPhase.Victory or RaidPhase.Defeated;
        if (fighting && !battle.gameObject.activeSelf) ShowBattle();
        if (!fighting && battle.gameObject.activeSelf) battle.gameObject.SetActive(false);
        string title = fighting ? "交战" : "拾荒";
        if (window.titleString != title) window.SetTitle(title);
        if (enemy != null)
        {
            string faction = RaidRuntime.FactionName(enemy.Faction);
            if (factionLabel.GetText() != faction) factionLabel.SetText(faction);
            enemyName.text = enemy.Name;
            enemyInfo.text = $"生命 {enemy.Hp}/{enemy.MaxHp}　伤害 {enemy.Attack}　护甲 {enemy.Armor}";
        }
        RefreshEnemyRow();
        var signature = state.Phase + ":" + state.CanExtract + ":" + raid.Busy;
        if (signature != actionSignature) { actionSignature = signature; RebuildActions(); }
        foreach (var button in actions) button.SetDisabled(raid.Busy);
    }
    private void RefreshLog()
    {
        var lines = raid.Messages.TakeLast(8).ToList();
        float width = Mathf.Max(1, log.rectTransform.rect.width), height = Mathf.Max(1, log.rectTransform.rect.height);
        while (lines.Count > 1 && log.GetPreferredValues(string.Join("\n\n", lines), width, Mathf.Infinity).y > height)
            lines.RemoveAt(0);
        log.text = string.Join("\n\n", lines);
    }
    private void RefreshEnemyRow()
    {
        if (displayedNode != raid.State.Node)
        {
            displayedNode = raid.State.Node; interactionsOpen = false; menuEnemy = null;
        }
        var present = raid.State.Nodes[raid.State.Node].GetEnemies().Where(e => !e.Settled).ToArray();
        while (cards.Count < present.Length) MakeCard();
        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            card.Holder.gameObject.SetActive(i < present.Length);
            if (i >= present.Length) { card.Target = null; continue; }
            var enemy = present[i];
            if (!ReferenceEquals(card.Target, enemy))
            {
                card.Target = enemy; card.Name.text = enemy.Name;
                card.Photo.sprite = SpriteDict.Instance.GetSprite(enemy.Sprite);
                card.Button.SetDescription(RaidRuntime.FactionName(enemy.Faction) + " · " + enemy.Name + "\n点击互动，或拖入物品对其使用。");
            }
            Color faction = FactionColor(enemy.Faction);
            card.Frame.color = ReferenceEquals(raid.State.Enemy, enemy) ? Color.Lerp(faction, Color.white, .25f) : faction;
            card.NamePlate.color = Color.Lerp(faction, Color.black, .42f);
            card.Button.SetDisabled(raid.Busy || raid.State.Phase != RaidPhase.Exploring || enemy.Hp <= 0);
        }
        if (raid.State.Phase != RaidPhase.Exploring || raid.Busy || menuEnemy == null
            || !present.Contains(menuEnemy) || menuEnemy.Hp <= 0) interactionsOpen = false;
        interactionMenu.gameObject.SetActive(interactionsOpen);
        foreach (var entry in enemyButtons) entry.button.SetDisabled(raid.Busy);
    }
    private static Color FactionColor(string faction)
    {
        string hex = StoreReputation.GetFactionColorByID(faction).TrimStart('#');
        return ColorUtility.TryParseHtmlString("#" + hex, out var color) ? color : Accent;
    }
    private void RebuildActions()
    {
        for (int i = 0; i < actions.Count; i++) { actionHolders[i].gameObject.SetActive(false); dynamicActions[i] = () => { }; }
        int index = 0;
        void Add(string text, Action action)
        {
            if (index >= actions.Count) return;
            actions[index].SetText(text); actionHolders[index].gameObject.SetActive(true); dynamicActions[index++] = action;
        }
        switch (raid.State.Phase)
        {
            case RaidPhase.Exploring:
                Add("拾荒", () => raid.Search());
                if (raid.State.CanExtract) Add("撤离", raid.Extract);
                break;
            case RaidPhase.Searching: Add("搜索中……", () => { }); break;
            case RaidPhase.Combat:
                Add("攻击", raid.Attack); Add("防御", raid.Defend); Add("逃跑", raid.Flee); break;
            case RaidPhase.Victory:
                Add("击杀", () => raid.ResolveVictory(VictoryChoice.Kill));
                Add("击晕", () => raid.ResolveVictory(VictoryChoice.KnockOut));
                Add("放走", () => raid.ResolveVictory(VictoryChoice.Release)); break;
            case RaidPhase.Defeated: Add("求饶", raid.Beg); Add("认命", raid.Die); break;
        }
        for (int i = 0; i < index; i++)
        {
            float left = .018f + (.964f - index * .30f - Math.Max(0, index - 1) * .024f) * .5f + i * .324f;
            actionHolders[i].anchorMin = new Vector2(left, .282f);
            actionHolders[i].anchorMax = new Vector2(left + .30f, .352f);
        }
    }
    internal void AnimateAttack(GameItem? weapon, int damage, bool knockout, Action complete)
        => StartCoroutine(AttackAnimation(weapon, damage, knockout, complete));
    private IEnumerator AttackAnimation(GameItem? weapon, int damage, bool knockout, Action complete)
    {
        RefreshGun();
        bool hit = damage > 0, damageShown = false;
        var native = StoreUIManager.Instance;
        bool firearm = weapon?.IsGameItemType("FIREARM") == true;
        RaidRuntime.TrySound(!string.IsNullOrEmpty(weapon?.soundUse) ? weapon!.soundUse
            : firearm ? AudioHelper.PISTOL_FIRE : weapon?.IsTag(HusbandryHelper.BLADE_ITEM_TAG) == true
                ? AudioHelper.KNIFE_STAB : AudioHelper.MELEE_BLUNT_IMPACT);
        Vector2 origin = hand.anchoredPosition;
        Vector2 enemyOrigin = enemyImage.rectTransform.anchoredPosition;
        float time = 0;
        muzzle.gameObject.SetActive(firearm && muzzle.sprite != null);
        while (time < .30f)
        {
            time += Time.unscaledDeltaTime;
            if (hit && !damageShown && time >= .06f) { ShowEnemyDamage(damage); damageShown = true; }
            float recoil = time < .05f ? time / .05f : Mathf.Clamp01(1 - (time - .05f) / .25f);
            hand.anchoredPosition = origin + new Vector2(8, 5) * recoil;
            if (hit)
            {
                float strength = Mathf.Clamp01(1 - time / .30f) * 3;
                enemyImage.rectTransform.anchoredPosition = enemyOrigin
                    + new Vector2(Mathf.Sin(time * 105), Mathf.Cos(time * 87) * .45f) * strength;
            }
            muzzle.color = new Color(1, 1, 1, Mathf.Clamp01(1 - time / .24f));
            yield return null;
        }
        hand.anchoredPosition = origin; muzzle.gameObject.SetActive(false);
        enemyImage.rectTransform.anchoredPosition = enemyOrigin;
        wound.gameObject.SetActive(hit && native.shootingWoundSprites.Count > 0);
        foreach (var sprite in native.shootingWoundSprites.Take(!hit ? 0 : knockout ? native.shootingWoundSprites.Count : 2))
        {
            wound.sprite = sprite; yield return new WaitForSecondsRealtime(Mathf.Max(.04f, native.woundFrameDuration));
        }
        wound.gameObject.SetActive(false);
        if (knockout)
        {
            enemyImage.gameObject.SetActive(false);
            fallen.gameObject.SetActive(native.shootingBodySprites.Count > 0);
            foreach (var sprite in native.shootingBodySprites)
            {
                fallen.sprite = sprite; yield return new WaitForSecondsRealtime(Mathf.Max(.05f, native.bodyFrameDuration));
            }
            RaidRuntime.TrySound("body_collapse_floor");
        }
        complete();
    }
    internal void FlashDamage() => damageUntil = Time.unscaledTime + .3f;
    private void LateUpdate()
    {
        if (hurt != null) hurt.color = new Color(.8f, .1f, .08f, Mathf.Clamp01((damageUntil - Time.unscaledTime) / .3f) * .32f);
        bool hide = GenericConfirmationUIManager.Instance?.panel?.activeInHierarchy == true || GameOverUIManager.Instance?.IsShowing == true;
        if (window?.handler != null) window.handler.gameObject.SetActive(!hide);
        if (!hide) { raid.Panels?.FitToScreen(); LayoutWindow(); FitNativeButtons(); }
        if (damageLayer != null) { damageLayer.gameObject.SetActive(!hide); UpdateDamageNumbers(); }
    }
    private void LayoutWindow()
    {
        float bottom = NativeWindowLayout.Bounds(EmporiumEntry.Instance.afterhourWindow).yMax + Screen.height * .012f;
        NativeWindowLayout.Place(window, new Rect(Screen.width * .03f, bottom,
            Screen.width * .94f, Math.Max(1, Screen.height * .98f - bottom)), windowScale, true);
    }
    private void FitNativeButtons()
    {
        for (int i = 0; i < nodes.Count; i++) Fit(nodes[i], nodeHolders[i]);
        for (int i = 0; i < actions.Count; i++) Fit(actions[i], actionHolders[i]);
        foreach (var entry in enemyButtons) Fit(entry.button, entry.holder);
        Fit(factionLabel, factionHolder);
        var active = cards.Where(c => c.Holder.gameObject.activeSelf).ToArray();
        float spacing = Mathf.Min(76, enemyRow.rect.width / Mathf.Max(1, active.Length));
        for (int i = 0; i < active.Length; i++)
        {
            var card = active[i];
            card.Holder.anchoredPosition = new Vector2((i - (active.Length - 1) * .5f) * spacing, -2);
            card.Holder.localScale = Vector3.one * Mathf.Min(1, spacing / 68);
            Fit(card.Button, card.Holder);
        }
    }
    internal EnemyState? EnemyDropTarget(Vector2 screenPoint, TreeNodeRender ignore)
    {
        if (!window.handler.gameObject.activeInHierarchy || raid.State.Phase is not (RaidPhase.Exploring or RaidPhase.Combat)) return null;
        var camera = NativeWindowLayout.CameraFor(panel);
        if (RenderHandler.RaycastFindParentElement<PixelWindow>(screenPoint, ignore) != window) return null;
        foreach (var card in cards)
            if (card.Target != null && card.Holder.gameObject.activeInHierarchy
                && RectTransformUtility.RectangleContainsScreenPoint(card.Holder, screenPoint, camera))
                return raid.State.MayUseOnEnemy(card.Target) ? card.Target : null;
        var target = raid.State.Enemy;
        return target != null && raid.State.MayUseOnEnemy(target) && battle.gameObject.activeInHierarchy
            && RectTransformUtility.RectangleContainsScreenPoint(battle, screenPoint, camera)
            && RectTransformUtility.RectangleContainsScreenPoint(enemyImage.rectTransform, screenPoint, camera) ? target : null;
    }
    private static void Fit(PixelElement button, RectTransform holder)
    {
        float scale = Mathf.Min(1, holder.rect.width / Mathf.Max(1, button.widthPixels),
            holder.rect.height / Mathf.Max(1, button.heightPixels));
        var r = button.rectTransform; r.localScale = Vector3.one * scale;
        r.anchoredPosition = new Vector2((holder.rect.width - button.widthPixels * scale) * .5f,
            -(holder.rect.height - button.heightPixels * scale) * .5f);
    }
    internal void SetVisible(bool visible) { if (window != null) window.handler.gameObject.SetActive(visible); }
    internal void Dispose()
    {
        StopAllCoroutines();
        if (damageLayer != null) Destroy(damageLayer.gameObject);
        if (damageMaterial != null) Destroy(damageMaterial);
        window?.Destroy(); Destroy(gameObject);
    }

    private RectTransform Rect(string name, Transform parent, float x, float y, float w, float h)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var r = go.GetComponent<RectTransform>(); r.SetParent(parent, false);
        r.anchorMin = new Vector2(x, y); r.anchorMax = new Vector2(x + w, y + h);
        r.offsetMin = r.offsetMax = Vector2.zero; return r;
    }
    private RectTransform Box(string name, Transform parent, float x, float y, float w, float h, Color color)
    {
        var r = Rect(name, parent, x, y, w, h);
        var image = r.gameObject.AddComponent<Image>(); image.color = color; image.raycastTarget = false; return r;
    }
    private Image Image(string name, Transform parent, float x, float y, float w, float h, Sprite? sprite)
    {
        var r = Rect(name, parent, x, y, w, h);
        var image = r.gameObject.AddComponent<Image>(); image.sprite = sprite; image.raycastTarget = false; return image;
    }
    private TextMeshProUGUI Text(string text, Transform parent, float x, float y, float w, float h,
        float size, Color color, TextAlignmentOptions alignment = TextAlignmentOptions.Left)
    {
        var r = Rect("文本", parent, x, y, w, h);
        var label = r.gameObject.AddComponent<TextMeshProUGUI>();
        label.font = font; label.fontSize = size; label.color = color; label.text = text;
        label.enableAutoSizing = true; label.fontSizeMin = Math.Min(5, size); label.fontSizeMax = size;
        label.alignment = alignment; label.enableWordWrapping = true; label.overflowMode = TextOverflowModes.Truncate;
        label.raycastTarget = false; label.richText = true; return label;
    }
    private MachineryButton NativeButton(string text, RectTransform holder, Action action, int width = 60, int height = 16)
    {
        var button = new MachineryButton(text, () =>
        {
            try { action(); }
            catch (Exception error) { Debug.LogError("[ScavengeExtraction] 操作失败：" + error); raid.Log("操作未完成：" + error.Message); }
        }, width, height);
        var host = TreeNodeRender.Instantiate().SetParent(surface.handler);
        host.rectTransform.SetParent(holder, false);
        host.image.enabled = false;
        host.rectTransform.anchorMin = host.rectTransform.anchorMax = new Vector2(0, 1);
        host.rectTransform.pivot = new Vector2(0, 1);
        host.rectTransform.anchoredPosition = Vector2.zero;
        host.rectTransform.sizeDelta = Vector2.zero;
        button.handler.SetParent(host);
        return button;
    }
    private void BlockPointerThrough(RectTransform holder)
    {
        var hit = TreeNodeRender.Instantiate().SetParent(surface.handler);
        hit.rectTransform.SetParent(holder, false);
        hit.rectTransform.anchorMin = Vector2.zero; hit.rectTransform.anchorMax = Vector2.one;
        hit.rectTransform.offsetMin = hit.rectTransform.offsetMax = Vector2.zero;
        hit.image.enabled = true; hit.image.sprite = null;
        hit.image.color = Color.clear; hit.image.raycastTarget = true;
    }

    private void HostWidget(PixelElement widget, RectTransform holder)
    {
        var host = TreeNodeRender.Instantiate().SetParent(surface.handler);
        host.rectTransform.SetParent(holder, false); host.image.enabled = false;
        host.rectTransform.anchorMin = host.rectTransform.anchorMax = new Vector2(0, 1);
        host.rectTransform.pivot = new Vector2(0, 1); host.rectTransform.anchoredPosition = Vector2.zero;
        host.rectTransform.sizeDelta = Vector2.zero; widget.handler.SetParent(host);
    }
    private void BuildDamageLayer()
    {
        damageLayer = Rect("拾荒伤害飘字", RenderHandler.current.overlayRoot.rectTransform, 0, 0, 1, 1);
        damageMaterial = new Material(font.material);
        if (damageMaterial.HasProperty(ShaderUtilities.ID_OutlineWidth))
        {
            damageMaterial.SetFloat(ShaderUtilities.ID_OutlineWidth, .22f);
            damageMaterial.SetColor(ShaderUtilities.ID_OutlineColor, Color.black);
        }
    }
    internal void ShowEnemyDamage(int amount) => AddDamageNumber(amount, true);
    internal void ShowPlayerDamage(int amount) => AddDamageNumber(amount, false);
    private void AddDamageNumber(int amount, bool enemy)
    {
        if (amount <= 0 || damageLayer == null) return;
        var recent = damageNumbers.LastOrDefault(n => n.IsEnemy == enemy && Time.unscaledTime - n.Born < .08f
            && (!enemy || ReferenceEquals(n.Enemy, raid.State.Enemy)));
        if (recent != null)
        {
            recent.Amount += amount; recent.Label.text = "-" + recent.Amount;
            return;
        }
        if (damageNumbers.Count >= 12)
        { Destroy(damageNumbers[0].Label.gameObject); damageNumbers.RemoveAt(0); }
        var label = Text("-" + amount, damageLayer, 0, 0, 0, 0, 16,
            enemy ? new Color(1, .85f, .30f) : new Color(1, .28f, .23f), TextAlignmentOptions.Center);
        label.enableAutoSizing = false; label.enableWordWrapping = false;
        label.fontStyle = FontStyles.Bold; label.fontSharedMaterial = damageMaterial; label.extraPadding = true;
        label.rectTransform.anchorMin = label.rectTransform.anchorMax = Vector2.zero;
        label.rectTransform.pivot = new Vector2(.5f, 0); label.rectTransform.sizeDelta = new Vector2(100, 26);
        damageNumbers.Add(new DamageNumber { Label = label, IsEnemy = enemy, Amount = amount,
            Enemy = enemy ? raid.State.Enemy : null, Born = Time.unscaledTime,
            Lane = damageNumbers.Count(n => n.IsEnemy == enemy) % 3 });
    }
    private Vector3 EnemyHeadPosition()
    {
        var r = enemyImage.rectTransform;
        float height = r.rect.height;
        if (enemyImage.sprite != null)
            height = Mathf.Min(height, r.rect.width * enemyImage.sprite.rect.height / Mathf.Max(1, enemyImage.sprite.rect.width));
        return r.TransformPoint(new Vector3(r.rect.center.x, r.rect.center.y + height * .5f));
    }
    private void UpdateDamageNumbers()
    {
        var camera = NativeWindowLayout.CameraFor(damageLayer);
        float parentScale = Mathf.Max(.01f, Mathf.Abs(damageLayer.lossyScale.y));
        float screenFont = Mathf.Clamp(16 * Mathf.Abs(window.rectTransform.lossyScale.y), 24, 40);
        for (int i = damageNumbers.Count - 1; i >= 0; i--)
        {
            var number = damageNumbers[i]; float age = Time.unscaledTime - number.Born;
            if (age >= 1.25f || (number.IsEnemy && (!battle.gameObject.activeSelf || !ReferenceEquals(number.Enemy, raid.State.Enemy))))
            { Destroy(number.Label.gameObject); damageNumbers.RemoveAt(i); continue; }
            Vector3 world = number.IsEnemy ? EnemyHeadPosition() : raid.Panels?.HealthNumberPosition ?? hand.position;
            var anchor = number.IsEnemy ? enemyImage.rectTransform : raid.Panels?.HealthAnchor ?? hand;
            var point = RectTransformUtility.WorldToScreenPoint(NativeWindowLayout.CameraFor(anchor), world);
            point += new Vector2(number.Lane == 1 ? -36 : number.Lane == 2 ? 36 : 0, 6 + age / 1.25f * 30);
            point.x = Mathf.Clamp(point.x, 55, Screen.width - 55); point.y = Mathf.Clamp(point.y, 12, Screen.height - 45);
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(damageLayer, point, camera, out var local))
                number.Label.rectTransform.localPosition = local;
            number.Label.rectTransform.localScale = Vector3.one * (screenFont / (16 * parentScale));
            number.Label.alpha = age < 1 ? 1 : Mathf.Clamp01((1.25f - age) / .25f);
        }
    }
    private sealed class NpcCard
    {
        internal RectTransform Holder = null!;
        internal Image Frame = null!, Photo = null!, NamePlate = null!;
        internal TextMeshProUGUI Name = null!;
        internal MachineryButton Button = null!;
        internal EnemyState? Target;
    }
    private sealed class DamageNumber
    {
        internal TextMeshProUGUI Label = null!;
        internal EnemyState? Enemy;
        internal int Amount, Lane;
        internal bool IsEnemy;
        internal float Born;
    }

}

internal sealed class RaidMapLine : MonoBehaviour
{
    internal RectTransform Parent = null!;
    internal Vector2 From, To;
    private Vector2 previous;
    private void LateUpdate()
    {
        var size = Parent.rect.size;
        if (size == previous) return; previous = size;
        var start = Vector2.Scale(size, From); var end = Vector2.Scale(size, To); var delta = end - start;
        var r = (RectTransform)transform;
        r.anchoredPosition = (start + end) * .5f; r.sizeDelta = new Vector2(delta.magnitude, 2);
        r.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
    }
}
