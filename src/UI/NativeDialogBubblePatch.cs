using HarmonyLib;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace AIWorldPawnshop.UI;

internal enum NativeDialogScene
{
    Store,
    Phone
}

[HarmonyPatch(typeof(DialogUIManager), nameof(DialogUIManager.DisplayChoice))]
internal static class NativeStoreDialogChoicePatch
{
    private static void Postfix(DialogUIManager __instance)
    {
        NativeDialogEntry.AttachStoreChoices(__instance);
    }
}

[HarmonyPatch(typeof(DialoguePhoneUIManager), nameof(DialoguePhoneUIManager.DisplayChoice))]
internal static class NativePhoneDialogChoicePatch
{
    private static void Postfix(DialoguePhoneUIManager __instance)
    {
        NativeDialogEntry.AttachPhoneChoices(__instance);
    }
}

[HarmonyPatch(typeof(DialoguePhoneUIManager), nameof(DialoguePhoneUIManager.EndConversation))]
internal static class NativePhoneEndConversationPatch
{
    private static void Postfix() => NativeDialogEntry.HidePhoneChoices();
}

[HarmonyPatch(typeof(DialoguePhoneUIManager), nameof(DialoguePhoneUIManager.InterruptDialog))]
internal static class NativePhoneInterruptPatch
{
    private static void Postfix() => NativeDialogEntry.HidePhoneChoices();
}

[HarmonyPatch(typeof(PhoneUIManager), nameof(PhoneUIManager.StopCall))]
internal static class NativePhoneStopCallPatch
{
    private static void Postfix()
    {
        NativeDialogEntry.HidePhoneChoices();
        AIWorldPawnshopMod.Instance?.OnNativePhoneEnded();
    }
}

[HarmonyPatch(typeof(PhoneUIManager), nameof(PhoneUIManager.CloseUI))]
internal static class NativePhoneClosePatch
{
    private static void Postfix()
    {
        NativeDialogEntry.HidePhoneChoices();
        AIWorldPawnshopMod.Instance?.OnNativePhoneEnded();
    }
}

[HarmonyPatch(typeof(PhoneUIManager), nameof(PhoneUIManager.OnPhoneDialogEnd))]
internal static class NativePhoneDialogEndPatch
{
    private static void Postfix() => NativeDialogEntry.HidePhoneChoices();
}

internal static class NativeDialogEntry
{
    private const string EntryText = "......";
    private const string EntryObjectName = "AIWorldPawnshop.NativeChatChoice";
    private const string PhoneEntryObjectName = "AIWorldPawnshop.NativePhoneChatChoice";
    private const string HeadEntryObjectName = "AIWorldPawnshop.NativeChatHeadChoice";
    private const float RetryIntervalSeconds = 0.2f;
    private const float PhoneEntryGap = 12f;
    private const float HeadEntryOffsetX = -17f;
    private const float HeadEntryOffsetY = 195f;
    private const float HeadEntryScale = 0.7f;

    private static float _nextRetryTime;
    private static string _storeSessionKey = string.Empty;
    private static GameObject? _storeHeadEntry;
    private static GameObject? _phoneEntry;
    private static DialogChoiceBubble? _sceneTemplate;
    private static float _nextTemplateSearchTime;
    private static bool _storeHeadUsesPhoneStyle;
    private static string _lastHeadState = string.Empty;
    private static string _lastHeadPositionLogKey = string.Empty;

    public static void Update()
    {
        if (Time.unscaledTime < _nextRetryTime)
            return;

        _nextRetryTime = Time.unscaledTime + RetryIntervalSeconds;

        try
        {
            TryUpdatePhoneEntry();
            TryUpdateStoreHeadEntry();
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[当铺世界] 原版对话入口轮询失败：{ex.Message}");
        }
    }

