using System.Drawing;
using EveryPrinter.Models;

namespace EveryPrinter.Services;

/// <summary>Optional lookups that need the page pictures; the engine itself never renders anything.</summary>
public sealed class LayoutHooks
{
    public Func<PageRef, bool>? IsBlank { get; init; }

    /// <summary>The part of the page that has content, as fractions of the unrotated page.</summary>
    public Func<PageRef, RectangleF?>? ContentBounds { get; init; }

    public DateTime Now { get; init; } = DateTime.Now;
}

/// <summary>
/// Turns pages and settings into sheets: which pages go where, how big each is, and what is drawn over them.
/// It is pure geometry, so the preview, the printer, the PDF and the image export all get the same answer.
/// </summary>
public static class LayoutEngine
{
    private sealed record Item(PageRef Page, RectangleF Crop, int Number);

    public static List<Sheet> Build(IReadOnlyList<PageRef> source, PrintSettings s, LayoutHooks? hooks = null)
    {
        hooks ??= new LayoutHooks();
        var items = Select(source, s, hooks);
        var sheets = s.Mode switch
        {
            LayoutMode.Booklet => Booklet(items, s),
            LayoutMode.Poster => Poster(items, s),
            _ => Grid(items, s),
        };
        Decorate(sheets, items, s, hooks);
        return sheets;
    }

    // ---- Which pages ----

    private static List<Item> Select(IReadOnlyList<PageRef> source, PrintSettings s, LayoutHooks hooks)
    {
        var live = source.Where(p => !p.Deleted).ToList();
        var range = ParseRange(s.PageRange, live.Count);
        var picked = new List<PageRef>();
        for (var i = 0; i < live.Count; i++)
        {
            if (range is not null && !range.Contains(i + 1)) continue;
            if (s.OddEven == OddEven.Odd && (i + 1) % 2 == 0) continue;
            if (s.OddEven == OddEven.Even && (i + 1) % 2 == 1) continue;
            if (s.SkipBlankPages && hooks.IsBlank is { } blank && !live[i].IsBlank && blank(live[i])) continue;
            picked.Add(live[i]);
        }
        if (s.Reverse) picked.Reverse();

        var smart = s.Mode == LayoutMode.Join && s.JoinSmart && hooks.ContentBounds is not null;
        var items = new List<Item>(picked.Count);
        for (var i = 0; i < picked.Count; i++)
        {
            var crop = picked[i].Crop;
            if (smart && hooks.ContentBounds!(picked[i]) is { } bounds)
            {
                bounds.Inflate(0.008f, 0.008f);
                var cut = RectangleF.Intersect(bounds, crop);
                if (cut.Width > 0.02f && cut.Height > 0.02f) crop = cut;
            }
            items.Add(new Item(picked[i], crop, i + 1));
        }
        return items;
    }

