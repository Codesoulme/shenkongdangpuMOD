using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace AIWorldPawnshop.UI;

internal static class NativeLedgerTab
{
    private static AdvCalendarUIManager? _owner;
    private static Button? _tab;
    private static RectTransform? _strip;
    private static RectTransform? _section;
    private static RectTransform? _body;
    private static TextMeshProUGUI? _template;
    private static int _page;
    private static int _selectedDayOffset;
    private const int PageSize = 6;
    private static readonly Color Ink = new(0.25f, 0.25f, 0.25f, 1f);
    private static readonly Color Income = new(0.21f, 0.42f, 0.26f, 1f);
    private static readonly Color Expense = new(0.61f, 0.23f, 0.20f, 1f);
    private static readonly Color PaperTint = new(0.35f, 0.26f, 0.18f, 0.055f);
    private static readonly Color Rule = new(0.35f, 0.26f, 0.18f, 0.16f);

    internal static void Attach(AdvCalendarUIManager manager)
    {
        // Use Unity's null comparison after scene unload.
        if (manager == null || manager.UIPanel == null || manager.statusButton == null || manager.calendarSection == null)
            return;
        if (_owner == manager && _tab != null && _section != null)
            return;

        _owner = manager;
        var labelTemplate = manager.statusButton.GetComponentInChildren<TextMeshProUGUI>(true);
        if (labelTemplate == null) return;
        _template = labelTemplate;
        var panel = manager.UIPanel.GetComponent<RectTransform>();
        Canvas.ForceUpdateCanvases();
        var originalTab = manager.statusButton.GetComponent<RectTransform>();
        var corners = new Vector3[4];
        originalTab.GetWorldCorners(corners);
        var bottom = panel.InverseTransformPoint(corners[0]);
        var top = panel.InverseTransformPoint(corners[1]);
        var height = Mathf.Max(panel.rect.height, 1f);
        var yMin = Mathf.Clamp01((bottom.y - panel.rect.yMin) / height);
        var yMax = Mathf.Clamp01((top.y - panel.rect.yMin) / height);
        if (yMax - yMin < 0.015f) { yMin = 0.85f; yMax = 0.91f; }

        _strip = Rect("DeepSpacePawnshopLedger.Tabs", panel, .18f, yMin, .82f, yMax);
        // New controls contain no localization scripts or persistent events.
        _tab = MakeButton("LedgerTab", _strip, manager.statusButton, labelTemplate, "账本",
            () => ShowLedger(manager));
        var buttons = new[] { manager.calendarButton, manager.nightlyReportButton, manager.statusButton, _tab };
        for (var i = 0; i < buttons.Length; i++)
        {
            var rect = buttons[i].GetComponent<RectTransform>();
            rect.SetParent(_strip, false);
            var element = buttons[i].GetComponent<LayoutElement>();
            if (element != null) element.ignoreLayout = true;
            var fitter = buttons[i].GetComponent<ContentSizeFitter>();
            if (fitter != null) fitter.enabled = false;
            Place(rect, i / 4f + .004f, 0, (i + 1) / 4f - .004f, 1);
        }
        // calendarSection spans the outer window. Measure the centered inset
        // parchment Graphic itself, which is the actual text-safe area.
        var paper = FindPaperBounds(manager, panel);
        var pageTop = Mathf.Min(paper.yMax, yMin - .025f);
        _section = Rect("DeepSpacePawnshopLedger.Section", panel, paper.xMin, paper.yMin,
            paper.xMax, Mathf.Max(paper.yMin + .1f, pageTop));
        _section.gameObject.AddComponent<RectMask2D>();
        _section.gameObject.SetActive(false);
    }

    internal static void Open(AdvCalendarUIManager manager)
    {
        Attach(manager);
        ShowLedger(manager);
    }

    internal static void Hide(AdvCalendarUIManager manager)
    {
        if (_owner != manager) return;
        if (_section != null) _section.gameObject.SetActive(false);
        if (_tab != null) _tab.image.color = manager.normalColor;
        if (manager.gridLayoutGroup != null)
            manager.gridLayoutGroup.gameObject.SetActive(false);
    }

