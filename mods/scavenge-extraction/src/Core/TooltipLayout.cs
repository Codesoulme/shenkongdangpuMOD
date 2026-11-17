namespace ScavengeExtraction.Core;

public static class TooltipLayout
{
    // CJK glyphs occupy roughly two Latin columns in the native tooltip font.
    public static IEnumerable<string> Wrap(string text, int columns)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var line = new System.Text.StringBuilder(); int width = 0;
        foreach (char c in text)
        {
            int advance = c > 255 ? 2 : 1;
            if (width + advance > columns * 2 && line.Length > 0)
            { yield return line.ToString(); line.Clear(); width = 0; }
            line.Append(c); width += advance;
        }
        if (line.Length > 0) yield return line.ToString();
    }
}
