using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;

namespace AIWorldPawnshop.UI;

internal static class LocalizedInputFontFix
{
    public static void Apply(
        CustomUIWindow window,
        string sourceTextId,
        params string[] inputIds)
    {
        try
        {
            var localizedFont = window.Get(sourceTextId)?.text?.font;
            if (localizedFont is null)
                return;

            foreach (var inputId in inputIds)
            {
                var input = window.Get(inputId)?.input;
                if (input is null)
                    continue;

                input.fontAsset = localizedFont;
                if (input.textComponent is not null)
                    input.textComponent.font = localizedFont;

                var placeholderText = input.placeholder?.gameObject
                    ?.GetComponent<TextMeshProUGUI>();
                if (placeholderText is not null)
                    placeholderText.font = localizedFont;

                input.ForceLabelUpdate();
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[当铺世界] 输入框中文字体修复失败：{ex.Message}");
        }
    }
}
