using System.Diagnostics;
using AIWorldPawnshop.Models;
using AIWorldPawnshop.Runtime;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace AIWorldPawnshop.UI;

public sealed class ChatUiController
{
    private const string WindowId = "AIWorldPawnshop.ChatInput";
    private CustomUIWindow? _window;
    private string _pendingText = string.Empty;
    private string _characterKey = string.Empty;
    private string _characterName = string.Empty;
    private string _conversationId = string.Empty;
    private int _turnId;
    private int _offTopicStreak;
    private readonly List<string> _history = new();
    private Task<ConversationResponse>? _pendingReply;
    private ConversationRequest? _pendingRequest;
    private int _requestGeneration;
    private long _requestStartedTimestamp;
    private Action<string>? _replyPresenter;
    private bool _loggedFailure;

    public bool IsOpen => _window is not null && _window.IsAlive && _window.IsVisible;

    public void Update(WorldRuntime runtime)
    {
        PollReply(runtime);
    }

    public void OpenFromBubble(WorldRuntime runtime, string scene = "store")
    {
        if (IsOpen)
            return;

        try
        {
            var speaker = GetCurrentSpeaker(scene);
            if (speaker is null)
            {
                MelonLogger.Msg("[当铺世界] 当前没有可对话的对象");
                return;
            }

            _characterKey = speaker.Value.Key;
            _characterName = speaker.Value.Name;
            _conversationId = Guid.NewGuid().ToString("N");
            _turnId = 0;
            _offTopicStreak = 0;
            _scene = scene;
            _history.Clear();
            var speakerName = _characterName;
            var speakerScene = scene;
            _replyPresenter = reply =>
                NativeDialogBridge.ShowClientReply(speakerScene, speakerName, reply);

            var builder = CustomUIManager.Window(WindowId, $"与 {_characterName} 交谈");
            builder.SetSize(560f, 250f).Center().SetCloseOnEscape().SetDraggable(true);
            builder.BeginColumn(8f);
            builder.AddLabel(
                $"当前{(scene.Equals("phone", StringComparison.OrdinalIgnoreCase) ? "联系人" : "客人")}：{_characterName}",
                "header");
            builder.AddInput("输入一句话", string.Empty,
                new Action<string>(text => _pendingText = text), "input");
            builder.AddButton("发送", new Action(() => Send(runtime)), "send");
            builder.AddButton("查看关系", new Action(() => AIWorldPawnshopMod.Instance?.OpenWorldPage()), "relations");
            builder.AddButton("取消", new Action(Close), "close");
            builder.End();
            _window = builder.Show();
            LocalizedInputFontFix.Apply(_window, "header", "input");
            MelonLogger.Msg("[当铺世界] 已打开原版对话入口的输入框");
        }
        catch (Exception ex)
        {
            if (!_loggedFailure)
            {
                MelonLogger.Error($"[当铺世界] 对话输入框创建失败：{ex}");
                _loggedFailure = true;
            }
        }
    }

    public void Close()
    {
        _requestGeneration++;
        _pendingReply = null;
        _pendingRequest = null;
        _requestStartedTimestamp = 0;
        _replyPresenter = null;
        var window = _window;
        _window = null;
        window?.Close();
    }