    /// <summary>Parses "1-5, 8, 10-" into the page numbers it names. Empty or invalid text means every page (null).</summary>
    public static HashSet<int>? ParseRange(string text, int count)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var set = new HashSet<int>();
        foreach (var raw in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            var dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (!int.TryParse(part, out var one)) return null;
                set.Add(one);
                continue;
            }
            var left = part[..dash];
            var right = part[(dash + 1)..];
            if (!(left.Length == 0 || int.TryParse(left, out _)) || !(right.Length == 0 || int.TryParse(right, out _))) return null;
            var from = left.Length == 0 ? 1 : int.Parse(left);
            var to = right.Length == 0 ? count : int.Parse(right);
            if (from > to) (from, to) = (to, from);
            for (var n = Math.Max(1, from); n <= Math.Min(to, count); n++) set.Add(n);
        }
        return set;
    }

    public static bool IsValidRange(string text) => string.IsNullOrWhiteSpace(text) || ParseRange(text, 1) is not null;

    // ---- Grid layouts: one page per sheet, N-up and join ----

    private static List<Sheet> Grid(List<Item> items, PrintSettings s)
    {
        int rows = 1, cols = 1;
        if (s.Mode == LayoutMode.NUp) { rows = Math.Clamp(s.Rows, 1, 12); cols = Math.Clamp(s.Columns, 1, 12); }
        else if (s.Mode == LayoutMode.Join) { cols = Math.Clamp(s.JoinCount, 1, 12); }
        var perSheet = rows * cols;

        var sheets = new List<Sheet>();
        for (var start = 0; start < items.Count; start += perSheet)
        {
            var cells = new Item?[perSheet];
            for (var k = 0; k < perSheet && start + k < items.Count; k++)
            {
                var (r, c) = CellOf(k, rows, cols, s.Order);
                cells[r * cols + c] = items[start + k];
            }
            var join = s.Mode == LayoutMode.Join;
            sheets.Add(LayOut(cells, rows, cols, s, join ? 0 : Paper.Mm(s.GutterXMm), join ? 0 : Paper.Mm(s.GutterYMm), join, sourceSized: s.Mode == LayoutMode.Normal && s.UseSourceSize));
        }
        return sheets;
    }

    private static (int Row, int Col) CellOf(int k, int rows, int cols, PageOrder order) => order switch
    {
        PageOrder.LeftThenDown => (k / cols, cols - 1 - k % cols),
        PageOrder.DownThenRight => (k % rows, k / rows),
        PageOrder.DownThenLeft => (k % rows, cols - 1 - k / rows),
        _ => (k / cols, k % cols),
    };

    // ---- Booklet ----

    private static List<Sheet> Booklet(List<Item> items, PrintSettings s)
    {
        var vertical = s.BookletAxis == BookletAxis.Vertical;
        var sheets = new List<Sheet>();
        if (items.Count == 0) return sheets;

        var perFold = s.SheetsPerFold > 0 ? s.SheetsPerFold * 4 : int.MaxValue;
        for (var start = 0; start < items.Count; start += perFold)
        {
            var chunk = items.Skip(start).Take(perFold).ToList();
            var padded = (chunk.Count + 3) / 4 * 4;
            Item? At(int i) => i < chunk.Count ? chunk[i] : null;

            for (var k = 0; k < padded / 4; k++)
            {
                // Each sheet carries four pages: outer pair on the front, inner pair on the back.
                var front = new[] { At(padded - 1 - 2 * k), At(2 * k) };
                var back = new[] { At(2 * k + 1), At(padded - 2 - 2 * k) };
                foreach (var (pair, isBack) in new[] { (front, false), (back, true) })
                {
                    var cells = s.Binding == BookletBinding.Right ? [pair[1], pair[0]] : pair;
                    var sheet = LayOut(cells, vertical ? 2 : 1, vertical ? 1 : 2, s, Paper.Mm(s.GutterXMm), Paper.Mm(s.GutterYMm), false, false, booklet: true);
                    sheet.IsBack = isBack;
                    sheets.Add(sheet);
                }
            }
        }
        return sheets;
    }

    // ---- Poster ----

    private static List<Sheet> Poster(List<Item> items, PrintSettings s)
    {
        var sheets = new List<Sheet>();
        var nx = Math.Clamp(s.PosterColumns, 1, 12);
        var ny = Math.Clamp(s.PosterRows, 1, 12);
        var overlap = Paper.Mm(s.PosterOverlapMm);

        foreach (var item in items)
        {
            var (pw, ph) = Paper.Points(s);
            var landscape = s.Orientation == Orientation.Landscape;
            var rot = item.Page.Rotation;
            var vis = item.Page.VisibleSize(rot, item.Crop);

            if (s.Orientation == Orientation.Auto)
            {
                // Pick the paper direction that lets the poster use more of the page.
                var portraitFit = PosterScale(vis, pw, ph, s, nx, ny, overlap);
                var landscapeFit = PosterScale(vis, ph, pw, s, nx, ny, overlap);
                landscape = landscapeFit > portraitFit * 1.001f;
            }
            var w = landscape ? ph : pw;
            var h = landscape ? pw : ph;

            float ml = Paper.Mm(s.MarginLeftMm), mt = Paper.Mm(s.MarginTopMm), mr = Paper.Mm(s.MarginRightMm), mb = Paper.Mm(s.MarginBottomMm);
            var tw = Math.Max(10, w - ml - mr);
            var th = Math.Max(10, h - mt - mb);
            var step = (X: Math.Max(5, tw - overlap), Y: Math.Max(5, th - overlap));
            var posterW = tw + (nx - 1) * step.X;
            var posterH = th + (ny - 1) * step.Y;

            var scale = PosterScale(vis, w, h, s, nx, ny, overlap);
            var (ax, ay) = Geometry.AnchorFactors(s.ContentAnchor);
            var pageRect = new RectangleF((posterW - vis.Width * scale) * ax, (posterH - vis.Height * scale) * ay, vis.Width * scale, vis.Height * scale);

            for (var j = 0; j < ny; j++)
            for (var i = 0; i < nx; i++)
            {
                var region = new RectangleF(i * step.X, j * step.Y, tw, th);
                var inter = RectangleF.Intersect(region, pageRect);
                if (inter.Width < 0.5f || inter.Height < 0.5f) continue;

                // The tile's slice of the visible page, then back to the unrotated source and through the user's crop.
                var visWindow = new RectangleF((inter.X - pageRect.X) / pageRect.Width, (inter.Y - pageRect.Y) / pageRect.Height, inter.Width / pageRect.Width, inter.Height / pageRect.Height);
                var unrot = Geometry.UnrotateRect(visWindow, rot);
                var window = new RectangleF(
                    item.Crop.X + unrot.X * item.Crop.Width, item.Crop.Y + unrot.Y * item.Crop.Height,
                    unrot.Width * item.Crop.Width, unrot.Height * item.Crop.Height);

                var sheet = new Sheet { Width = w, Height = h, Label = $"Tile {j + 1}-{i + 1}" };
                sheet.Placements.Add(new Placement
                {
                    Page = item.Page, Number = item.Number, Rotation = rot, Window = window, Scale = scale,
                    Dest = new RectangleF(ml + inter.X - region.X, mt + inter.Y - region.Y, inter.Width, inter.Height),
                    Clip = new RectangleF(ml, mt, tw, th),
                });
                if (s.PosterMarks) AddCropMarks(sheet, new RectangleF(ml, mt, tw, th), 3, Math.Min(10, Math.Min(ml, mt) * 0.8f));
                if (s.PosterLabels)
                    sheet.Front.Add(new TextOverlay($"{item.Page.Job.Name}  ·  row {j + 1}/{ny}  column {i + 1}/{nx}", "Segoe UI", 7, false, false, "#555555", 1, 0,
                        new RectangleF(ml, 2, tw, Math.Max(8, mt - 2)), Anchor.Left, 0, 0, false, null));
                sheets.Add(sheet);
            }
        }
        return sheets;
    }

    private static float PosterScale(SizeF vis, float w, float h, PrintSettings s, int nx, int ny, float overlap)
    {
        var tw = Math.Max(10, w - Paper.Mm(s.MarginLeftMm) - Paper.Mm(s.MarginRightMm));
        var th = Math.Max(10, h - Paper.Mm(s.MarginTopMm) - Paper.Mm(s.MarginBottomMm));
        var posterW = tw + (nx - 1) * Math.Max(5, tw - overlap);
        var posterH = th + (ny - 1) * Math.Max(5, th - overlap);
        if (s.Scale == ScaleMode.Actual) return 1;
        if (s.Scale == ScaleMode.Custom) return (float)(Math.Max(1, s.ScalePercent) / 100);
        return Math.Min(posterW / vis.Width, posterH / vis.Height);
    }

    // ---- Placing pages into cells ----

    /// <summary>Lays out one sheet. With automatic orientation both paper directions are tried and the one that gives bigger pages wins.</summary>
    private static Sheet LayOut(Item?[] cells, int rows, int cols, PrintSettings s, float gutterX, float gutterY, bool join, bool sourceSized, bool booklet = false)
    {
        if (sourceSized && cells[0] is { } only)
        {
            var vis = only.Page.VisibleSize(only.Page.Rotation, only.Crop);
            var sheetW = vis.Width + Paper.Mm(s.MarginLeftMm) + Paper.Mm(s.MarginRightMm);
            var sheetH = vis.Height + Paper.Mm(s.MarginTopMm) + Paper.Mm(s.MarginBottomMm);
            return Fill(cells, rows, cols, s, sheetW, sheetH, gutterX, gutterY, out _);
        }

        var (pw, ph) = Paper.Points(s);
        var orientation = s.Orientation;
        if (orientation == Orientation.Auto)
        {
            if (booklet) orientation = s.BookletAxis == BookletAxis.Vertical ? Orientation.Portrait : Orientation.Landscape;
            else if (join) orientation = Orientation.Landscape;
        }

        if (orientation == Orientation.Portrait) return Fill(cells, rows, cols, s, pw, ph, gutterX, gutterY, out _);
        if (orientation == Orientation.Landscape) return Fill(cells, rows, cols, s, ph, pw, gutterX, gutterY, out _);

        var portrait = Fill(cells, rows, cols, s, pw, ph, gutterX, gutterY, out var portraitArea);
        var landscape = Fill(cells, rows, cols, s, ph, pw, gutterX, gutterY, out var landscapeArea);
        return landscapeArea > portraitArea * 1.02f ? landscape : portrait;
    }

    private static Sheet Fill(Item?[] cells, int rows, int cols, PrintSettings s, float w, float h, float gutterX, float gutterY, out float contentArea)
    {
        var sheet = new Sheet { Width = w, Height = h };
        float ml = Paper.Mm(s.MarginLeftMm), mt = Paper.Mm(s.MarginTopMm), mr = Paper.Mm(s.MarginRightMm), mb = Paper.Mm(s.MarginBottomMm);
        var areaW = Math.Max(10, w - ml - mr);
        var areaH = Math.Max(10, h - mt - mb);
        var cellW = Math.Max(1, (areaW - (cols - 1) * gutterX) / cols);
        var cellH = Math.Max(1, (areaH - (rows - 1) * gutterY) / rows);

        contentArea = 0;
        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
        {
            if (cells[r * cols + c] is not { } item) continue;
            var cell = new RectangleF(ml + c * (cellW + gutterX), mt + r * (cellH + gutterY), cellW, cellH);
            var placement = Place(item, cell, s);
            sheet.Placements.Add(placement);
            contentArea += placement.Dest.Width * placement.Dest.Height;
        }
        return sheet;
    }

    private static Placement Place(Item item, RectangleF cell, PrintSettings s)
    {
        var page = item.Page;
        var rot = page.Rotation;
        var vis = page.VisibleSize(rot, item.Crop);

        if (s.AutoRotate && s.Scale is ScaleMode.Fit or ScaleMode.ShrinkOnly or ScaleMode.Fill)
        {
            var turned = page.VisibleSize((rot + 90) % 360, item.Crop);
            if (FitScale(turned, cell) > FitScale(vis, cell) * 1.05f) { rot = (rot + 90) % 360; vis = turned; }
        }

        var fit = FitScale(vis, cell);
        var scale = s.Scale switch
        {
            ScaleMode.ShrinkOnly => Math.Min(1, fit),
            ScaleMode.Fill => Math.Max(cell.Width / vis.Width, cell.Height / vis.Height),
            ScaleMode.Actual => 1,
            ScaleMode.Custom => (float)(Math.Max(1, s.ScalePercent) / 100),
            _ => fit,
        };

        var dw = vis.Width * scale;
        var dh = vis.Height * scale;
        var (ax, ay) = Geometry.AnchorFactors(s.ContentAnchor);
        return new Placement
        {
            Page = page, Number = item.Number, Rotation = rot, Window = item.Crop, Scale = scale,
            Dest = new RectangleF(cell.X + (cell.Width - dw) * ax, cell.Y + (cell.Height - dh) * ay, dw, dh),
            Clip = cell,
        };
    }

    private static float FitScale(SizeF visible, RectangleF cell) => Math.Min(cell.Width / visible.Width, cell.Height / visible.Height);

    // ---- Marks, borders, annotations, letterhead ----

    private static void Decorate(List<Sheet> sheets, List<Item> items, PrintSettings s, LayoutHooks hooks)
    {
        var duplex = s.Duplex != DuplexMode.OneSided || s.Mode == LayoutMode.Booklet;
        var total = items.Count;
        var macros = new MacroContext(total, sheets.Count, hooks.Now);

        for (var i = 0; i < sheets.Count; i++)
        {
            var sheet = sheets[i];
            sheet.Index = i;
            sheet.Physical = duplex ? i / 2 + 1 : i + 1;
            if (duplex && s.Mode != LayoutMode.Booklet) sheet.IsBack = i % 2 == 1;
            sheet.FlipHorizontal = s.FlipHorizontal;
            sheet.FlipVertical = s.FlipVertical;

            if (!string.IsNullOrWhiteSpace(s.FormPath) && (!s.FormFirstSheetOnly || i == 0))
                sheet.Behind.Add(new FormOverlay(s.FormPath));

            foreach (var p in sheet.Placements)
            {
                foreach (var a in p.Page.Annotations) AddAnnotation(sheet, p, a);
                if (s.PageBorders)
                    sheet.Front.Add(new RectOverlay(p.Dest, null, 0, "#000000", (float)s.BorderWidthPt, p.Clip));
                if (s.CropMarks && s.Mode != LayoutMode.Poster)
                    AddCropMarks(sheet, p.Dest, 3, 9);
            }

            foreach (var mark in s.Watermarks.Where(m => m.Enabled))
                AddWatermark(sheet, mark, i, macros);
        }
    }

    private static void AddAnnotation(Sheet sheet, Placement p, Annotation a)
    {
        var rect = p.MapRect(a.Rect);
        switch (a.Kind)
        {
            case AnnotationKind.Redact:
                sheet.Front.Add(new RectOverlay(rect, "#000000", 1, null, 0, p.Clip));
                break;
            case AnnotationKind.Highlight:
                sheet.Front.Add(new RectOverlay(rect, a.Color, 0.4, null, 0, p.Clip));
                break;
            case AnnotationKind.Picture when a.Text.Length > 0:
                sheet.Front.Add(new PictureOverlay(a.Text, rect, p.Rotation, p.Clip));
                break;
            case AnnotationKind.Text when a.Text.Length > 0:
                sheet.Front.Add(new TextOverlay(a.Text, "Segoe UI", Math.Max(2, a.FontSize * p.Page.Height * p.Scale), false, false, a.Color, 1, 0,
                    rect, Anchor.TopLeft, 0, 0, false, p.Clip));
                break;
        }
    }

    private static void AddCropMarks(Sheet sheet, RectangleF r, float gap, float length)
    {
        if (length < 2) return;
        foreach (var x in new[] { r.Left, r.Right })
        foreach (var y in new[] { r.Top, r.Bottom })
        {
            var dx = x == r.Left ? -1 : 1;
            var dy = y == r.Top ? -1 : 1;
            sheet.Front.Add(new LineOverlay(new PointF(x + dx * gap, y), new PointF(x + dx * (gap + length), y), "#000000", 0.4f));
            sheet.Front.Add(new LineOverlay(new PointF(x, y + dy * gap), new PointF(x, y + dy * (gap + length)), "#000000", 0.4f));
        }
    }

    private static void AddWatermark(Sheet sheet, Watermark mark, int sheetIndex, MacroContext macros)
    {
        var offX = Paper.Mm(mark.OffsetXMm);
        var offY = Paper.Mm(mark.OffsetYMm);

        void Add(RectangleF bounds, int number, string fileName, RectangleF? clip)
        {
            if (mark.Kind == WatermarkKind.Image)
            {
                if (mark.ImagePath.Length > 0)
                    sheet.Front.Add(new ImageOverlay(mark.ImagePath, bounds, mark.Anchor, offX, offY, mark.ImageWidth, mark.Opacity, mark.Angle, mark.Tile));
                return;
            }
            var text = macros.Expand(mark.Text, number, sheetIndex + 1, fileName);
            if (text.Length == 0) return;
            sheet.Front.Add(new TextOverlay(text, mark.FontFamily, mark.FontSize, mark.Bold, mark.Italic, mark.Color, mark.Opacity, mark.Angle,
                bounds, mark.Anchor, offX, offY, mark.Tile, clip));
        }

        if (mark.Scope == WatermarkScope.PerSheet)
        {
            if (!Matches(mark.Pages, sheetIndex + 1, macros.Sheets)) return;
            var first = sheet.Placements.FirstOrDefault();
            Add(new RectangleF(0, 0, sheet.Width, sheet.Height), first?.Number ?? sheetIndex + 1, first?.Page.Job.Name ?? "", null);
            return;
        }

        foreach (var p in sheet.Placements)
        {
            if (!Matches(mark.Pages, p.Number, macros.Pages)) continue;
            Add(p.Dest, p.Number, p.Page.Job.Name, p.Clip);
        }
    }

    private static bool Matches(WatermarkPages filter, int number, int total) => filter switch
    {
        WatermarkPages.First => number == 1,
        WatermarkPages.Last => number == total,
        WatermarkPages.Odd => number % 2 == 1,
        WatermarkPages.Even => number % 2 == 0,
        _ => true,
    };

    private sealed class MacroContext(int pages, int sheets, DateTime now)
    {
        public int Pages { get; } = pages;
        public int Sheets { get; } = sheets;

        public string Expand(string text, int page, int sheet, string file)
        {
            if (!text.Contains('{')) return text;
            return text
                .Replace("{page}", page.ToString())
                .Replace("{pages}", Pages.ToString())
                .Replace("{sheet}", sheet.ToString())
                .Replace("{sheets}", Sheets.ToString())
                .Replace("{file}", file)
                .Replace("{date}", now.ToString("d"))
                .Replace("{time}", now.ToString("t"))
                .Replace("{user}", Environment.UserName)
                .Replace("{computer}", Environment.MachineName);
        }
    }
}
