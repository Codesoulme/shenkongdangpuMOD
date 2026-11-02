using System.Reflection;
using AIWorldPawnshop.Runtime;
using AIWorldPawnshop.UI;
using UnityEngine;

namespace AIWorldPawnshop;

/// <summary>
/// Native Probably Stolen ModLoader entry point. The game owns this
/// lifecycle; a small Unity driver supplies the per-frame callback that the
/// old Demo MelonLoader entry used to provide.
/// </summary>
public sealed class AIWorldPawnshopMod : IMod
{
    internal static AIWorldPawnshopMod? Instance { get; private set; }
    internal WorldRuntime Runtime => _runtime ?? throw new InvalidOperationException("Mod runtime is not ready.");
    internal ChatUiController ChatUi => _chatUi ?? throw new InvalidOperationException("Chat UI is not ready.");

    private WorldRuntime? _runtime;
    private WorldUiController? _ui;
    private ChatUiController? _chatUi;
    private ModelSettingsUiController? _modelSettingsUi;
    private ModDriver? _driver;
    private bool _enabled = true;

    public void Init(ModManifest manifest)
    {
        Instance = this;
        ModLog.Info($"[{ModConstants.DisplayName}] 初始化正式版 ModLoader，清单 {manifest.ModVersion}");
    }

    public void OnEnable()
    {
        if (_runtime is not null)
            return;

        _enabled = true;
        Instance = this;
        var dataRoot = Path.Combine(Application.persistentDataPath, ModConstants.DataDirectoryName);
        _runtime = new WorldRuntime(dataRoot, Path.GetDirectoryName(Application.dataPath));
        _ui = new WorldUiController();
        _chatUi = new ChatUiController();
        _modelSettingsUi = new ModelSettingsUiController();
        _driver = ModDriver.Attach(this);

        ModLog.Info($"[{ModConstants.DisplayName}] 已加载，目标版本 {ModConstants.TargetGameVersion}");
        ModLog.Info($"[{ModConstants.DisplayName}] 程序集：{Assembly.GetExecutingAssembly().GetName().Version}");
        ModLog.Info($"[{ModConstants.DisplayName}] 数据目录：{dataRoot}");
        ModLog.Info(
            $"[{ModConstants.DisplayName}] 模型接口：{_runtime.Config.Provider} / {_runtime.Config.BaseUrl} / " +
            $"{_runtime.Config.Model}，联网请求：{_runtime.Config.AllowNetwork}");
    }

    public void OnDisable()
    {
        _enabled = false;
        try
        {
            _runtime?.Save();
        }
        catch (Exception ex)
        {
            ModLog.Error($"[{ModConstants.DisplayName}] 退出保存失败：{ex}");
        }

        if (_driver is not null)
            UnityEngine.Object.Destroy(_driver.gameObject);
        _driver = null;
        _modelSettingsUi?.Close();
        _chatUi?.Close();
        _modelSettingsUi = null;
        _chatUi = null;
        _ui = null;
        _runtime = null;
        Instance = null;
    }

    internal void Tick()
    {
        if (!_enabled || _runtime is null)
            return;

        NativeDialogEntry.Update();
        _runtime.TickGameKnowledge();
        NativeBarterAdapter.Update();
        _modelSettingsUi?.Update(_runtime);
        _ui?.Update(_runtime);
        _chatUi?.Update(_runtime);
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
    internal void OnNativeClientDismissed(StoreClient? client)
    {
        _chatUi?.CloseIfScene("store");
        _runtime?.OnNativeClientDismissed(client);
    }
    internal void OnNativePhoneEnded() => _chatUi?.CloseIfScene("phone");
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

internal sealed class ModDriver : MonoBehaviour
{
    private AIWorldPawnshopMod? _mod;

    internal static ModDriver Attach(AIWorldPawnshopMod mod)
    {
        var host = new GameObject("FreeChatBargainBarter.Driver");
        UnityEngine.Object.DontDestroyOnLoad(host);
        var driver = host.AddComponent<ModDriver>();
        driver._mod = mod;
        return driver;
    }

    private void Update() => _mod?.Tick();

    private void OnApplicationQuit()
    {
        try
        {
            _mod?.OnDisable();
        }
        catch (Exception ex)
        {
            ModLog.Error($"[{ModConstants.DisplayName}] 应用退出处理失败：{ex}");
        }
    }
}