    public static void Attach(
        Il2CppSystem.Collections.Generic.List<DialogChoiceBubble>? bubbles,
        NativeDialogScene scene,
        bool activeOnly = false)
    {
        try
        {
            if (bubbles is null || bubbles.Count == 0)
                return;

            DialogChoiceBubble? existingEntry = null;
            DialogChoiceBubble? spare = null;
            DialogChoiceBubble? template = null;
            for (var i = bubbles.Count - 1; i >= 0; i--)
            {
                var existing = bubbles[i];
                if (!IsAlive(existing))
                    continue;

                var existingObject = existing.gameObject;
                if (!IsAlive(existingObject))
                    continue;

                var existingName = existingObject.name;
                if (existingName.Equals(HeadEntryObjectName, StringComparison.Ordinal) ||
                    existingName.Equals(PhoneEntryObjectName, StringComparison.Ordinal))
                    continue;

                if (existingName.Equals(EntryObjectName, StringComparison.Ordinal))
                {
                    existingEntry ??= existing;
                    continue;
                }

                if (!existingObject.activeInHierarchy)
                    spare ??= existing;
                if (activeOnly && !existingObject.activeInHierarchy)
                    continue;

                template ??= existing;
            }

            if (existingEntry is not null)
            {
                // Do not wake a stale custom slot while the native manager is
                // still preparing its choices. The next poll/postfix will retry
                // once the original slot is active.
                if (activeOnly && !existingEntry.gameObject.activeInHierarchy)
                    return;
                ConfigureEntry(existingEntry, scene);
                return;
            }

            if (!activeOnly && spare is not null)
            {
                ConfigureEntry(spare, scene);
                MelonLogger.Msg($"[当铺世界] 已将 ...... 放入原版{(scene == NativeDialogScene.Phone ? "电话" : "对话")}预置选项槽");
                return;
            }

            // An active-only attach is called from DisplayChoice. If every native
            // slot is still inactive, cloning/reusing one here would be undone by
            // the original manager a frame later. Let the polling retry handle it.
            if (activeOnly && template is null)
                return;

            if (!IsAlive(template))
                return;

            Transform? parent;
            try
            {
                parent = template!.transform.parent;
            }
            catch
            {
                return;
            }

            if (!IsAlive(parent))
                return;

            var previous = parent!.Find(EntryObjectName);
            if (previous is not null && IsAlive(previous.gameObject))
            {
                var previousBubble = previous.gameObject.GetComponent<DialogChoiceBubble>();
                if (IsAlive(previousBubble))
                    ConfigureEntry(previousBubble!, scene);
                return;
            }

            var templateObject = template.gameObject;
            if (!IsAlive(templateObject))
                return;

            var clone = UnityEngine.Object.Instantiate(templateObject, parent, false);
            if (!IsAlive(clone))
                return;

            clone.name = EntryObjectName;
            clone.transform.SetAsLastSibling();

            var bubble = clone.GetComponent<DialogChoiceBubble>();
            if (!IsAlive(bubble))
            {
                UnityEngine.Object.Destroy(clone);
                return;
            }

            if (!ConfigureEntry(bubble!, scene))
            {
                UnityEngine.Object.Destroy(clone);
                return;
            }

            MelonLogger.Msg($"[当铺世界] 已将 ...... 加入原版{(scene == NativeDialogScene.Phone ? "电话" : "对话")}选项");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[当铺世界] 原版{(scene == NativeDialogScene.Phone ? "电话" : "对话")}入口挂载失败：{ex.Message}");
        }
    }

    public static void AttachStoreChoices(DialogUIManager? manager)
    {
        try
        {
            if (!IsAlive(manager))
                return;

            Attach(manager!.dialogChoiceBubbles, NativeDialogScene.Store, activeOnly: true);
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[当铺世界] 原版对话选项入口挂载失败：{ex.Message}");
        }
    }

    public static void AttachPhoneChoices(DialoguePhoneUIManager? manager)
    {
        try
        {
            if (!IsAlive(manager))
                return;

            // Phone choices are managed by the native layout and include the
            // original "end call" button.  Never add our entry to that list:
            // doing so makes it appear before the call is connected and lets
            // the layout group overlap it with the native button.
            TryUpdatePhoneEntry(manager);
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[当铺世界] 原版电话选项入口挂载失败：{ex.Message}");
        }
    }

    internal static void HidePhoneChoices()
    {
        try
        {
            if (IsAlive(_phoneEntry))
                _phoneEntry!.SetActive(false);
            else
                _phoneEntry = null;
        }
        catch
        {
            _phoneEntry = null;
        }
    }

    private static void TryUpdatePhoneEntry(DialoguePhoneUIManager? supplied = null)
    {
        var manager = supplied;
        try
        {
            manager ??= DialoguePhoneUIManager.Instance;
            if (!IsPhoneEntryReady(manager))
            {
                HidePhoneChoices();
                return;
            }

            var bubbles = manager!.dialogChoiceBubbles;
            var template = FindTemplate(bubbles);
            if (!IsAlive(template))
            {
                HidePhoneChoices();
                return;
            }

            var parent = FindLayoutFreeAncestor(template!.transform.parent);
            if (!IsAlive(parent))
            {
                HidePhoneChoices();
                return;
            }

            var entry = GetOrCreatePhoneEntry(template!, parent!);
            if (!IsAlive(entry) || !PositionPhoneEntry(entry!, template!, bubbles))
            {
                HidePhoneChoices();
                return;
            }
        }
        catch
        {
            HidePhoneChoices();
        }
    }

