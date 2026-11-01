using System.Drawing;
using EveryPrinter.Models;

namespace EveryPrinter.Services;

/// <summary>
/// Where marks go on a sheet. Both the bitmap renderer and the PDF writer use this, so a watermark lands in the same place
/// in the preview, on paper and in the saved PDF.
/// </summary>
public static class OverlayMath
{
    private const int MaxTiles = 600;

    /// <summary>Centres of each copy of a w-by-h mark (angle in degrees, clockwise) for an overlay inside <paramref name="bounds"/>.</summary>
    public static IEnumerable<PointF> Centres(RectangleF bounds, Anchor anchor, float offsetX, float offsetY, float w, float h, double angle, bool tile)
    {
        if (!tile)
        {
            var (ax, ay) = Geometry.AnchorFactors(anchor);
            yield return new PointF(bounds.X + (bounds.Width - w) * ax + w / 2 + offsetX, bounds.Y + (bounds.Height - h) * ay + h / 2 + offsetY);
            yield break;
        }

        // A lattice in the mark's own (rotated) frame, big enough to cover the bounds from any angle.
        var stepX = Math.Max(4, w * 1.6f);
        var stepY = Math.Max(4, h * 3.2f);
        var radius = (float)Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height) / 2;
        var cx = bounds.X + bounds.Width / 2 + offsetX;
        var cy = bounds.Y + bounds.Height / 2 + offsetY;
        var rad = angle * Math.PI / 180;
        var cos = (float)Math.Cos(rad);
        var sin = (float)Math.Sin(rad);
        var nx = (int)Math.Ceiling(radius / stepX);
        var ny = (int)Math.Ceiling(radius / stepY);
        var count = 0;
        for (var j = -ny; j <= ny; j++)
        for (var i = -nx; i <= nx; i++)
        {
            var lx = i * stepX + (j % 2 != 0 ? stepX / 2 : 0);
            var ly = j * stepY;
            var p = new PointF(cx + lx * cos - ly * sin, cy + lx * sin + ly * cos);
            if (p.X < bounds.Left - w || p.X > bounds.Right + w || p.Y < bounds.Top - h || p.Y > bounds.Bottom + h) continue;
            if (++count > MaxTiles) yield break;
            yield return p;
        }
    }

    public static Color ParseColor(string hex, double opacity = 1)
    {
        try
        {
            var c = ColorTranslator.FromHtml(hex);
            return Color.FromArgb((int)Math.Round(Math.Clamp(opacity, 0, 1) * 255), c.R, c.G, c.B);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return Color.FromArgb((int)Math.Round(Math.Clamp(opacity, 0, 1) * 255), 0, 0, 0);
        }
    }

    /// <summary>True for scripts GDI+ shapes correctly but the PDF writer can't (Arabic, Persian, Hebrew, Indic and similar).</summary>
    public static bool NeedsShaping(string text) => text.Any(c => c is >= '֐' and <= '໿' or >= 'יִ' and <= '﻿');

    /// <summary>Width and height of the box that holds a w-by-h mark rotated by <paramref name="angle"/> degrees.</summary>
    public static SizeF RotatedExtent(float w, float h, double angle)
    {
        var rad = angle * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(rad));
        var sin = Math.Abs(Math.Sin(rad));
        return new SizeF((float)(w * cos + h * sin), (float)(w * sin + h * cos));
    }
}
