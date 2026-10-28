using System.Reflection;
using AIWorldPawnshop.Models;

var normalize = typeof(ConversationRequest).Assembly
    .GetType("AIWorldPawnshop.Runtime.ConversationActionResolver")!
    .GetMethod("Normalize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;

var request = new ConversationRequest
{
    Scene = "store",
    PlayerText = "我拿这把枪换你的能量电池，但枪90、电池76，你给我补14信用点。",
    GameContext = new GameConversationContext
    {
        Character = new NativeCharacterContext
        {
            Key = "scavGeneral",
            OriginalIdentifier = "scavGeneral"
        },
        Trade = new NativeTradeContext
        {
            Item = new NativeItemContext
            {
                RuntimeKey = 34,
                DisplayName = "紧凑型手枪",
                Owner = "player",
                CurrentValue = 90
            },
            ClientCarriedItems = new List<NativeItemContext>
            {
                new()
                {
                    RuntimeKey = 685,
                    DisplayName = "能量电池",
                    Owner = "client",
                    CurrentValue = 76
                }
            },
            PlayerOfferedItems = new List<NativeItemContext>
            {
                new()
                {
                    RuntimeKey = 34,
                    DisplayName = "紧凑型手枪",
                    Owner = "player",
                    CurrentValue = 90
                }
            }
        }
    }
};

var wrongDirection = new ConversationResponse
{
    NpcReply = "行，但你还要补我14信用点。",
    BarterProposal = AcceptedBarter(),
    WalletTransfer = new WalletTransferProposal()
};
InvokeNormalize(normalize, request, wrongDirection);
Check(!wrongDirection.BarterProposal.Accepted, "玩家补顾客的不可执行差价没有被拒绝");

var correctDirection = new ConversationResponse
{
    NpcReply = "可以，我补你14信用点。",
    BarterProposal = AcceptedBarter(),
    WalletTransfer = new WalletTransferProposal
    {
        Explicit = true,
        Accepted = true,
        Amount = 14,
        Source = "client_budget"
    }
};
InvokeNormalize(normalize, request, correctDirection);
Check(correctDirection.BarterProposal.Accepted, "顾客补玩家的可执行差价被错误拒绝");

Console.WriteLine("Barter direction regression passed.");

static BarterProposal AcceptedBarter() => new()
{
    Explicit = true,
    Accepted = true,
    ClientItemRuntimeKey = 685,
    RequiredPlayerItemRuntimeKey = 34,
    ValueAssessment = new BarterValueAssessment
    {
        Decision = "accept",
        Reason = "双方价值可通过补款平衡"
    }
};

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void InvokeNormalize(MethodInfo method, ConversationRequest request, ConversationResponse response)
{
    try
    {
        method.Invoke(null, new object[] { request, response });
    }
    catch (TargetInvocationException ex) when (ex.InnerException is not null)
    {
        throw new InvalidOperationException(
            $"Normalize failed: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}",
            ex.InnerException);
    }
}
