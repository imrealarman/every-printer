using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using EveryPrinter.Models;

namespace EveryPrinter.Services;

/// <summary>
/// Draws a sheet with GDI+. The same code serves the on-screen preview, the printer and the image export;
/// only the transform on the <see cref="Graphics"/> and the resolution of the page pictures differ.
/// </summary>
public sealed class SheetRenderer(PageSource source)
{
    private readonly Dictionary<string, Bitmap?> overlayImages = [];

    /// <summary>
    /// Draws <paramref name="sheet"/> in points onto <paramref name="g"/>, whose transform must already map sheet points to the target.
    /// <paramref name="pxPerPoint"/> is how many pixels of page picture to ask for per point, which decides sharpness and memory.
    /// </summary>
    public void Draw(Graphics g, Sheet sheet, PrintSettings settings, float pxPerPoint, bool fillPaper = true, CancellationToken cancel = default)
    {
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        if (fillPaper) g.FillRectangle(Brushes.White, 0, 0, sheet.Width, sheet.Height);

        var state = g.Save();
        try
        {
            if (sheet.FlipHorizontal || sheet.FlipVertical)
            {
                g.TranslateTransform(sheet.FlipHorizontal ? sheet.Width : 0, sheet.FlipVertical ? sheet.Height : 0);
                g.ScaleTransform(sheet.FlipHorizontal ? -1 : 1, sheet.FlipVertical ? -1 : 1);
            }

            foreach (var overlay in sheet.Behind) DrawOverlay(g, sheet, overlay, settings, pxPerPoint);

            using var attributes = Effects.Create(settings);
            foreach (var placement in sheet.Placements)
            {
                cancel.ThrowIfCancellationRequested();
                DrawPlacement(g, placement, attributes, pxPerPoint);
            }

            foreach (var overlay in sheet.Front) DrawOverlay(g, sheet, overlay, settings, pxPerPoint);
        }
        finally { g.Restore(state); }
    }

    /// <summary>Renders a whole sheet to a new bitmap at the given pixels per point.</summary>
    public Bitmap Render(Sheet sheet, PrintSettings settings, float pxPerPoint, CancellationToken cancel = default)
    {
        var w = Math.Max(1, (int)Math.Round(sheet.Width * pxPerPoint));
        var h = Math.Max(1, (int)Math.Round(sheet.Height * pxPerPoint));
        var bitmap = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(pxPerPoint * 72, pxPerPoint * 72);
        using var g = Graphics.FromImage(bitmap);
        g.ScaleTransform(w / sheet.Width, h / sheet.Height);
        Draw(g, sheet, settings, pxPerPoint, cancel: cancel);
        return bitmap;
    }

    private void DrawPlacement(Graphics g, Placement p, ImageAttributes attributes, float pxPerPoint)
    {
        if (p.Page.IsBlank) return;

        var odd = p.Rotation % 180 != 0;
        var pxW = (int)Math.Ceiling((odd ? p.Dest.Height : p.Dest.Width) * pxPerPoint);
        var pxH = (int)Math.Ceiling((odd ? p.Dest.Width : p.Dest.Height) * pxPerPoint);
        using var lease = source.Render(p.Page, p.Window, pxW, pxH);
        if (lease is null) return;

        var state = g.Save();
        try
        {
            g.SetClip(p.Clip, CombineMode.Intersect);
            g.TranslateTransform(p.Dest.X + p.Dest.Width / 2, p.Dest.Y + p.Dest.Height / 2);
            g.RotateTransform(p.Rotation);
            var w = odd ? p.Dest.Height : p.Dest.Width;
            var h = odd ? p.Dest.Width : p.Dest.Height;
            var target = new RectangleF(-w / 2, -h / 2, w, h);
            var src = lease.Source;
            g.DrawImage(lease.Bitmap, Points(target), src, GraphicsUnit.Pixel, attributes);
        }
        finally { g.Restore(state); }
    }

