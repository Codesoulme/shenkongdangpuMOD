using System.Linq;
using TMPro;
using UnityEngine;

namespace AIWorldPawnshop.UI;

internal static class CreditChangeToast
{
    private const float FontScale = 1.5f;
    private const float StartHeight = 125f;
    private const float RiseDistance = 150f;
    private static readonly Color IncomeColor = new(0.28f, 0.78f, 0.40f, 1f);
    private static readonly Color ExpenseColor = new(0.92f, 0.34f, 0.30f, 1f);
    private static readonly Color OutlineColor = new(0.03f, 0.03f, 0.03f, 1f);
    private static readonly Queue<long> Pending = new();
    private static readonly List<long> Batch = new();
    private static int _batchDepth;
    private static CreditChangeToastAnimation? _active;

    internal static void BeginBatch() => _batchDepth++;

    internal static void EndBatch()
    {
        if (_batchDepth == 0 || --_batchDepth != 0) return;
        foreach (var delta in Batch) Pending.Enqueue(delta);
        Batch.Clear();
        PlayNext();
    }

    internal static void Reset()
    {
        Pending.Clear();
        Batch.Clear();
        _batchDepth = 0;
        var active = _active;
        _active = null;
        if (active != null)
        {
            active.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(active.gameObject);
        }
    }

    internal static void Show(long delta)
    {
        if (delta == 0)
            return;

        if (_batchDepth > 0)
        {
            // Equal-price items are separate real transactions. Sum adjacent
            // changes of the same sign; never discard them as duplicates.
            if (Batch.Count > 0 && (Batch[Batch.Count - 1] > 0) == (delta > 0))
                Batch[Batch.Count - 1] += delta;
            else
                Batch.Add(delta);
            return;
        }

        Pending.Enqueue(delta);
        PlayNext();
    }

    private static void PlayNext()
    {
        if (_active != null || _batchDepth > 0 || Pending.Count == 0) return;
        try
        {
            var canvas = FindCanvas();
            if (canvas is null)
            {
                Pending.Clear();
                return;
            }

            var delta = Pending.Dequeue();

            // Match the original calendar text at the top-left of the store.
            // Fall back to the native dialogue text during scenes where the
            // calendar object has not been initialized yet.
            var template = StoreUIManager.Instance?.storeCalendar?.dayTMP ??
                           DialogUIManager.Instance?.clientBubble?.textfield;
            var toastObject = new GameObject("DeepSpacePawnshop.CreditChangeToast");
            toastObject.transform.SetParent(canvas.transform, false);

            var rect = toastObject.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(760f, 88f);
            // Keep the toast in the same fixed canvas coordinate system as the
            // last known-good implementation. The counter's world position is
            // not reliable across the game's camera and canvas states.
            var startPosition = new Vector2(0f, StartHeight);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = startPosition;

            var text = toastObject.AddComponent<TextMeshProUGUI>();
            if (template is not null)
            {
                text.font = template.font;
                text.fontSharedMaterial = template.fontSharedMaterial;
                text.fontStyle = template.fontStyle | FontStyles.Bold;
                text.fontSize = template.fontSize * FontScale;
                text.characterSpacing = template.characterSpacing;
                text.wordSpacing = template.wordSpacing;
                text.lineSpacing = template.lineSpacing;
            }
            else
            {
                text.fontSize = 35f;
            }

            text.text = (delta > 0 ? "+ " : "- ") + Math.Abs(delta).ToString("N0") + "信用点";
            text.alignment = TextAlignmentOptions.Center;
            text.enableAutoSizing = false;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.color = delta > 0 ? IncomeColor : ExpenseColor;
            text.outlineColor = OutlineColor;
            text.outlineWidth = 0.22f;

            var group = toastObject.AddComponent<CanvasGroup>();
            group.alpha = 1f;
            var animation = toastObject.AddComponent<CreditChangeToastAnimation>();
            animation.Initialize(rect, group, startPosition, startPosition + Vector2.up * RiseDistance);
            _active = animation;
        }
        catch (Exception ex)
        {
            Pending.Clear();
            UnityEngine.Debug.LogWarning($"[账本] 收支提示显示失败：{ex.Message}");
        }
    }

    internal static void OnFinished(CreditChangeToastAnimation animation)
    {
        if (!ReferenceEquals(_active, animation)) return;
        _active = null;
        PlayNext();
    }

    internal static void OnRemoved(CreditChangeToastAnimation animation)
    {
        // Destruction of the canvas/scene cancels old notifications. Do not
        // replay a previous customer's income after loading a different save.
        if (!ReferenceEquals(_active, animation)) return;
        _active = null;
        Pending.Clear();
        Batch.Clear();
        _batchDepth = 0;
    }

    private static Canvas? FindCanvas()
    {
        var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
        return canvases
            .Where(canvas => canvas.isRootCanvas && canvas.isActiveAndEnabled)
            .OrderByDescending(canvas => canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            .ThenByDescending(canvas => canvas.sortingOrder)
            .FirstOrDefault();
    }

}

internal sealed class CreditChangeToastAnimation : MonoBehaviour
{
    private const float AnimationDuration = 2.4f;
    private RectTransform? _rect;
    private CanvasGroup? _group;
    private Vector2 _startPosition;
    private Vector2 _endPosition;
    private float _elapsed;

    internal void Initialize(RectTransform rect, CanvasGroup group, Vector2 startPosition, Vector2 endPosition)
    {
        _rect = rect;
        _group = group;
        _startPosition = startPosition;
        _endPosition = endPosition;
    }

    private void Update()
    {
        if (_rect is null || _group is null)
            return;

        _elapsed += Time.unscaledDeltaTime;
        var progress = Mathf.Clamp01(_elapsed / AnimationDuration);
        _rect.anchoredPosition = Vector2.Lerp(_startPosition, _endPosition, progress);
        _group.alpha = progress < 0.62f
            ? 1f
            : Mathf.Lerp(1f, 0f, (progress - 0.62f) / 0.38f);
        if (progress >= 1f)
        {
            // Destroy is deferred by Unity. Hide this toast before starting the
            // next one so two messages are never visible together.
            gameObject.SetActive(false);
            CreditChangeToast.OnFinished(this);
            Destroy(gameObject);
        }
    }

    private void OnDestroy() => CreditChangeToast.OnRemoved(this);
}