    private static void ShowLedger(AdvCalendarUIManager manager)
    {
        if (_owner != manager || _section == null) return;
        manager.OnAnySectionButtonClicked();
        if (manager.gridLayoutGroup != null)
            manager.gridLayoutGroup.gameObject.SetActive(false);
        _page = 0;
        _selectedDayOffset = 0;
        _section.gameObject.SetActive(true);
        _tab!.image.color = manager.selectedColor;
        Render(manager);
    }

    internal static void ShowCalendar(AdvCalendarUIManager manager)
    {
        if (manager != null && manager.gridLayoutGroup != null)
            manager.gridLayoutGroup.gameObject.SetActive(true);
    }

    private static (float xMin, float yMin, float xMax, float yMax) FindPaperBounds(
        AdvCalendarUIManager manager, RectTransform panel)
    {
        var bestScore = float.MaxValue;
        var best = (.065f, .10f, .935f, .90f);
        foreach (var image in manager.UIPanel.GetComponentsInChildren<Image>(true))
        {
            if (image == null || image.transform == manager.UIPanel.transform)
                continue;
            var rect = image.rectTransform;
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var lowerLeft = panel.InverseTransformPoint(corners[0]);
            var upperRight = panel.InverseTransformPoint(corners[2]);
            var width = (upperRight.x - lowerLeft.x) / Mathf.Max(panel.rect.width, 1f);
            var height = (upperRight.y - lowerLeft.y) / Mathf.Max(panel.rect.height, 1f);
            var centerX = (lowerLeft.x + upperRight.x - 2 * panel.rect.xMin) /
                          (2 * Mathf.Max(panel.rect.width, 1f));
            var centerY = (lowerLeft.y + upperRight.y - 2 * panel.rect.yMin) /
                          (2 * Mathf.Max(panel.rect.height, 1f));
            if (width < .70f || width > .96f || height < .62f || height > .96f ||
                Mathf.Abs(centerX - .5f) > .08f || Mathf.Abs(centerY - .5f) > .10f)
                continue;

            var score = Mathf.Abs(width - .87f) + Mathf.Abs(height - .82f) +
                        Mathf.Abs(centerX - .5f) + Mathf.Abs(centerY - .5f);
            if (score >= bestScore) continue;
            bestScore = score;
            best = (
                Mathf.Clamp01((lowerLeft.x - panel.rect.xMin) / Mathf.Max(panel.rect.width, 1f)),
                Mathf.Clamp01((lowerLeft.y - panel.rect.yMin) / Mathf.Max(panel.rect.height, 1f)),
                Mathf.Clamp01((upperRight.x - panel.rect.xMin) / Mathf.Max(panel.rect.width, 1f)),
                Mathf.Clamp01((upperRight.y - panel.rect.yMin) / Mathf.Max(panel.rect.height, 1f)));
        }
        return best;
    }