    private static bool IsPhoneEntryReady(DialoguePhoneUIManager? manager)
    {
        try
        {
            if (!IsAlive(manager))
                return false;

            // The native end-call choice is the authoritative connected-state
            // signal.  PhoneUIManager.currentPhoneClient can be temporarily
            // null for valid calls, so it must not suppress the entry here.
            return HasActiveNativeChoice(manager!.dialogChoiceBubbles);
        }
        catch
        {
            return false;
        }
    }

    private static GameObject? GetOrCreatePhoneEntry(
        DialogChoiceBubble template,
        Transform parent)
    {
        if (!IsAlive(template) || !IsAlive(parent))
            return null;

        try
        {
            if (IsAlive(_phoneEntry))
            {
                if (_phoneEntry!.transform.parent != parent)
                    _phoneEntry.transform.SetParent(parent, false);

                var existingBubble = _phoneEntry.GetComponent<DialogChoiceBubble>();
                if (IsAlive(existingBubble) && ConfigurePhoneEntry(existingBubble!))
                    return _phoneEntry;

                UnityEngine.Object.Destroy(_phoneEntry);
                _phoneEntry = null;
            }

            var templateObject = template.gameObject;
            if (!IsAlive(templateObject))
                return null;

            var clone = UnityEngine.Object.Instantiate(templateObject, parent, false);
            if (!IsAlive(clone))
                return null;

            clone.name = PhoneEntryObjectName;
            var bubble = clone.GetComponent<DialogChoiceBubble>();
            if (!IsAlive(bubble) || !ConfigurePhoneEntry(bubble!))
            {
                UnityEngine.Object.Destroy(clone);
                return null;
            }

            clone.transform.SetAsLastSibling();
            _phoneEntry = clone;
            return clone;
        }
        catch
        {
            return null;
        }
    }

