namespace WindowsMemo.Interop;

/// <summary>Physical screen coordinates; negative origins are valid on secondary displays.</summary>
internal readonly record struct PixelBounds(int X, int Y, int Width, int Height)
{
    internal PixelBounds FitWithin(PixelBounds area, int minWidth, int minHeight)
    {
        if (area.Width <= 0 || area.Height <= 0 || minWidth <= 0 || minHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(area));
        int width = Math.Clamp(Width, Math.Min(minWidth, area.Width), area.Width);
        int height = Math.Clamp(Height, Math.Min(minHeight, area.Height), area.Height);
        int x = checked((int)Math.Clamp((long)X, area.X, (long)area.X + area.Width - width));
        int y = checked((int)Math.Clamp((long)Y, area.Y, (long)area.Y + area.Height - height));
        return new PixelBounds(x, y, width, height);
    }

    internal PixelBounds ScaleSize(double previousScale, double currentScale)
    {
        if (!double.IsFinite(previousScale) || !double.IsFinite(currentScale) || previousScale <= 0 || currentScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(currentScale));
        return this with
        {
            Width = checked((int)Math.Round(Width / previousScale * currentScale)),
            Height = checked((int)Math.Round(Height / previousScale * currentScale))
        };
    }
}
