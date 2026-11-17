using ScavengeExtraction.Core;
using UnityEngine;

namespace ScavengeExtraction;

internal static class NativeWindowLayout
{
    internal static Camera? CameraFor(RectTransform rect)
    {
        var canvas = rect.GetComponentInParent<Canvas>()?.rootCanvas;
        return canvas?.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas?.worldCamera;
    }
    internal static Rect Bounds(PixelWindow window)
    {
        var rect = window.rectTransform;
        var camera = CameraFor(rect);
        // Native machinery children are placed from the local top-left (0,0).
        // PixelWindow.Validate sizes those children, but never sizes the root.
        var origin = new Vector3(rect.rect.xMin, rect.rect.yMax);
        var a = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(origin));
        var b = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(origin + new Vector3(window.widthPixels, 0)));
        var c = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(origin + new Vector3(0, -window.heightPixels)));
        var d = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(origin + new Vector3(window.widthPixels, -window.heightPixels)));
        return Rect.MinMaxRect(Mathf.Min(a.x, b.x, c.x, d.x), Mathf.Min(a.y, b.y, c.y, d.y),
            Mathf.Max(a.x, b.x, c.x, d.x), Mathf.Max(a.y, b.y, c.y, d.y));
    }
    internal static void Place(PixelWindow window, Rect area, Vector3 baseline, bool top)
    {
        var rect = window.rectTransform;
        rect.localScale = baseline;
        var bounds = Bounds(window);
        float scale = (float)WindowLayout.FitScale(bounds.width, bounds.height, area.width, area.height);
        rect.localScale = baseline * scale;
        bounds = Bounds(window);
        var target = new Vector2((float)WindowLayout.CenterLeft(area.xMin, area.width, bounds.width),
            top ? area.yMax - bounds.height : area.yMin);
        var camera = CameraFor(rect);
        if (rect.parent is RectTransform parent
            && RectTransformUtility.ScreenPointToWorldPointInRectangle(parent, bounds.min, camera, out var from)
            && RectTransformUtility.ScreenPointToWorldPointInRectangle(parent, target, camera, out var to))
            rect.position += to - from;
    }
}
