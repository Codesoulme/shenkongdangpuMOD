using UnityEngine;

namespace MelonLoader;

/// <summary>
/// Source compatibility for the existing business adapters. The formal game
/// has no MelonLoader; messages are routed to Unity's player log instead.
/// </summary>
internal static class MelonLogger
{
    internal static void Msg(string message) => Debug.Log(message);
    internal static void Warning(string message) => Debug.LogWarning(message);
    internal static void Error(string message) => Debug.LogError(message);
}
