using AIWorldPawnshop.Models;
using Il2Cpp;
using MelonLoader;

namespace AIWorldPawnshop.Runtime;

internal static class NativeConversationExitAdapter
{
    public static bool Apply(ConversationRequest request, ConversationResponse response)
    {
        try
        {
            if (ConversationActionResolver.ShouldDismissStoreClient(request, response))
            {
                DialogUIManager.Instance?.EndConversation();
                PlayerStore.Instance?.DismissCurrentClient(true, true, true);
                MelonLogger.Msg("[当铺世界] 顾客在自由交谈中明确离店，已调用原版离店流程");
                return true;
            }

            if (ConversationActionResolver.ShouldHangUpPhone(request, response))
            {
                DialoguePhoneUIManager.Instance?.EndConversation();
                PhoneUIManager.Instance?.StopCall();
                MelonLogger.Msg("[当铺世界] 联系人在自由通话中明确挂断，已调用原版挂断流程");
                return true;
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[当铺世界] 自动结束对话失败：{ex.Message}");
        }

        return false;
    }
}
