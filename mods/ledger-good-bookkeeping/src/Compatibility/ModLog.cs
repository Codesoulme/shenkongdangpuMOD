using UnityEngine;

namespace AIWorldPawnshop;

internal static class ModLog
{
    internal static void Info(string message) => Debug.Log(message);
    internal static void Warning(string message) => Debug.LogWarning(message);
    internal static void Error(string message) => Debug.LogError(message);
}