    public void CloseIfScene(string scene)
    {
        if (!_scene.Equals(scene, StringComparison.OrdinalIgnoreCase))
            return;
        try { Close(); }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[当铺世界] 关闭已结束对话的输入框失败：{ex.Message}");
        }
    }

    private void Send(WorldRuntime runtime)
    {
        if (!IsOpen || _pendingReply is not null)
            return;

        var playerText = _pendingText.Trim();
        if (string.IsNullOrWhiteSpace(playerText))
            return;

        var currentSpeaker = GetCurrentSpeaker(_scene);
        if (currentSpeaker is null ||
            !currentSpeaker.Value.Key.Equals(_characterKey, StringComparison.OrdinalIgnoreCase))
        {
            MelonLogger.Warning("[当铺世界] 对话对象已经改变，请重新点击 ...... 入口");
            Close();
            return;
        }

        _pendingText = string.Empty;
        _window?.Get("input")?.SetString(string.Empty);

        _requestStartedTimestamp = Stopwatch.GetTimestamp();
        var contextStarted = Stopwatch.GetTimestamp();
        var gameContext = runtime.BuildConversationContext(
            _scene,
            _characterKey,
            _characterName);
        runtime.EnrichConversationContext(gameContext, playerText);
        runtime.LogPerformance(
            $"stage=context_build elapsed_ms={ElapsedMilliseconds(contextStarted)}");
        var contextCharacter = gameContext.Character;
        var authoritativeKey = string.IsNullOrWhiteSpace(contextCharacter.Key)
            ? _characterKey
            : contextCharacter.Key;
        var authoritativeName = string.IsNullOrWhiteSpace(contextCharacter.DisplayName)
            ? _characterName
            : contextCharacter.DisplayName;

        var request = new ConversationRequest
        {
            ConversationId = _conversationId,
            TurnId = ++_turnId,
            CharacterKey = authoritativeKey,
            CharacterDisplayName = authoritativeName,
            FactionKey = contextCharacter.FactionKey,
            Scene = _scene,
            PlayerText = playerText,
            OffTopicStreak = _offTopicStreak,
            RecentHistory = _history.TakeLast(8).ToList(),
            GameContext = gameContext
        };

        _pendingRequest = request;
        _pendingReply = runtime.ReplyAsync(request);
        _history.Add($"你：{playerText}");
        NativeDialogBridge.RecordPlayerLine(_scene, playerText);
        _window?.Get("send")?.SetText("客人思考中...");
        MelonLogger.Msg(
            $"[当铺世界] 已提交{_scene}对话请求：角色 {authoritativeKey}，第 {_turnId} 轮，" +
            $"原版交易上下文：{gameContext.Trade.Phase}");
    }

    private void PollReply(WorldRuntime runtime)
    {
        if (_pendingReply is null || !_pendingReply.IsCompleted)
            return;

        var task = _pendingReply;
        _pendingReply = null;
        runtime.LogPerformance(
            $"stage=ui_task_observed total_ms={ElapsedMilliseconds(_requestStartedTimestamp)}");
        try
        {
            var completedRequest = _pendingRequest;
            var currentSpeaker = GetCurrentSpeaker(_scene);
            var speakerStillPresent = IsOpen && currentSpeaker is not null &&
                                      completedRequest is not null &&
                                      currentSpeaker.Value.Key.Equals(
                                          completedRequest.CharacterKey,
                                          StringComparison.OrdinalIgnoreCase);
            if (!speakerStillPresent)
            {
                _ = task.Exception;
                MelonLogger.Warning("[当铺世界] 回复到达时对话对象已经改变，已丢弃迟到回复");
                Close();
                return;
            }

            var alreadyRecorded = false;
            var response = task.IsCanceled || task.IsFaulted
                ? (_pendingRequest is null
                    ? new ConversationResponse
                    {
                        NpcReply = "客人暂时没有回应，还是先谈眼前的交易吧。",
                        Topic = "trade"
                    }
                    : runtime.Reply(_pendingRequest))
                : task.Result;
            alreadyRecorded = (task.IsCanceled || task.IsFaulted) && _pendingRequest is not null;
            if (completedRequest is not null)
            {
                if (!alreadyRecorded)
                    runtime.RecordConversation(completedRequest, response);
            }
            _pendingRequest = null;

            _offTopicStreak = response.Topic == "off_topic" ? _offTopicStreak + 1 : 0;
            var tradeResult = speakerStillPresent && completedRequest is not null
                ? ApplyLiveActions(runtime, completedRequest, response)
                : new NativeTradeApplication();
            var replyHandledByOriginalBargain = tradeResult.Invoked;
            if (speakerStillPresent && completedRequest is not null && !replyHandledByOriginalBargain)
                ConversationActionResolver.EnsureNaturalExitReply(completedRequest, response);
            var historyReply = replyHandledByOriginalBargain
                ? tradeResult.Message
                : response.NpcReply;
            _history.Add($"{_characterName}：{historyReply}");

            if (replyHandledByOriginalBargain)
            {
                MelonLogger.Msg(
                    $"[当铺世界] 原版议价对话已接管回复：{tradeResult.Message}");
            }
            else if (_replyPresenter is null)
            {
                MelonLogger.Warning("[当铺世界] 对话结果已取得，但没有原版回复显示器");
            }
            else
            {
                NativeDialogBridge.RecordClientLine(_scene, _characterName, response.NpcReply);
                _replyPresenter.Invoke(response.NpcReply);
                MelonLogger.Msg($"[当铺世界] 对话结果已交给原版气泡：{_scene}");
            }

            if (speakerStillPresent && !replyHandledByOriginalBargain && completedRequest is not null &&
                runtime.ApplyNativeConversationExit(completedRequest, response))
            {
                Close();
            }

            if (tradeResult.Proposed && !tradeResult.Invoked)
                MelonLogger.Msg($"[当铺世界] 对话议价未执行：{tradeResult.Message}");
            _window?.Get("send")?.SetText("发送");
            runtime.LogPerformance(
                $"stage=ui_presented total_ms={ElapsedMilliseconds(_requestStartedTimestamp)}");
            _requestStartedTimestamp = 0;
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[当铺世界] 对话结果处理失败：{ex.Message}");
            try
            {
                _replyPresenter?.Invoke("客人暂时没有回应，还是先谈眼前的交易吧。");
            }
            catch (Exception presenterException)
            {
                MelonLogger.Error($"[当铺世界] 降级回复显示失败：{presenterException.Message}");
            }
            _pendingRequest = null;
            _window?.Get("send")?.SetText("发送");
            _requestStartedTimestamp = 0;
        }
    }

    private static long ElapsedMilliseconds(long startedTimestamp) =>
        startedTimestamp <= 0
            ? 0
            : (long)((Stopwatch.GetTimestamp() - startedTimestamp) * 1000d / Stopwatch.Frequency);

    private static NativeTradeApplication ApplyLiveActions(
        WorldRuntime runtime,
        ConversationRequest request,
        ConversationResponse response)
    {
        var nativeActionsApplied = runtime.ApplyNativeConversationActions(request, response);
        if (nativeActionsApplied && response.BarterProposal.Explicit)
        {
            // Opening a barter offer does not itself emit the model's reply;
            // the native game only speaks when an item is placed. Keep the
            // conversation reply in the normal presenter path immediately.
            return new NativeTradeApplication();
        }

        if (nativeActionsApplied && response.CarriedInventoryProposal.Explicit)
        {
            // Revealing a carried item changes the native counter but does not
            // consume the character's reply; keep it on the normal presenter.
            return new NativeTradeApplication();
        }

        if (nativeActionsApplied)
        {
            return new NativeTradeApplication
            {
                Proposed = true,
                Invoked = true,
                Kind = "barter",
                Scope = "named_items",
                Message = response.NpcReply
            };
        }
        return runtime.ApplyNativeTrade(request, response);
    }

    private string _scene = "store";

    private readonly record struct ConversationSpeaker(string Key, string Name);

    private static ConversationSpeaker? GetCurrentSpeaker(string scene)
    {
        try
        {
            if (scene.Equals("phone", StringComparison.OrdinalIgnoreCase))
            {
                var phone = PhoneUIManager.Instance?.currentPhoneClient;
                if (phone is not null)
                {
                    var phoneManager = PhoneUIManager.Instance;
                    var localizedName = phone.GetLocalizedDisplayName();
                    var phoneName = string.IsNullOrWhiteSpace(localizedName)
                        ? $"电话 {phoneManager?.currentNumber}"
                        : localizedName;
                    var phoneKey = string.IsNullOrWhiteSpace(phone.dialogFuncId)
                        ? $"phone:{phoneManager?.currentNumber}"
                        : $"phone:{phone.dialogFuncId}";
                    return new ConversationSpeaker(phoneKey, phoneName);
                }

                var phoneDialogName = DialoguePhoneUIManager.Instance?.clientNameTMP?.text?.Trim();
                if (!string.IsNullOrWhiteSpace(phoneDialogName))
                    return new ConversationSpeaker($"phone-dialog:{phoneDialogName}", phoneDialogName);

                // A phone chat must never silently become a chat with the shop visitor.
                return null;
            }

            var instance = PlayerStore.Instance?.currentClientInstance;
            var client = instance?.GetClientBlueprint() ?? instance?.storeClient;
            if (client is not null)
            {
                var key = string.IsNullOrWhiteSpace(client.identifier)
                    ? client.displayName
                    : client.identifier;
                var name = string.IsNullOrWhiteSpace(client.realName)
                    ? client.displayName
                    : client.realName;
                return new ConversationSpeaker(key, name);
            }

            var dialogName = DialogUIManager.Instance?.clientNameTMP?.text?.Trim();
            if (!string.IsNullOrWhiteSpace(dialogName))
                return new ConversationSpeaker($"dialog:{dialogName}", dialogName);

            return null;
        }
        catch
        {
            return null;
        }
    }
}