    private static void Render(AdvCalendarUIManager manager)
    {
        if (_section == null || _template == null) return;
        var runtime = AIWorldPawnshopMod.Instance?.Runtime;
        if (runtime == null) return;
        var currentDay = StoreStation.Instance != null ? StoreStation.Instance.dayCounter : 0;
        if (_selectedDayOffset > 0 && currentDay <= 1)
            _selectedDayOffset = 0;
        var day = Math.Max(1, currentDay - _selectedDayOffset);
        var data = CreditLedgerUi.Read(runtime.Entries, day, _page, PageSize);
        _page = data.Page;
        if (_body != null)
        {
            _body.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(_body.gameObject);
        }
        _body = Rect("LedgerTable", _section, .04f, .015f, .96f, .985f);
        var fontSize = Mathf.Clamp(_section.rect.height * .032f, 12f, 27f);
        Text(_body, $"第 {day} 天 · 信用点账本", 0, .91f, .62f, 1, fontSize * 1.14f, FontStyles.Bold);
        Text(_body, $"收支汇总  /  共 {data.TotalCount} 笔", 0, .855f, .60f, .91f,
            fontSize * .78f, align: TextAlignmentOptions.MidlineLeft);
        var todayButton = DayButton(manager, "Today", "今天", .64f, _selectedDayOffset == 0,
            () => SelectDay(manager, 0));
        var yesterdayButton = DayButton(manager, "Yesterday", "昨天", .82f, _selectedDayOffset == 1,
            () => SelectDay(manager, 1));
        yesterdayButton.interactable = currentDay > 1;

        Metric(_body, "收入", data.Income, 0, Income, fontSize);
        Metric(_body, "支出", data.Expense, .34f, Expense, fontSize);
        Metric(_body, "净额", data.Income - data.Expense, .68f, Ink, fontSize);
        var columns = new[] { 0f, .06f, .18f, .34f, .52f, .90f, 1f };
        var names = new[] { "序号", "收支", "顾客", "交易类型", "物品 / 明细", "信用点" };
        Band(_body, "Header", 0, .60f, 1, .67f, new Color(.61f, .23f, .20f, .15f));
        for (var i = 0; i < names.Length; i++)
            Cell(names[i], columns[i], columns[i + 1], .60f, .67f, fontSize * .82f, i == 5, Ink, FontStyles.Bold);
        for (var row = 0; row < data.Entries.Count; row++)
        {
            var entry = data.Entries[row];
            var upper = .60f - row * .082f;
            var lower = upper - .082f;
            if (row % 2 == 0) Band(_body, "RowShade", 0, lower, 1, upper, PaperTint);
            var income = entry.Direction == "收入";
            var values = new[] { (data.Page * PageSize + row + 1).ToString("00"), entry.Direction,
                CreditLedgerUi.Value(entry.CustomerName), CreditLedgerUi.Value(entry.TransactionType),
                CreditLedgerUi.Value(FormatLedgerDetail(entry)),
                (income ? "+" : "−") + entry.Amount.ToString("N0") };
            for (var col = 0; col < values.Length; col++)
                Cell(values[col], columns[col], columns[col + 1], lower, upper,
                    fontSize * (col == 4 ? .79f : .88f), col == 5,
                    col == 1 || col == 5 ? (income ? Income : Expense) : Ink);
            Band(_body, "RowRule", 0, lower, 1, lower + .0015f, Rule);
        }
        if (data.TotalCount == 0)
            Text(_body, "该日暂无信用点交易记录", 0, .30f, 1, .55f,
                fontSize * .95f, align: TextAlignmentOptions.Center);
        var label = manager.statusButton.GetComponentInChildren<TextMeshProUGUI>(true);
        var previous = MakeButton("Previous", _body, manager.statusButton, label, "上一页",
            () => { _page--; Render(manager); });
        Place(previous.GetComponent<RectTransform>(), .34f, .012f, .45f, .072f);
        previous.interactable = data.Page > 0;
        Text(_body, $"{data.Page + 1} / {data.PageCount}", .45f, .012f, .55f, .072f,
            fontSize * .85f, align: TextAlignmentOptions.Center);
        var next = MakeButton("Next", _body, manager.statusButton, label, "下一页",
            () => { _page++; Render(manager); });
        Place(next.GetComponent<RectTransform>(), .55f, .012f, .66f, .072f);
        next.interactable = data.Page + 1 < data.PageCount;
    }

    private static Button DayButton(AdvCalendarUIManager manager, string name, string caption,
        float left, bool selected, UnityAction action)
    {
        var template = manager.statusButton.GetComponentInChildren<TextMeshProUGUI>(true);
        var button = MakeButton(name, _body!, manager.statusButton, template, caption, action);
        Place(button.GetComponent<RectTransform>(), left, .855f, left + .16f, .91f);
        var fill = selected ? new Color(.61f, .23f, .20f, .18f) : PaperTint;
        var colors = button.colors;
        colors.normalColor = fill;
        colors.highlightedColor = selected
            ? new Color(.61f, .23f, .20f, .25f)
            : new Color(.35f, .26f, .18f, .13f);
        colors.pressedColor = new Color(.61f, .23f, .20f, .30f);
        colors.selectedColor = fill;
        button.colors = colors;
        button.image.color = fill;
        var label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        label.color = selected ? Expense : Ink;
        label.fontStyle = selected ? FontStyles.Bold : FontStyles.Normal;
        return button;
    }

