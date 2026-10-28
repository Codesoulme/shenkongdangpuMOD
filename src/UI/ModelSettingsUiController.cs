using System.Globalization;
using AIWorldPawnshop.Model;
using AIWorldPawnshop.Models;
using AIWorldPawnshop.Runtime;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIWorldPawnshop.UI;

public sealed class ModelSettingsUiController
{
    private const string WindowId = "AIWorldPawnshop.ModelSettings";
    private const string StatusId = "status";
    private const string NetworkButtonId = "network";
    private const string ProtocolButtonId = "protocol";

    private CustomUIWindow? _window;
    private WorldModConfig? _draft;
    private int _profileIndex;
    private string _timeoutText = string.Empty;
    private string _maxTokensText = string.Empty;
    private string _status = "尚未检测连接";
    private Task<ModelConnectionResult>? _testTask;
    private CancellationTokenSource? _testCancellation;
    private bool _loggedFailure;

    public void Update(WorldRuntime runtime)
    {
        PollConnectionTest();

        if (Input.GetKeyDown(KeyCode.F10))
        {
            try
            {
                Toggle(runtime);
            }
            catch (Exception ex)
            {
                if (!_loggedFailure)
                {
                    MelonLogger.Error($"[当铺世界] 模型设置窗口创建失败：{ex}");
                    _loggedFailure = true;
                }
            }
        }
    }

    public void Toggle(WorldRuntime runtime)
    {
        if (IsOpen)
        {
            Close();
            return;
        }

        Open(runtime);
    }

    public void Open(WorldRuntime runtime)
    {
        _draft = runtime.Config.Clone();
        _profileIndex = FindActiveProfileIndex(_draft);
        _status = "尚未检测连接";
        BuildWindow(runtime);
        MelonLogger.Msg("[当铺世界] 模型设置页面已打开");
    }

    public void Close()
    {
        _testCancellation?.Cancel();
        _testTask = null;
        _testCancellation?.Dispose();
        _testCancellation = null;
        _window?.Close();
        _window = null;
        _draft = null;
    }

    private bool IsOpen => _window is not null && _window.IsAlive && _window.IsVisible;

    private void BuildWindow(WorldRuntime runtime)
    {
        if (_window is not null && _window.IsAlive)
            _window.Close();

        var profile = CurrentProfile;
        _timeoutText = profile.TimeoutMs.ToString(CultureInfo.InvariantCulture);
        _maxTokensText = profile.MaxOutputTokens.ToString(CultureInfo.InvariantCulture);

        var builder = CustomUIManager.Window(WindowId, "模型接口设置");
        builder
            .SetSize(780f, 650f)
            .Center()
            .SetCloseOnEscape()
            .SetDraggable(true);

        builder.BeginColumn(10f);
        builder.AddLabel("模型接口设置", "title");
        builder.AddLabel(BuildProfileSummary(profile), "profile_summary");
        builder.BeginRow(10f);
        builder.AddButton("上一个接口", new Action(() => SwitchProfile(runtime, -1)), "previous");
        builder.AddButton("下一个接口", new Action(() => SwitchProfile(runtime, 1)), "next");
        builder.AddButton("复制为新接口", new Action(() => DuplicateProfile(runtime)), "duplicate");
        builder.AddButton("删除当前接口", new Action(() => DeleteProfile(runtime)), "delete");
        builder.End();

        builder.BeginScrollFlexible(150f);
        builder.AddLabel("接口名称", "name_label");
        builder.AddInput("例如：本地模型 / 云端模型", profile.Name,
            new Action<string>(value => profile.Name = value), "name");
        builder.AddLabel("基础地址（填到 /v1，或填完整 chat/completions 地址）", "base_url_label");
        builder.AddInput("https://example.com/v1", profile.BaseUrl,
            new Action<string>(value => profile.BaseUrl = value), "base_url");
        builder.AddLabel("模型名称", "model_label");
        builder.AddInput("例如：gpt-4o-mini", profile.Model,
            new Action<string>(value => profile.Model = value), "model");
        builder.AddButton(BuildProtocolButtonText(profile), new Action(CycleProtocol), ProtocolButtonId);
        builder.AddLabel(
            "自动模式会优先为 gpt-5 / o 系列选择 Responses，失败时再尝试 Chat Completions。",
            "protocol_hint");
        builder.AddLabel("API 密钥（可留空，优先读取下面的环境变量）", "api_key_label");
        builder.AddInput("sk-...", profile.ApiKey,
            new Action<string>(value => profile.ApiKey = value), "api_key");
        builder.AddLabel("密钥环境变量名（直填密钥时可留空）", "api_key_env_label");
        builder.AddInput("PAWN_SHOP_MODEL_KEY", profile.ApiKeyEnv,
            new Action<string>(value => profile.ApiKeyEnv = value), "api_key_env");
        builder.AddLabel("请求超时（毫秒）", "timeout_label");
        builder.AddInput("60000", _timeoutText,
            new Action<string>(value => _timeoutText = value), "timeout");
        builder.AddLabel("最大回复长度", "max_tokens_label");
        builder.AddInput("500", _maxTokensText,
            new Action<string>(value => _maxTokensText = value), "max_tokens");
        builder.End();

        builder.AddButton(BuildNetworkButtonText(profile), new Action(() => ToggleNetwork()), NetworkButtonId);
        builder.AddLabel(_status, StatusId);
        builder.BeginRow(10f);
        builder.AddButton("检测连接", new Action(() => StartConnectionTest(runtime)), "test");
        builder.AddButton("保存并使用", new Action(() => SaveAndApply(runtime)), "save");
        builder.AddButton("恢复当前接口默认值", new Action(() => ResetProfile(runtime)), "reset");
        builder.AddButton("关闭", new Action(Close), "close");
        builder.End();
        builder.AddLabel(
            "保存后立即使用当前接口。接口不可用时会自动回到本地预置回复。",
            "hint");
        builder.End();

        _window = builder.Show();
        LocalizedInputFontFix.Apply(
            _window,
            "name_label",
            "name",
            "base_url",
            "model",
            "api_key",
            "api_key_env",
            "timeout",
            "max_tokens");
        SizeControls();
    }

