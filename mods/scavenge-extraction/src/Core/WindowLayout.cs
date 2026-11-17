namespace ScavengeExtraction.Core;

// Screen-space layout, independent of the prefab's (often zero-sized) root rect.
public static class WindowLayout
{
    public static double FitScale(double width, double height, double availableWidth, double availableHeight)
        => System.Math.Min(1, System.Math.Min(availableWidth / System.Math.Max(1, width),
            availableHeight / System.Math.Max(1, height)));
    public static double CenterLeft(double left, double availableWidth, double contentWidth)
        => left + (availableWidth - contentWidth) * .5;
}