    private static void SelectDay(AdvCalendarUIManager manager, int offset)
    {
        _selectedDayOffset = offset;
        _page = 0;
        Render(manager);
    }

    private static void Cell(string value, float left, float right, float bottom, float top,
        float size, bool rightAlign, Color color, FontStyles style = FontStyles.Normal)
    {
        var text = Text(_body!, value, left + .006f, bottom + .005f, right - .006f, top - .005f,
            size, style, rightAlign ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.MidlineLeft);
        text.color = color;
    }

    private static string FormatLedgerDetail(CreditLedgerUi.LedgerDisplayEntry entry)
    {
        var item = entry.FoldCount > 1 ? $"{entry.Detail} ×{entry.FoldCount}" : entry.Detail;
        return string.IsNullOrWhiteSpace(entry.Negotiation) ? item : $"{item}\n议价：{entry.Negotiation}";
    }

    private static void Metric(RectTransform parent, string name, long amount, float left, Color color, float size)
    {
        var right = left + .32f;
        Band(parent, $"{name}Card", left, .69f, right, .81f, PaperTint);
        Text(parent, name, left + .025f, .755f, right - .025f, .80f,
            size * .70f, FontStyles.Normal);
        var amountText = Text(parent, $"{amount:N0}", left + .025f, .695f, right - .025f, .755f,
            size * .98f, FontStyles.Bold);
        amountText.color = color;
    }

    private static TextMeshProUGUI Text(RectTransform parent, string value, float x0, float y0, float x1,
        float y1, float size, FontStyles style = FontStyles.Normal, TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
    {
        var rect = Rect("Text", parent, x0, y0, x1, y1);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        ApplyFont(text, _template!);
        text.text = value;
        text.fontSize = size;
        text.fontStyle = style;
        text.alignment = align;
        text.color = Ink;
        return text;
    }

    private static void ApplyFont(TextMeshProUGUI text, TextMeshProUGUI template)
    {
        text.font = template.font;
        text.fontSharedMaterial = template.fontSharedMaterial;
        text.enableAutoSizing = false;
        text.enableWordWrapping = true;
        text.richText = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        text.margin = Vector4.zero;
    }

    private static Button MakeButton(string name, RectTransform parent, Button template,
        TextMeshProUGUI labelTemplate, string caption, UnityAction action)
    {
        var rect = Rect(name, parent, 0, 0, 1, 1);
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = template.image.sprite;
        image.type = template.image.type;
        image.color = _owner!.normalColor;
        var button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.colors = template.colors;
        button.transition = template.transition;
        button.spriteState = template.spriteState;
        button.onClick.AddListener(action);
        var label = Rect("Label", rect, .03f, .03f, .97f, .97f).gameObject.AddComponent<TextMeshProUGUI>();
        ApplyFont(label, labelTemplate);
        label.color = labelTemplate.color;
        label.fontStyle = labelTemplate.fontStyle;
        label.fontSize = labelTemplate.fontSize;
        label.enableAutoSizing = true;
        label.fontSizeMin = 8;
        label.fontSizeMax = labelTemplate.fontSize;
        label.enableWordWrapping = false;
        label.alignment = TextAlignmentOptions.Center;
        label.text = caption;
        return button;
    }

    private static void Band(RectTransform parent, string name, float x0, float y0, float x1, float y1, Color color)
    {
        var image = Rect(name, parent, x0, y0, x1, y1).gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
    }

    private static RectTransform Rect(string name, Transform parent, float x0, float y0, float x1, float y1)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        var element = rect.gameObject.AddComponent<LayoutElement>();
        element.ignoreLayout = true;
        Place(rect, x0, y0, x1, y1);
        return rect;
    }

    private static void Place(RectTransform rect, float x0, float y0, float x1, float y1)
    {
        rect.anchorMin = new Vector2(x0, y0);
        rect.anchorMax = new Vector2(x1, y1);
        rect.pivot = new Vector2(.5f, .5f);
        rect.localScale = Vector3.one;
        rect.localRotation = Quaternion.identity;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }
}
