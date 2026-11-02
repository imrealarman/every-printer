using System.Drawing;

namespace EveryPrinter.Models;

/// <summary>Anything drawn on a sheet besides the page contents. All coordinates are in points, from the top-left of the sheet.</summary>
public abstract record Overlay;

/// <summary>Text anchored inside <see cref="Bounds"/>. The renderer measures the text, so tiling and anchoring happen there.</summary>
public sealed record TextOverlay(
    string Text, string Font, float Size, bool Bold, bool Italic, string Color, double Opacity, double Angle,
    RectangleF Bounds, Anchor Anchor, float OffsetX, float OffsetY, bool Tile, RectangleF? Clip) : Overlay;

public sealed record ImageOverlay(
    string Path, RectangleF Bounds, Anchor Anchor, float OffsetX, float OffsetY, double WidthFraction, double Opacity, double Angle, bool Tile) : Overlay;

/// <summary>A picture placed on a page, stretched into <see cref="Rect"/> (already mapped onto the sheet). Rotated with the page.</summary>
public sealed record PictureOverlay(string Path, RectangleF Rect, int Rotation, RectangleF? Clip) : Overlay;

public sealed record RectOverlay(RectangleF Rect, string? Fill, double FillOpacity, string? Stroke, float StrokeWidth, RectangleF? Clip) : Overlay;

public sealed record LineOverlay(PointF From, PointF To, string Color, float Width) : Overlay;

/// <summary>A letterhead or form (a page of a PDF or an image) stretched over the whole sheet, behind the pages.</summary>
public sealed record FormOverlay(string Path) : Overlay;

/// <summary>Where one page (or part of a page) goes on a sheet.</summary>
public sealed class Placement
{
    public required PageRef Page { get; init; }

    /// <summary>Where the visible window lands on the sheet.</summary>
    public RectangleF Dest { get; init; }

    /// <summary>The cell the page may not draw outside of.</summary>
    public RectangleF Clip { get; init; }

    /// <summary>The visible window as fractions of the unrotated source page (the page crop, or a poster tile of it).</summary>
    public RectangleF Window { get; init; }

    /// <summary>Total clockwise rotation in degrees.</summary>
    public int Rotation { get; init; }

    /// <summary>Sheet points per source point.</summary>
    public float Scale { get; init; }

    /// <summary>1-based position of the page in the output, as used by the {page} macro.</summary>
    public int Number { get; init; }

    public bool NeedsRaster => Page.Annotations.Any(a => a.Kind == AnnotationKind.Redact) || !Page.Job.VectorOk;

    /// <summary>Maps a rectangle given as fractions of the unrotated page onto the sheet.</summary>
    public RectangleF MapRect(RectangleF r)
    {
        var a = MapPoint(r.Left, r.Top);
        var b = MapPoint(r.Right, r.Bottom);
        return RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    public PointF MapPoint(float x, float y)
    {
        var u = Window.Width > 0 ? (x - Window.X) / Window.Width : 0;
        var v = Window.Height > 0 ? (y - Window.Y) / Window.Height : 0;
        var p = Geometry.Rotate(u, v, Rotation);
        return new PointF(Dest.X + p.X * Dest.Width, Dest.Y + p.Y * Dest.Height);
    }

    /// <summary>Inverse of <see cref="MapPoint"/>: a sheet position to fractions of the unrotated page.</summary>
    public PointF Unmap(float sx, float sy)
    {
        var p = Geometry.Unrotate(Dest.Width > 0 ? (sx - Dest.X) / Dest.Width : 0, Dest.Height > 0 ? (sy - Dest.Y) / Dest.Height : 0, Rotation);
        return new PointF(Window.X + p.X * Window.Width, Window.Y + p.Y * Window.Height);
    }
}

public sealed class Sheet
{
    public float Width { get; set; }
    public float Height { get; set; }
    public List<Placement> Placements { get; } = [];

    /// <summary>Drawn first: letterhead or form.</summary>
    public List<Overlay> Behind { get; } = [];

    /// <summary>Drawn after the pages: marks, borders, annotations.</summary>
    public List<Overlay> Front { get; } = [];

    /// <summary>0-based position among the output sheets.</summary>
    public int Index { get; set; }

    /// <summary>The 1-based physical sheet of paper (two output sheets share one when printing on both sides).</summary>
    public int Physical { get; set; }

    public bool IsBack { get; set; }
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
    public string Label { get; set; } = "";
}

public static class Geometry
{
    /// <summary>Rotates a point given as fractions of a rectangle clockwise by 0, 90, 180 or 270 degrees.</summary>
    public static PointF Rotate(float x, float y, int rotation) => rotation switch
    {
        90 => new PointF(1 - y, x),
        180 => new PointF(1 - x, 1 - y),
        270 => new PointF(y, 1 - x),
        _ => new PointF(x, y),
    };

    public static PointF Unrotate(float x, float y, int rotation) => rotation switch
    {
        90 => new PointF(y, 1 - x),
        180 => new PointF(1 - x, 1 - y),
        270 => new PointF(1 - y, x),
        _ => new PointF(x, y),
    };

    public static RectangleF UnrotateRect(RectangleF r, int rotation)
    {
        var a = Unrotate(r.Left, r.Top, rotation);
        var b = Unrotate(r.Right, r.Bottom, rotation);
        return RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    public static (float X, float Y) AnchorFactors(Anchor anchor) => anchor switch
    {
        Anchor.TopLeft => (0, 0),
        Anchor.Top => (0.5f, 0),
        Anchor.TopRight => (1, 0),
        Anchor.Left => (0, 0.5f),
        Anchor.Right => (1, 0.5f),
        Anchor.BottomLeft => (0, 1),
        Anchor.Bottom => (0.5f, 1),
        Anchor.BottomRight => (1, 1),
        _ => (0.5f, 0.5f),
    };
}