    private static bool ConfigurePhoneEntry(DialogChoiceBubble bubble)
    {
        try
        {
            if (!IsAlive(bubble))
                return false;

            var gameObject = bubble!.gameObject;
            if (!IsAlive(gameObject))
                return false;

            gameObject.name = PhoneEntryObjectName;
            gameObject.SetActive(true);
            var button = IsAlive(bubble.button)
                ? bubble.button
                : gameObject.GetComponent<Button>();
            var label = IsAlive(bubble.TMP)
                ? bubble.TMP
                : gameObject.GetComponentInChildren<TextMeshProUGUI>(true);
            if (!IsAlive(button) || !IsAlive(label))
            {
                gameObject.SetActive(false);
                return false;
            }

            label!.text = EntryText;
            label.alignment = TextAlignmentOptions.Center;
            bubble.index = -1;
            button!.onClick.RemoveAllListeners();
            button.onClick.AddListener((UnityAction)(() => OnEntryClicked(NativeDialogScene.Phone)));
            bubble.SetInteractable(true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool PositionPhoneEntry(
        GameObject entry,
        DialogChoiceBubble template,
        Il2CppSystem.Collections.Generic.List<DialogChoiceBubble>? bubbles)
    {
        if (!IsAlive(entry) || !IsAlive(template))
            return false;

        try
        {
            var anchor = FindLastActiveChoice(bubbles);
            if (!IsAlive(anchor))
                return false;

            var entryRect = entry.GetComponent<RectTransform>();
            var anchorRect = anchor!.gameObject.GetComponent<RectTransform>();
            if (!IsAlive(entryRect) || !IsAlive(anchorRect))
                return false;

            var height = MathF.Max(
                24f,
                anchorRect!.rect.height * Mathf.Abs(anchorRect.lossyScale.y));
            entry.transform.position = anchorRect.transform.position +
                                       Vector3.down * (height + PhoneEntryGap);
            entry.transform.rotation = anchorRect.transform.rotation;
            entry.transform.localScale = template.transform.localScale;
            entry.SetActive(true);
            return entry.activeInHierarchy;
        }
        catch
        {
            entry.SetActive(false);
            return false;
        }
    }

    private static DialogChoiceBubble? FindLastActiveChoice(
        Il2CppSystem.Collections.Generic.List<DialogChoiceBubble>? bubbles)
    {
        if (bubbles is null)
            return null;

        for (var i = bubbles.Count - 1; i >= 0; i--)
        {
            try
            {
                var candidate = bubbles[i];
                if (!IsAlive(candidate) || !IsAlive(candidate!.gameObject))
                    continue;
                var name = candidate.gameObject.name;
                if (name.Equals(EntryObjectName, StringComparison.Ordinal) ||
                    name.Equals(PhoneEntryObjectName, StringComparison.Ordinal) ||
                    name.Equals(HeadEntryObjectName, StringComparison.Ordinal) ||
                    !candidate.gameObject.activeInHierarchy)
                    continue;
                return candidate;
            }
            catch
            {
                // Ignore a destroyed native slot during a scene transition.
            }
        }

        return null;
    }

    private static void TryUpdateStoreHeadEntry()
    {
        StoreUIManager? storeUi;
        StoreClientMono? clientObject;
        GameObject? clientGameObject;
        try
        {
            storeUi = StoreUIManager.Instance;
            if (!IsAlive(storeUi))
            {
                ResetStoreSession();
                return;
            }

            clientObject = storeUi!.clientGameObject;
            if (!IsAlive(clientObject))
            {
                ResetStoreSession();
                return;
            }

            clientGameObject = clientObject!.gameObject;
        }
        catch
        {
            // Unity can leave a managed wrapper behind for one frame while the
            // native scene object is being destroyed. Treat that as no customer;
            // the next poll will reacquire the newly-created object.
            ResetStoreSession();
            return;
        }

        string sessionKey;
        try
        {
            // A scene transition can invalidate the managed GameObject wrapper
            // between the first check and the property access.  Keep both
            // Unity calls in the same guarded boundary so the poll never
            // leaks a destroyed-object exception into the update loop.
            if (!IsAlive(clientGameObject) || !clientGameObject!.activeInHierarchy)
            {
                ResetStoreSession();
                return;
            }

            sessionKey = clientGameObject.GetInstanceID().ToString();
        }
        catch
        {
            ResetStoreSession();
            return;
        }

        if (!sessionKey.Equals(_storeSessionKey, StringComparison.Ordinal))
        {
            _storeSessionKey = sessionKey;
            _lastHeadPositionLogKey = string.Empty;
            HideStoreHeadEntry();
        }

        var manager = GetDialogManager();
        if (manager is null)
        {
            HideStoreHeadEntry();
            return;
        }

        bool dialogueActive;
        Il2CppSystem.Collections.Generic.List<DialogChoiceBubble>? storeBubbles;
        try
        {
            storeBubbles = manager.dialogChoiceBubbles;
            // `currentDialog` remains populated for a short time after the native
            // flow closes. It is not an activity signal: using it here hides the
            // free-chat entry until another conversation happens. The native flag
            // is the primary open/closed state; an active native choice slot is a
            // concrete fallback for builds where the flag changes one frame later.
            dialogueActive = manager.isConversationActiveFlag != 0 ||
                             HasActiveNativeChoice(storeBubbles);
        }
        catch
        {
            HideStoreHeadEntry();
            return;
        }

        if (dialogueActive)
        {
            // The Harmony hook covers the normal path. The poll is a retry for
            // scene transitions where DisplayChoice ran before the list existed.
            Attach(storeBubbles, NativeDialogScene.Store, activeOnly: true);
            HideStoreHeadEntry();
            return;
        }

        bool clientMoving;
        try
        {
            clientMoving = clientObject!.IsMoving();
        }
        catch
        {
            // A partially initialized StoreClientMono can throw here for a
            // frame. The GameObject is already known to be active, so keep the
            // entry eligible and let the next poll correct its position instead
            // of hiding it indefinitely behind a transient wrapper failure.
            clientMoving = false;
        }

        if (clientMoving)
        {
            HideStoreHeadEntry();
            return;
        }

        var storeTemplate = FindTemplate(storeBubbles);
        var phoneTemplate = FindPhoneTemplate();
        var sceneTemplate = FindSceneTemplate();
        // The customer is in the store, so prefer the store choice canvas. The
        // phone template is only a fallback for scenes that have not created the
        // store choice list yet. A scene-wide lookup covers the first customer,
        // before either manager has populated its lazy choice list.
        var template = storeTemplate ?? phoneTemplate ?? sceneTemplate;
        if (!IsAlive(template))
        {
            HideStoreHeadEntry();
            return;
        }

        var anchor = GetStoreHeadAnchor(manager, clientGameObject);
        var parent = FindHeadEntryParent(template!, anchor);

        if (!IsAlive(parent))
        {
            HideStoreHeadEntry();
            return;
        }

        if (!IsAlive(anchor))
        {
            HideStoreHeadEntry();
            return;
        }

        // Keep the entry under a layout-free UI parent. The name bubble is only a
        // position anchor; attaching below its transient dialogue panel can make
        // the entry disappear as soon as the native line closes.
        var entry = GetOrCreateHeadEntry(
            template!,
            parent,
            storeTemplate is null && phoneTemplate is not null);
        if (!IsAlive(entry) || !PositionHeadEntry(entry!, anchor, template!))
        {
            HideStoreHeadEntry();
            return;
        }

        var state = $"show:{sessionKey}";
        if (state != _lastHeadState)
        {
            _lastHeadState = state;
            MelonLogger.Msg("[当铺世界] 已在普通客人头顶显示 ...... 入口");
        }
    }

    private static DialogUIManager? GetDialogManager()
    {
        try
        {
            var manager = DialogUIManager.Instance;
            return IsAlive(manager) ? manager : null;
        }
        catch
        {
            return null;
        }
    }

    private static DialogChoiceBubble? FindTemplate(
        Il2CppSystem.Collections.Generic.List<DialogChoiceBubble>? bubbles)
    {
        if (bubbles is null)
            return null;

        for (var i = bubbles.Count - 1; i >= 0; i--)
        {
            try
            {
                var candidate = bubbles[i];
                if (!IsAlive(candidate))
                    continue;

                var candidateObject = candidate!.gameObject;
                if (IsAlive(candidateObject) &&
                    !candidateObject!.name.Equals(EntryObjectName, StringComparison.Ordinal) &&
                    !candidateObject.name.Equals(HeadEntryObjectName, StringComparison.Ordinal) &&
                    !candidateObject.name.Equals(PhoneEntryObjectName, StringComparison.Ordinal))
                    return candidate;
            }
            catch
            {
                // A list can contain a destroyed IL2CPP wrapper during a scene
                // transition. Skip that slot and keep looking for a live template.
            }
        }

        return null;
    }

    private static DialogChoiceBubble? FindPhoneTemplate()
    {
        try
        {
            var manager = DialoguePhoneUIManager.Instance;
            if (!IsAlive(manager))
                return null;

            return FindTemplate(manager!.dialogChoiceBubbles);
        }
        catch
        {
            // The phone UI is created lazily. A missing phone template should only
            // fall back to the store choice style, never interrupt the update loop.
            return null;
        }
    }

    private static bool HasActiveNativeChoice(
        Il2CppSystem.Collections.Generic.List<DialogChoiceBubble>? bubbles)
    {
        if (bubbles is null)
            return false;

        for (var i = bubbles.Count - 1; i >= 0; i--)
        {
            try
            {
                var candidate = bubbles[i];
                if (!IsAlive(candidate))
                    continue;

                var candidateObject = candidate!.gameObject;
                if (!IsAlive(candidateObject))
                    continue;

                var name = candidateObject.name;
                if (!name.Equals(EntryObjectName, StringComparison.Ordinal) &&
                    !name.Equals(HeadEntryObjectName, StringComparison.Ordinal) &&
                    !name.Equals(PhoneEntryObjectName, StringComparison.Ordinal) &&
                    candidateObject.activeInHierarchy)
                    return true;
            }
            catch
            {
                // Ignore a destroyed slot during a scene transition.
            }
        }

        return false;
    }

    private static DialogChoiceBubble? FindSceneTemplate()
    {
        if (IsAlive(_sceneTemplate))
            return _sceneTemplate;

        if (Time.unscaledTime < _nextTemplateSearchTime)
            return null;

        _nextTemplateSearchTime = Time.unscaledTime + 1f;
        try
        {
            // Both native managers create their choice slots lazily. Looking at
            // all live scene objects lets the first visitor receive the entry
            // before either manager has displayed a choice once.
            var candidates = UnityEngine.Object.FindObjectsOfType<DialogChoiceBubble>(true);
            for (var i = candidates.Length - 1; i >= 0; i--)
            {
                var candidate = candidates[i];
                if (!IsAlive(candidate))
                    continue;

                var candidateObject = candidate!.gameObject;
                if (!IsAlive(candidateObject))
                    continue;

                var name = candidateObject.name;
                if (name.Equals(EntryObjectName, StringComparison.Ordinal) ||
                    name.Equals(HeadEntryObjectName, StringComparison.Ordinal) ||
                    name.Equals(PhoneEntryObjectName, StringComparison.Ordinal))
                    continue;

                var parent = candidate.transform.parent;
                if (!IsAlive(parent))
                    continue;

                _sceneTemplate = candidate;
                MelonLogger.Msg($"[当铺世界] 已找到原版对话选项模板：{name}");
                return candidate;
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[当铺世界] 查找原版对话选项模板失败：{ex.Message}");
        }

        return null;
    }

    private static Transform? FindHeadEntryParent(
        DialogChoiceBubble template,
        GameObject? anchor)
    {
        try
        {
            var templateParent = FindLayoutFreeAncestor(template.transform.parent);
            var anchorParent = FindLayoutFreeAncestor(anchor?.transform.parent);

            // Prefer a live UI parent with a Canvas. The fallback anchor can be
            // the customer's world object, whose parent is not a renderable UI
            // layer even though it is active.
            if (IsAlive(templateParent) &&
                templateParent!.gameObject.activeInHierarchy &&
                HasCanvasAncestor(templateParent))
                return templateParent;

            if (IsAlive(anchorParent) &&
                anchorParent!.gameObject.activeInHierarchy &&
                HasCanvasAncestor(anchorParent))
                return anchorParent;

            var root = template.transform.root;
            if (IsAlive(root) &&
                root!.gameObject.activeInHierarchy &&
                HasCanvasAncestor(root))
                return root;

            if (IsAlive(templateParent) && templateParent!.gameObject.activeInHierarchy)
                return templateParent;
            if (IsAlive(anchorParent) && anchorParent!.gameObject.activeInHierarchy)
                return anchorParent;

            if (IsAlive(anchorParent))
                return anchorParent;
            if (IsAlive(templateParent))
                return templateParent;
            return root;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasCanvasAncestor(Transform transform)
    {
        try
        {
            return IsAlive(transform.GetComponentInParent<Canvas>(true));
        }
        catch
        {
            return false;
        }
    }

    private static Transform? FindLayoutFreeAncestor(Transform? start)
    {
        var current = start;
        for (var depth = 0; IsAlive(current) && depth < 8; depth++)
        {
            try
            {
                if (!IsAlive(current!.GetComponent<LayoutGroup>()))
                    return current;
                current = current.parent;
            }
            catch
            {
                // LayoutGroup is an optional component on the native prefab. If
                // an IL2CPP wrapper is temporarily unavailable, keep the live
                // parent instead of dropping the entry altogether.
                return IsAlive(current) ? current : null;
            }
        }

        return IsAlive(current) ? current : null;
    }

    private static GameObject? GetOrCreateHeadEntry(
        DialogChoiceBubble template,
        Transform? parent,
        bool usePhoneStyle)
    {
        if (!IsAlive(template) || !IsAlive(parent))
            return null;

        if (IsAlive(_storeHeadEntry))
        {
            try
            {
                if (_storeHeadUsesPhoneStyle != usePhoneStyle)
                {
                    UnityEngine.Object.Destroy(_storeHeadEntry);
                    _storeHeadEntry = null;
                }
            }
            catch
            {
                _storeHeadEntry = null;
            }
        }

        if (IsAlive(_storeHeadEntry))
        {
            try
            {
                if (_storeHeadEntry!.transform.parent != parent)
                    _storeHeadEntry.transform.SetParent(parent, false);

                var existingBubble = _storeHeadEntry.GetComponent<DialogChoiceBubble>();
                if (IsAlive(existingBubble) && ConfigureHeadEntry(existingBubble!))
                    return _storeHeadEntry;

                UnityEngine.Object.Destroy(_storeHeadEntry);
                _storeHeadEntry = null;
            }
            catch
            {
                _storeHeadEntry = null;
            }
        }

        try
        {
            var templateObject = template.gameObject;
            if (!IsAlive(templateObject))
                return null;

            var clone = UnityEngine.Object.Instantiate(templateObject, parent, false);
            if (!IsAlive(clone))
                return null;

            clone.name = HeadEntryObjectName;
            var bubble = clone.GetComponent<DialogChoiceBubble>();
            if (!IsAlive(bubble) || !ConfigureHeadEntry(bubble!))
            {
                UnityEngine.Object.Destroy(clone);
                return null;
            }

            clone.transform.SetAsLastSibling();
            _storeHeadEntry = clone;
            _storeHeadUsesPhoneStyle = usePhoneStyle;
            return clone;
        }
        catch
        {
            return null;
        }
    }

    private static GameObject? GetStoreHeadAnchor(
        DialogUIManager? manager,
        GameObject? clientGameObject)
    {
        if (!IsAlive(manager))
            return IsAlive(clientGameObject) ? clientGameObject : null;

        try
        {
            var nameBubble = manager!.clientNameBubble;
            if (IsAlive(nameBubble))
                return nameBubble;

            var clientBubble = manager.clientBubble;
            if (IsAlive(clientBubble))
            {
                var clientBubbleObject = clientBubble!.gameObject;
                if (IsAlive(clientBubbleObject))
                    return clientBubbleObject;
            }
        }
        catch
        {
            return IsAlive(clientGameObject) ? clientGameObject : null;
        }

        return IsAlive(clientGameObject) ? clientGameObject : null;
    }

    private static bool ConfigureHeadEntry(DialogChoiceBubble bubble)
    {
        try
        {
            if (!IsAlive(bubble))
                return false;

            var gameObject = bubble!.gameObject;
            if (!IsAlive(gameObject))
                return false;

            gameObject.name = HeadEntryObjectName;
            gameObject.SetActive(true);

            var rect = gameObject.GetComponent<RectTransform>();
            if (IsAlive(rect))
            {
                // The original choice bubbles are laid out at the bottom. A head bubble
                // needs an independent, center-anchored coordinate system.
                rect!.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.localRotation = Quaternion.identity;
                rect.localScale = Vector3.one;
            }

            var button = IsAlive(bubble.button)
                ? bubble.button
                : gameObject.GetComponent<Button>();
            var label = IsAlive(bubble.TMP)
                ? bubble.TMP
                : gameObject.GetComponentInChildren<TextMeshProUGUI>(true);
            if (!IsAlive(button) || !IsAlive(label))
            {
                gameObject.SetActive(false);
                return false;
            }

            label!.text = EntryText;
            label.alignment = TextAlignmentOptions.Center;

            bubble.index = -1;
            button!.onClick.RemoveAllListeners();
            button.onClick.AddListener((UnityAction)(() => OnEntryClicked(NativeDialogScene.Store)));
            bubble.SetInteractable(true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool PositionHeadEntry(
        GameObject entry,
        GameObject? anchor,
        DialogChoiceBubble template)
    {
        if (!IsAlive(entry) || !IsAlive(anchor) || !IsAlive(template))
        {
            if (IsAlive(entry))
                entry!.SetActive(false);
            return false;
        }

        try
        {
            var entryTransform = entry!.transform;
            var anchorTransform = anchor!.transform;
            if (!IsAlive(entryTransform) || !IsAlive(anchorTransform) || !IsAlive(entryTransform.parent))
            {
                entry.SetActive(false);
                return false;
            }

            // Keep the entry under the selected persistent choice/canvas parent.
            // Reparenting it to the name bubble's parent made it inherit a panel
            // that the native dialogue flow hides between lines.
            entryTransform.position = anchorTransform.position +
                                      new Vector3(HeadEntryOffsetX, HeadEntryOffsetY, 0f);
            entryTransform.rotation = anchorTransform.rotation;
            entryTransform.localScale = template!.transform.localScale * HeadEntryScale;
            entry.SetActive(true);

            if (!entry.activeInHierarchy)
            {
                entry.SetActive(false);
                return false;
            }

            var positionLogKey = $"{anchor.GetInstanceID()}:{entry.GetInstanceID()}";
            if (positionLogKey != _lastHeadPositionLogKey)
            {
                _lastHeadPositionLogKey = positionLogKey;
                MelonLogger.Msg(
                    $"[当铺世界] 客人入口跟随原版名称气泡：锚点={anchor.name}，" +
                    $"锚点位置={anchorTransform.position}，入口位置={entryTransform.position}");
            }

            return true;
        }
        catch
        {
            if (IsAlive(entry))
                entry!.SetActive(false);
            return false;
        }
    }

    private static void HideStoreHeadEntry()
    {
        if (IsAlive(_storeHeadEntry))
            _storeHeadEntry!.SetActive(false);
        else
            _storeHeadEntry = null;

        if (_lastHeadState != "hidden")
        {
            _lastHeadState = "hidden";
            MelonLogger.Msg("[当铺世界] 已隐藏普通客人头顶 ...... 入口");
        }
    }

    private static void ResetStoreSession()
    {
        _storeSessionKey = string.Empty;
        _storeHeadUsesPhoneStyle = false;
        _lastHeadPositionLogKey = string.Empty;
        HideStoreHeadEntry();
    }

    private static bool ConfigureEntry(DialogChoiceBubble bubble, NativeDialogScene scene)
    {
        try
        {
            if (!IsAlive(bubble))
                return false;

            var gameObject = bubble!.gameObject;
            if (!IsAlive(gameObject))
                return false;

            gameObject.name = EntryObjectName;
            gameObject.SetActive(true);

            var button = IsAlive(bubble.button)
                ? bubble.button
                : gameObject.GetComponent<Button>();
            var label = IsAlive(bubble.TMP)
                ? bubble.TMP
                : gameObject.GetComponentInChildren<TextMeshProUGUI>(true);
            if (!IsAlive(button) || !IsAlive(label))
            {
                gameObject.SetActive(false);
                return false;
            }

            label!.text = EntryText;
            label.alignment = TextAlignmentOptions.Center;
            bubble.index = -1;
            button!.onClick.RemoveAllListeners();
            button.onClick.AddListener((UnityAction)(() => OnEntryClicked(scene)));
            bubble.SetInteractable(true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void OnEntryClicked(NativeDialogScene scene)
    {
        try
        {
            MelonLogger.Msg($"[当铺世界] 原版{(scene == NativeDialogScene.Phone ? "电话" : "对话")} ...... 入口已点击");
            AIWorldPawnshopMod.Instance?.OpenNativeChat(scene == NativeDialogScene.Phone ? "phone" : "store");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[当铺世界] 原版对话入口响应失败：{ex.Message}");
        }
    }

    private static bool IsAlive(UnityEngine.Object? value)
    {
        try
        {
            return value is not null && value;
        }
        catch
        {
            return false;
        }
    }

}

internal static class NativeDialogBridge
{
    public static void RecordPlayerLine(string scene, string text)
    {
        AppendRecorderLine(scene, $"你：{text.Trim()}");
    }

    public static void RecordClientLine(string scene, string speakerName, string text)
    {
        var name = string.IsNullOrWhiteSpace(speakerName) ? "客人" : speakerName.Trim();
        AppendRecorderLine(scene, $"{name}：{text.Trim()}");
    }

    public static void ShowClientReply(string scene, string speakerName, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            var reply = text.Trim();
            if (scene.Equals("phone", StringComparison.OrdinalIgnoreCase))
            {
                var phoneManager = DialoguePhoneUIManager.Instance;
                if (phoneManager is not null)
                {
                    if (!RenderPhoneReply(phoneManager, speakerName, reply))
                        return;

                    MelonLogger.Msg("[当铺世界] 回复已提交给电话原版气泡");
                    return;
                }

                MelonLogger.Warning("[当铺世界] 电话管理器已不可用，拒绝把电话回复发送到店内气泡");
                return;
            }

            var manager = DialogUIManager.Instance;
            if (manager is not null)
            {
                SetStoreClientName(manager, speakerName);
                EnsureClientBubble(manager.clientBubble, reply);
                MelonLogger.Msg("[当铺世界] 回复已提交给店内原版气泡");
                return;
            }

            MelonLogger.Warning("[当铺世界] 店内对话管理器已不可用，回复未显示");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[当铺世界] 原版客人回复播放失败：{ex.Message}");
        }
    }

    private static void AppendRecorderLine(string scene, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        try
        {
            if (scene.Equals("phone", StringComparison.OrdinalIgnoreCase))
            {
                var phoneManager = DialoguePhoneUIManager.Instance;
                if (phoneManager is not null)
                    phoneManager.UpdateDialogHistory(line);
                else
                    EnqueueRecorderFallback(line);
            }
            else
            {
                var manager = DialogUIManager.Instance;
                if (manager is not null)
                    manager.UpdateDialogHistory(line);
                else
                    EnqueueRecorderFallback(line);
            }

            try
            {
                RecorderUIManager.Instance?.RefreshUI();
            }
            catch
            {
                // The recorder rebuilds itself the next time it opens.
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[当铺世界] 写入原版录音失败，尝试直接追加：{ex.Message}");
            EnqueueRecorderFallback(line);
        }
    }

    private static void EnqueueRecorderFallback(string line)
    {
        try
        {
            var history = PlayerStore.Instance?.dialogHistory;
            if (history is null)
                return;

            history.Enqueue(line);
            while (history.Count > 200)
                history.Dequeue();
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[当铺世界] 原版录音降级写入失败：{ex.Message}");
        }
    }

    private static void EnsureClientBubble(TextBubble? bubble, string text)
    {
        if (bubble is null || bubble.textfield is null)
            return;

        if (!bubble.gameObject.activeSelf)
            bubble.gameObject.SetActive(true);
        if (!bubble.textfield.gameObject.activeSelf)
            bubble.textfield.gameObject.SetActive(true);
        if (!string.Equals(bubble.textfield.text, text, StringComparison.Ordinal))
            bubble.textfield.text = text;
    }

    private static bool RenderPhoneReply(DialoguePhoneUIManager manager, string name, string text)
    {
        var bubble = manager.clientBubble;
        if (bubble is null || bubble.textfield is null)
        {
            MelonLogger.Warning("[当铺世界] 电话专属回复气泡尚未初始化，回复未显示");
            return false;
        }

        SetPhoneClientName(manager, name);
        EnsureClientBubble(bubble, text);
        // The native phone flow can activate the label after the bubble. Write it
        // again so the phone contact remains the visible speaker.
        SetPhoneClientName(manager, name);
        return true;
    }

    private static void SetStoreClientName(DialogUIManager manager, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || manager.clientNameTMP is null)
            return;

        if (manager.clientNameBubble is not null)
            manager.clientNameBubble.SetActive(true);
        manager.clientNameTMP.text = name;
    }

    private static void SetPhoneClientName(DialoguePhoneUIManager manager, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        if (manager.clientNameTMP is not null)
        {
            if (manager.clientNameBubble is not null)
                manager.clientNameBubble.SetActive(true);
            manager.clientNameTMP.text = name;
        }
    }

}
