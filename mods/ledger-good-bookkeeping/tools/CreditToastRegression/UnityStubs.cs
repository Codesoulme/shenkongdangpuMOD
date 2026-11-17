// A deterministic host for the real toast source. It exercises scheduling and
// object lifecycle, not Unity rendering, shaders, font assets or scene layout.
using System.Reflection;

namespace UnityEngine
{
    public static class ObjectStore
    {
        public static readonly List<GameObject> Created = new();
        public static GameObject[] ActiveToasts() => Created.Where(g => !g.Destroyed && g.Active &&
            g.name == "DeepSpacePawnshop.CreditChangeToast").ToArray();
    }
    public class Object
    {
        public static T[] FindObjectsOfType<T>() where T : Component => ObjectStore.Created
            .Where(g => !g.Destroyed && g.Active).Select(g => g.Get<T>()).OfType<T>().ToArray();
        public static void Destroy(Object value)
        {
            var go = value as GameObject ?? ((Component)value).gameObject;
            go.Destroyed = true;
            foreach (var component in go.Components.ToArray()) component.GetType()
                .GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(component, null);
        }
    }
    public class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform => gameObject.transform;
    }
    public class MonoBehaviour : Component { }
    public class Transform : Component { public void SetParent(Transform parent, bool worldPositionStays) { } }
    public class RectTransform : Transform
    {
        public Vector2 anchorMin, anchorMax, pivot, sizeDelta, anchoredPosition;
    }
    public class GameObject : Object
    {
        public readonly string name;
        public bool Destroyed, Active = true;
        public readonly List<Component> Components = new();
        public Transform transform;
        public GameObject(string name)
        {
            this.name = name;
            transform = new Transform { gameObject = this };
            ObjectStore.Created.Add(this);
        }
        public void SetActive(bool value) => Active = value;
        public T AddComponent<T>() where T : Component, new()
        {
            var component = new T { gameObject = this };
            Components.Add(component);
            if (component is RectTransform rect) transform = rect;
            return component;
        }
        public T? Get<T>() where T : Component => Components.OfType<T>().FirstOrDefault();
    }
    public enum RenderMode { ScreenSpaceOverlay }
    public class Canvas : Component
    {
        public bool isRootCanvas = true, isActiveAndEnabled = true;
        public RenderMode renderMode;
        public int sortingOrder;
    }
    public class CanvasGroup : Component { public float alpha; }
    public readonly struct Color
    {
        public Color(float r, float g, float b, float a) { }
    }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 up => new(0, 1);
        public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.x + b.x, a.y + b.y);
        public static Vector2 operator *(Vector2 a, float b) => new(a.x * b, a.y * b);
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => new(Mathf.Lerp(a.x, b.x, t), Mathf.Lerp(a.y, b.y, t));
    }
    public static class Time { public static int frameCount; public static float unscaledDeltaTime = 0.1f; }
    public static class Mathf
    {
        public static float Abs(float n) => Math.Abs(n);
        public static float Clamp01(float n) => Math.Clamp(n, 0, 1);
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
    public static class Debug { public static void LogWarning(object value) => Console.WriteLine(value); }
}
namespace TMPro
{
    [Flags] public enum FontStyles { Bold = 1 }
    public enum TextAlignmentOptions { Center }
    public enum TextOverflowModes { Ellipsis }
    public class TextMeshProUGUI : UnityEngine.Component
    {
        public object? font, fontSharedMaterial;
        public FontStyles fontStyle;
        public float fontSize, characterSpacing, wordSpacing, lineSpacing, outlineWidth;
        public string text = "";
        public TextAlignmentOptions alignment;
        public TextOverflowModes overflowMode;
        public bool enableAutoSizing, enableWordWrapping;
        public UnityEngine.Color color, outlineColor;
    }
}
public class StoreUIManager
{
    public static StoreUIManager? Instance;
    public StoreCalendar? storeCalendar;
}
public class StoreCalendar { public TMPro.TextMeshProUGUI? dayTMP; }
public class DialogUIManager
{
    public static DialogUIManager? Instance;
    public ClientBubble? clientBubble;
}
public class ClientBubble { public TMPro.TextMeshProUGUI? textfield; }
public class NegociationUIManager { public void SellMultiple() { } public void BuyMultipleItem() { } public void OnWholesaleClick() { } }
public class PlayerStore { public static PlayerStore? Instance; public int playerCash; }
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class HarmonyPatch : Attribute
    {
        public Type Type; public string Name;
        public HarmonyPatch(Type type, string name) { Type = type; Name = name; }
    }
}
