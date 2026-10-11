using System.Reflection;
using MelonLoader;
using UnityEngine;
using AIWorldPawnshop.Runtime;
using AIWorldPawnshop.UI;
using Il2Cpp;

namespace AIWorldPawnshop;

public sealed class AIWorldPawnshopMod : MelonMod
{
    internal static AIWorldPawnshopMod? Instance { get; private set; }
    internal WorldRuntime Runtime => _runtime ?? throw new InvalidOperationException("Mod runtime is not ready.");
    internal ChatUiController ChatUi => _chatUi ?? throw new InvalidOperationException("Chat UI is not ready.");

    private WorldRuntime? _runtime;
    private WorldUiController? _ui;
    private ChatUiController? _chatUi;
    private ModelSettingsUiController? _modelSettingsUi;
    private MelonPreferences_Entry<bool>? _enabledPreference;

    public override void OnInitializeMelon()
    {
        Instance = this;
        var dataRoot = Path.Combine(Application.persistentDataPath, ModConstants.DataDirectoryName);
        _runtime = new WorldRuntime(dataRoot);
        _ui = new WorldUiController();
        _chatUi = new ChatUiController();
        _modelSettingsUi = new ModelSettingsUiController();
        _enabledPreference = MelonPreferences.CreateCategory(ModConstants.ModId)
            .CreateEntry("Enabled", true, "启用当铺世界");

        MelonLogger.Msg($"[{ModConstants.DisplayName}] 已加载，目标版本 {ModConstants.TargetGameVersion}");
        MelonLogger.Msg($"[{ModConstants.DisplayName}] 程序集：{Assembly.GetExecutingAssembly().GetName().Version}");
        MelonLogger.Msg($"[{ModConstants.DisplayName}] 数据目录：{dataRoot}");
        MelonLogger.Msg(
            $"[{ModConstants.DisplayName}] 模型接口：{_runtime.Config.Provider} / {_runtime.Config.BaseUrl} / " +
            $"{_runtime.Config.Model}，联网请求：{_runtime.Config.AllowNetwork}");
    }

    public override void OnUpdate()
    {
        if (_runtime is null || _enabledPreference is null || !_enabledPreference.Value)
            return;

        NativeDialogEntry.Update();
        NativeBarterAdapter.Update();
        _modelSettingsUi?.Update(_runtime);
        _ui?.Update(_runtime);
        _chatUi?.Update(_runtime);
    }

    public override void OnApplicationQuit()
    {
        try
        {
            _runtime?.Save();
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[{ModConstants.DisplayName}] 退出保存失败：{ex}");
        }
        Instance = null;
    }

    internal void OnNativeDayEnded() => _runtime?.OnNativeDayEnded();
    internal void OnNativeSave() => _runtime?.Save();
    internal string PrepareNativeClient(StoreClient? client) =>
        _runtime?.PrepareNativeClient(client) ?? string.Empty;
    internal void CompleteNativeClientCreation(
        string requestKey,
        StoreClient? client,
        StoreClientInstance? instance) =>
        _runtime?.CompleteNativeClientCreation(requestKey, client, instance);
    internal void OnNativeItemBought(GameItem? item, int cost) =>
        _runtime?.OnNativeItemBought(item, cost);
    internal void PrepareNativeItemBought(GameItem? item) =>
        _runtime?.PrepareNativeItemBought(item);
    internal void OnNativeClientDismissed(StoreClient? client) =>
        _runtime?.OnNativeClientDismissed(client);
    internal void OpenWorldPage()
    {
        if (_runtime is not null && _ui is not null)
            _ui.Toggle(_runtime);
    }

    internal void OpenNativeChat(string scene)
    {
        if (_runtime is null || _chatUi is null)
            return;

        _chatUi.OpenFromBubble(_runtime, scene);
    }
}