    private void SwitchProfile(WorldRuntime runtime, int direction)
    {
        if (!CommitDraft())
            return;

        var count = _draft?.Profiles.Count ?? 0;
        if (count == 0)
            return;

        _profileIndex = (_profileIndex + direction) % count;
        if (_profileIndex < 0)
            _profileIndex += count;
        _draft!.ActiveProfileId = _draft.Profiles[_profileIndex].Id;
        _status = "已切换编辑档位，保存后才会应用";
        BuildWindow(runtime);
    }

    private void DuplicateProfile(WorldRuntime runtime)
    {
        if (!CommitDraft() || _draft is null)
            return;

        var source = CurrentProfile;
        var copy = source.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = $"{source.Name} 副本";
        _draft.Profiles.Add(copy);
        _profileIndex = _draft.Profiles.Count - 1;
        _draft.ActiveProfileId = copy.Id;
        _status = "已创建新接口档位，填写后保存即可使用";
        BuildWindow(runtime);
    }

    private void DeleteProfile(WorldRuntime runtime)
    {
        if (_draft is null || _draft.Profiles.Count <= 1)
        {
            SetStatus("至少保留一个接口档位");
            return;
        }

        if (!CommitDraft())
            return;

        _draft.Profiles.RemoveAt(_profileIndex);
        _profileIndex = Math.Clamp(_profileIndex, 0, _draft.Profiles.Count - 1);
        _draft.ActiveProfileId = _draft.Profiles[_profileIndex].Id;
        _status = "已删除当前接口档位，保存后才会写入配置";
        BuildWindow(runtime);
    }

    private void ToggleNetwork()
    {
        var profile = CurrentProfile;
        profile.AllowNetwork = !profile.AllowNetwork;
        _window?.Get(NetworkButtonId)?.SetText(BuildNetworkButtonText(profile));
        SetStatus(profile.AllowNetwork ? "联网请求已打开" : "联网请求已关闭，将使用本地回复");
    }

    private void CycleProtocol()
    {
        var profile = CurrentProfile;
        profile.Protocol = profile.Protocol switch
        {
            "auto" => "responses",
            "responses" => "chat_completions",
            _ => "auto"
        };
        _window?.Get(ProtocolButtonId)?.SetText(BuildProtocolButtonText(profile));
        SetStatus($"协议已切换为 {BuildProtocolButtonText(profile)}");
    }

    private void StartConnectionTest(WorldRuntime runtime)
    {
        if (!CommitDraft())
            return;

        var candidate = CurrentProfile.Clone();
        candidate.Normalize();
        _testCancellation?.Cancel();
        _testCancellation?.Dispose();
        _testCancellation = new CancellationTokenSource();
        _testTask = runtime.TestConnectionAsync(candidate, _testCancellation.Token);
        MelonLogger.Msg(
            $"[当铺世界] 开始检测模型接口：{candidate.Name} / {candidate.BaseUrl} / {candidate.Model}");
        SetStatus("正在检测连接，请稍候...");
    }

    private void PollConnectionTest()
    {
        if (_testTask is null || !_testTask.IsCompleted)
            return;

        var task = _testTask;
        _testTask = null;
        try
        {
            var result = task.GetAwaiter().GetResult();
            SetStatus(result.Succeeded ? $"成功：{result.Message}" : $"失败：{result.Message}");
            MelonLogger.Msg($"[当铺世界] 模型接口检测结果：{result.Message}");
        }
        catch (Exception ex)
        {
            SetStatus($"失败：{ex.Message}");
            MelonLogger.Error($"[当铺世界] 模型接口检测异常：{ex.Message}");
        }
        finally
        {
            _testCancellation?.Dispose();
            _testCancellation = null;
        }
    }

    private void SaveAndApply(WorldRuntime runtime)
    {
        if (!CommitDraft() || _draft is null)
            return;

        _draft.Normalize();
        _draft.ActiveProfileId = CurrentProfile.Id;
        runtime.ApplyConfig(_draft);
        _draft = runtime.Config.Clone();
        _profileIndex = FindActiveProfileIndex(_draft);
        _status = "已保存并立即使用当前接口";
        BuildWindow(runtime);
        SetStatus(_status);
    }

