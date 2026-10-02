namespace Win11PerformanceControlCenter.App.Core;

public sealed record WindowLayout(
    double MinWidth,
    double MinHeight,
    double MaxWidth,
    double MaxHeight,
    double Width,
    double Height,
    double Left,
    double Top);

public static class WindowGeometryPolicy
{
    public static int ScaleMinimumToPixels(
        double minimumDip,
        double dpiScale,
        int maximumPixels)
    {
        var scaled = (int)Math.Ceiling(
            Math.Max(0d, minimumDip) * Math.Max(0.01d, dpiScale));
        return Math.Clamp(scaled, 1, Math.Max(1, maximumPixels));
    }

    public static WindowLayout Constrain(
        double workLeft,
        double workTop,
        double workWidth,
        double workHeight,
        double currentWidth,
        double currentHeight,
        double currentLeft,
        double currentTop,
        double margin = 16d,
        double desiredMinWidth = 1024d,
        double desiredMinHeight = 640d)
    {
        workWidth = Math.Max(1d, workWidth);
        workHeight = Math.Max(1d, workHeight);
        var availableWidth = Math.Max(1d, workWidth - margin);
        var availableHeight = Math.Max(1d, workHeight - margin);
        var minWidth = Math.Min(desiredMinWidth, availableWidth);
        var minHeight = Math.Min(desiredMinHeight, availableHeight);
        var width = Math.Min(Math.Max(currentWidth, minWidth), availableWidth);
        var height = Math.Min(Math.Max(currentHeight, minHeight), availableHeight);

        var left = currentLeft;
        var top = currentTop;
        var maxLeft = workLeft + workWidth - width;
        var maxTop = workTop + workHeight - height;
        if (left < workLeft || left > maxLeft ||
            top < workTop || top > maxTop)
        {
            left = workLeft + Math.Max(0d, (workWidth - width) / 2d);
            top = workTop + Math.Max(0d, (workHeight - height) / 2d);
        }

        return new WindowLayout(
            minWidth,
            minHeight,
            Math.Max(minWidth, workWidth),
            Math.Max(minHeight, workHeight),
            width,
            height,
            left,
            top);
    }
}