    private static PointF[] Points(RectangleF r) => [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Left, r.Bottom)];

    // ---- Overlays ----

    private void DrawOverlay(Graphics g, Sheet sheet, Overlay overlay, PrintSettings settings, float pxPerPoint)
    {
        switch (overlay)
        {
            case RectOverlay r:
                DrawRect(g, r);
                break;
            case LineOverlay l:
                using (var pen = new Pen(OverlayMath.ParseColor(l.Color), l.Width)) g.DrawLine(pen, l.From, l.To);
                break;
            case TextOverlay t:
                DrawText(g, t);
                break;
            case ImageOverlay i:
                DrawImageOverlay(g, i);
                break;
            case FormOverlay f:
                DrawForm(g, sheet, f, settings, pxPerPoint);
                break;
        }
    }

    private static void DrawRect(Graphics g, RectOverlay r)
    {
        var state = g.Save();
        try
        {
            if (r.Clip is { } clip) g.SetClip(clip, CombineMode.Intersect);
            if (r.Fill is not null)
            {
                // Highlights multiply so the text underneath stays readable; solid fills simply cover.
                using var brush = new SolidBrush(OverlayMath.ParseColor(r.Fill, r.FillOpacity));
                g.FillRectangle(brush, r.Rect);
            }
            if (r.Stroke is not null && r.StrokeWidth > 0)
            {
                using var pen = new Pen(OverlayMath.ParseColor(r.Stroke), r.StrokeWidth);
                g.DrawRectangle(pen, r.Rect.X, r.Rect.Y, r.Rect.Width, r.Rect.Height);
            }
        }
        finally { g.Restore(state); }
    }

    private static void DrawText(Graphics g, TextOverlay t)
    {
        var style = (t.Bold ? FontStyle.Bold : 0) | (t.Italic ? FontStyle.Italic : 0);
        using var font = SafeFont(t.Font, t.Size, style);
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces };
        if (t.Text.Any(c => c is >= '֐' and <= 'ࣿ')) format.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
        var size = g.MeasureString(t.Text, font, PointF.Empty, format);
        using var brush = new SolidBrush(OverlayMath.ParseColor(t.Color, t.Opacity));

        var state = g.Save();
        try
        {
            if (t.Clip is { } clip) g.SetClip(clip, CombineMode.Intersect);
            foreach (var centre in OverlayMath.Centres(t.Bounds, t.Anchor, t.OffsetX, t.OffsetY, size.Width, size.Height, t.Angle, t.Tile))
            {
                var inner = g.Save();
                g.TranslateTransform(centre.X, centre.Y);
                g.RotateTransform((float)t.Angle);
                g.DrawString(t.Text, font, brush, new RectangleF(-size.Width / 2, -size.Height / 2, size.Width + 2, size.Height), format);
                g.Restore(inner);
            }
        }
        finally { g.Restore(state); }
    }

    private void DrawImageOverlay(Graphics g, ImageOverlay i)
    {
        var image = LoadOverlayImage(i.Path);
        if (image is null) return;
        var w = (float)(i.WidthFraction * i.Bounds.Width);
        var h = w * image.Height / image.Width;
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)Math.Clamp(i.Opacity, 0, 1) });
        foreach (var centre in OverlayMath.Centres(i.Bounds, i.Anchor, i.OffsetX, i.OffsetY, w, h, i.Angle, i.Tile))
        {
            var inner = g.Save();
            g.TranslateTransform(centre.X, centre.Y);
            g.RotateTransform((float)i.Angle);
            g.DrawImage(image, Points(new RectangleF(-w / 2, -h / 2, w, h)), new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel, attributes);
            g.Restore(inner);
        }
    }

    private void DrawForm(Graphics g, Sheet sheet, FormOverlay f, PrintSettings settings, float pxPerPoint)
    {
        if (source.FormPage(f.Path) is not { } page) return;
        // The form is scaled to cover the sheet, keeping its proportions, and is never coloured by the effects.
        var scale = Math.Min(sheet.Width / page.Width, sheet.Height / page.Height);
        var w = page.Width * scale;
        var h = page.Height * scale;
        using var lease = source.Render(page, new RectangleF(0, 0, 1, 1), (int)Math.Ceiling(w * pxPerPoint), (int)Math.Ceiling(h * pxPerPoint));
        if (lease is null) return;
        g.DrawImage(lease.Bitmap, new RectangleF((sheet.Width - w) / 2, (sheet.Height - h) / 2, w, h), lease.Source, GraphicsUnit.Pixel);
    }

    private Bitmap? LoadOverlayImage(string path)
    {
        lock (overlayImages)
        {
            if (overlayImages.TryGetValue(path, out var known)) return known;
            Bitmap? loaded = null;
            try
            {
                if (File.Exists(path))
                {
                    using var file = Image.FromFile(path);
                    loaded = new Bitmap(file);
                }
            }
            catch (Exception ex) when (ex is IOException or OutOfMemoryException or ArgumentException) { Log.Error("Load watermark image", ex); }
            overlayImages[path] = loaded;
            return loaded;
        }
    }

    /// <summary>The font asked for, or Segoe UI when it isn't installed.</summary>
    public static Font SafeFont(string family, float size, FontStyle style)
    {
        try { return new Font(family, Math.Max(1, size), style, GraphicsUnit.Point); }
        catch (ArgumentException) { return new Font("Segoe UI", Math.Max(1, size), FontStyle.Regular, GraphicsUnit.Point); }
    }
}