    private void ResetProfile(WorldRuntime runtime)
    {
        var profile = CurrentProfile;
        var reset = new ModelProfile
        {
            Id = profile.Id,
            Name = profile.Name
        };
        _draft!.Profiles[_profileIndex] = reset;
        _status = "当前接口已恢复默认值，保存后才会应用";
        BuildWindow(runtime);
    }

    private bool CommitDraft()
    {
        if (_draft is null)
            return false;

        if (!int.TryParse(_timeoutText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout))
        {
            SetStatus("请求超时必须是整数");
            return false;
        }

        if (!int.TryParse(_maxTokensText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxTokens))
        {
            SetStatus("最大回复长度必须是整数");
            return false;
        }

        var profile = CurrentProfile;
        profile.TimeoutMs = Math.Clamp(timeout, 1000, 120000);
        profile.MaxOutputTokens = Math.Clamp(maxTokens, 32, 8000);
        profile.Normalize();
        return true;
    }

    private void SetStatus(string status)
    {
        _status = status.Length > 180 ? status[..177] + "..." : status;
        if (_window is not null && _window.IsAlive)
        {
            _window.Get(StatusId)?.SetText(_status);
            SizeStatus();
        }
    }

    private void SizeControls()
    {
        SetControlHeight("title", 38f);
        SetControlHeight("profile_summary", 34f);
        foreach (var id in new[] { "previous", "next", "duplicate", "delete", "test", "save", "reset", "close", NetworkButtonId })
            SetControlHeight(id, 48f, button: true);

        foreach (var id in new[] { "name_label", "base_url_label", "model_label", "api_key_label", "api_key_env_label", "timeout_label", "max_tokens_label" })
            SetControlHeight(id, 30f);
        foreach (var id in new[] { "name", "base_url", "model", "api_key", "api_key_env", "timeout", "max_tokens" })
            SetControlHeight(id, 42f);
        SetControlHeight(ProtocolButtonId, 48f, button: true);
        SetControlHeight("protocol_hint", 52f);
        SetControlHeight("hint", 44f);
        SizeStatus();
    }

    private void SizeStatus()
    {
        var text = _window?.Get(StatusId)?.text;
        var width = Math.Max(280f, (_window?.Rect.rect.width ?? 780f) - 70f);
        var height = text is null
            ? 34f
            : Mathf.Clamp(text.GetPreferredValues(_status, width, 0f).y + 12f, 34f, 100f);
        SetControlHeight(StatusId, height);
    }

    private void SetControlHeight(string id, float height, bool button = false)
    {
        var element = _window?.Get(id);
        var rect = element?.Rect;
        if (rect is null)
            return;

        var layout = rect.GetComponent<LayoutElement>() ?? rect.gameObject.AddComponent<LayoutElement>();
        layout.minHeight = height;
        layout.preferredHeight = height;
        if (button)
        {
            layout.flexibleWidth = 1f;
            if (element?.text is not null)
            {
                element.text.enableAutoSizing = true;
                element.text.fontSizeMin = 14f;
                element.text.fontSizeMax = 19f;
                element.text.enableWordWrapping = false;
                element.text.overflowMode = TextOverflowModes.Ellipsis;
                element.text.alignment = TextAlignmentOptions.Center;
                var textRect = element.text.rectTransform;
                if (textRect != rect && textRect.parent == rect)
                {
                    textRect.anchorMin = Vector2.zero;
                    textRect.anchorMax = Vector2.one;
                    textRect.offsetMin = new Vector2(6f, 4f);
                    textRect.offsetMax = new Vector2(-6f, -4f);
                }
            }
        }
        LayoutRebuilder.MarkLayoutForRebuild(rect.parent as RectTransform);
    }

    private ModelProfile CurrentProfile
    {
        get
        {
            if (_draft is null)
                throw new InvalidOperationException("模型设置尚未打开");

            _draft.Normalize();
            _profileIndex = Math.Clamp(_profileIndex, 0, _draft.Profiles.Count - 1);
            return _draft.Profiles[_profileIndex];
        }
    }

    private string BuildProfileSummary(ModelProfile profile)
    {
        var count = _draft?.Profiles.Count ?? 0;
        var name = profile.Name.Length > 40 ? profile.Name[..37] + "..." : profile.Name;
        return $"接口档位 {_profileIndex + 1}/{count}：{name}";
    }

    private static string BuildNetworkButtonText(ModelProfile profile) =>
        profile.AllowNetwork ? "联网请求：开启" : "联网请求：关闭";

    private static string BuildProtocolButtonText(ModelProfile profile) =>
        profile.Protocol switch
        {
            "responses" => "协议：Responses",
            "chat_completions" => "协议：Chat Completions",
            _ => "协议：自动"
        };

    private static int FindActiveProfileIndex(WorldModConfig config)
    {
        var index = config.Profiles.FindIndex(profile =>
            profile.Id.Equals(config.ActiveProfileId, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : 0;
    }
}
