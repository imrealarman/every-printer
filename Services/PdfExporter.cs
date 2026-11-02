using System.Drawing;
using System.Drawing.Imaging;
using EveryPrinter.Models;
using Microsoft.Win32;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Security;

namespace EveryPrinter.Services;

/// <summary>
/// Writes sheets to a PDF. Sheets made only of PDF pages are written as vector pages, so text stays sharp, selectable and small;
/// anything that needs pixels (effects, redactions, images, scanned or protected PDFs, Persian marks) is written as a picture of the sheet.
/// </summary>
public static class PdfExporter
{
    public sealed record Result(int Sheets, int VectorSheets, int PictureSheets);

    public static Result Save(IReadOnlyList<Sheet> sheets, PrintSettings s, SheetRenderer renderer, string path, string title,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        EnsureFonts();
        var forms = new List<XPdfForm>();
        var streams = new List<MemoryStream>();
        var images = new List<XImage>();
        var probe = new VectorProbe();
        int vector = 0, pictures = 0;

        try
        {
            using var document = new PdfDocument();
            document.Info.Title = s.PdfTitle.Length > 0 ? s.PdfTitle : title;
            document.Info.Author = s.PdfAuthor;
            document.Info.Creator = "Every Printer";
            document.Options.CompressContentStreams = true;
            document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;

            for (var i = 0; i < sheets.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var sheet = sheets[i];
                var page = document.AddPage();
                page.Width = XUnit.FromPoint(sheet.Width);
                page.Height = XUnit.FromPoint(sheet.Height);
                using var g = XGraphics.FromPdfPage(page);

                if (CanWriteVector(sheet, s, probe))
                {
                    DrawVector(g, sheet, s, forms, images);
                    vector++;
                }
                else
                {
                    DrawPicture(g, sheet, s, renderer, streams, images, cancel);
                    pictures++;
                }
                progress?.Report((i + 1.0) / sheets.Count);
            }

            ApplySecurity(document, s);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            document.Save(path);
        }
        finally
        {
            foreach (var form in forms) form.Dispose();
            foreach (var image in images) image.Dispose();
            foreach (var stream in streams) stream.Dispose();
            probe.Dispose();
        }
        return new Result(sheets.Count, vector, pictures);
    }

    // ---- Deciding vector or picture ----

    private static bool CanWriteVector(Sheet sheet, PrintSettings s, VectorProbe probe)
    {
        if (!s.PdfKeepVector || s.HasEffects) return false;
        foreach (var p in sheet.Placements)
        {
            if (p.Page.IsBlank) continue;
            if (p.Page.Job.Kind != JobKind.Pdf || p.NeedsRaster || !probe.IsPlain(p.Page)) return false;
        }
        foreach (var overlay in sheet.Behind.Concat(sheet.Front))
        {
            switch (overlay)
            {
                case TextOverlay t when OverlayMath.NeedsShaping(t.Text): return false;
                case ImageOverlay i when i.Opacity < 0.999: return false;
                case FormOverlay f when !File.Exists(f.Path): return false;
            }
        }
        return true;
    }

    /// <summary>Checks, once per file, that PDFsharp can import its pages exactly as Windows draws them.</summary>
    private sealed class VectorProbe : IDisposable
    {
        private readonly Dictionary<string, PdfDocument?> opened = new(StringComparer.OrdinalIgnoreCase);

        public bool IsPlain(PageRef page)
        {
            var path = page.Job.Path;
            if (!opened.TryGetValue(path, out var doc))
            {
                try { doc = PdfReader.Open(path, PdfDocumentOpenMode.Import); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    doc = null;
                    page.Job.VectorOk = false; // encrypted or unusual: it will be drawn as pictures from now on
                    Log.Info($"{page.Job.Name}: written as pictures ({ex.GetType().Name})");
                }
                opened[path] = doc;
            }
            if (doc is null || page.Index >= doc.PageCount) return false;

            var pdfPage = doc.Pages[page.Index];
            if (pdfPage.Rotate != 0) return false; // Windows applies /Rotate when drawing; keep both identical by using pictures

            var media = pdfPage.MediaBox;
            var crop = pdfPage.CropBox;
            return crop.IsEmpty || (Math.Abs(crop.X1 - media.X1) < 0.5 && Math.Abs(crop.X2 - media.X2) < 0.5
                                    && Math.Abs(crop.Y1 - media.Y1) < 0.5 && Math.Abs(crop.Y2 - media.Y2) < 0.5);
        }

        public void Dispose()
        {
            foreach (var doc in opened.Values) doc?.Dispose();
        }
    }

    // ---- Vector sheets ----

    private static void DrawVector(XGraphics g, Sheet sheet, PrintSettings s, List<XPdfForm> forms, List<XImage> images)
    {
        if (sheet.FlipHorizontal || sheet.FlipVertical)
        {
            g.TranslateTransform(sheet.FlipHorizontal ? sheet.Width : 0, sheet.FlipVertical ? sheet.Height : 0);
            g.ScaleTransform(sheet.FlipHorizontal ? -1 : 1, sheet.FlipVertical ? -1 : 1);
        }

        foreach (var overlay in sheet.Behind) DrawOverlay(g, sheet, overlay, forms, images);

        foreach (var p in sheet.Placements)
        {
            if (p.Page.IsBlank) continue;
            var state = g.Save();
            g.IntersectClip(ToRect(p.Clip));
            g.TranslateTransform(p.Dest.X + p.Dest.Width / 2, p.Dest.Y + p.Dest.Height / 2);
            g.RotateTransform(p.Rotation);

            var odd = p.Rotation % 180 != 0;
            double w = odd ? p.Dest.Height : p.Dest.Width;
            double h = odd ? p.Dest.Width : p.Dest.Height;
            g.IntersectClip(new XRect(-w / 2, -h / 2, w, h));

            var fullW = w / p.Window.Width;
            var fullH = h / p.Window.Height;
            var form = XPdfForm.FromFile(p.Page.Job.Path);
            form.PageIndex = p.Page.Index;
            forms.Add(form);
            g.DrawImage(form, new XRect(-w / 2 - p.Window.X * fullW, -h / 2 - p.Window.Y * fullH, fullW, fullH));
            g.Restore(state);
        }

        foreach (var overlay in sheet.Front) DrawOverlay(g, sheet, overlay, forms, images);
    }

    private static void DrawOverlay(XGraphics g, Sheet sheet, Overlay overlay, List<XPdfForm> forms, List<XImage> images)
    {
        switch (overlay)
        {
            case RectOverlay r:
            {
                var state = g.Save();
                if (r.Clip is { } clip) g.IntersectClip(ToRect(clip));
                if (r.Fill is not null) g.DrawRectangle(new XSolidBrush(ToColor(OverlayMath.ParseColor(r.Fill, r.FillOpacity))), ToRect(r.Rect));
                if (r.Stroke is not null && r.StrokeWidth > 0)
                    g.DrawRectangle(new XPen(ToColor(OverlayMath.ParseColor(r.Stroke)), r.StrokeWidth), ToRect(r.Rect));
                g.Restore(state);
                break;
            }
            case LineOverlay l:
                g.DrawLine(new XPen(ToColor(OverlayMath.ParseColor(l.Color)), l.Width), l.From.X, l.From.Y, l.To.X, l.To.Y);
                break;
            case TextOverlay t:
                DrawText(g, t);
                break;
            case ImageOverlay i:
                DrawImageOverlay(g, i, images);
                break;
            case PictureOverlay pic:
                DrawPicture(g, pic, images);
                break;
            case FormOverlay f:
                DrawForm(g, sheet, f, forms, images);
                break;
        }
    }

    private static void DrawText(XGraphics g, TextOverlay t)
    {
        var style = (t.Bold ? XFontStyleEx.Bold : XFontStyleEx.Regular) | (t.Italic ? XFontStyleEx.Italic : XFontStyleEx.Regular);
        var font = new XFont(t.Font, Math.Max(1, t.Size), style, new XPdfFontOptions(PdfFontEncoding.Unicode));
        var size = g.MeasureString(t.Text, font);
        var brush = new XSolidBrush(ToColor(OverlayMath.ParseColor(t.Color, t.Opacity)));

        var state = g.Save();
        if (t.Clip is { } clip) g.IntersectClip(ToRect(clip));
        foreach (var centre in OverlayMath.Centres(t.Bounds, t.Anchor, t.OffsetX, t.OffsetY, (float)size.Width, (float)size.Height, t.Angle, t.Tile))
        {
            var inner = g.Save();
            g.TranslateTransform(centre.X, centre.Y);
            g.RotateTransform(t.Angle);
            g.DrawString(t.Text, font, brush, -size.Width / 2, -size.Height / 2, XStringFormats.TopLeft);
            g.Restore(inner);
        }
        g.Restore(state);
    }

    private static void DrawImageOverlay(XGraphics g, ImageOverlay o, List<XImage> images)
    {
        if (!File.Exists(o.Path)) return;
        var image = XImage.FromFile(o.Path);
        images.Add(image);
        var w = o.WidthFraction * o.Bounds.Width;
        var h = w * image.PixelHeight / image.PixelWidth;
        foreach (var centre in OverlayMath.Centres(o.Bounds, o.Anchor, o.OffsetX, o.OffsetY, (float)w, (float)h, o.Angle, o.Tile))
        {
            var inner = g.Save();
            g.TranslateTransform(centre.X, centre.Y);
            g.RotateTransform(o.Angle);
            g.DrawImage(image, -w / 2, -h / 2, w, h);
            g.Restore(inner);
        }
    }

    private static void DrawPicture(XGraphics g, PictureOverlay o, List<XImage> images)
    {
        if (!File.Exists(o.Path)) return;
        var image = XImage.FromFile(o.Path);
        images.Add(image);
        var odd = o.Rotation % 180 != 0;
        double w = odd ? o.Rect.Height : o.Rect.Width, h = odd ? o.Rect.Width : o.Rect.Height;
        var state = g.Save();
        if (o.Clip is { } clip) g.IntersectClip(ToRect(clip));
        g.TranslateTransform(o.Rect.X + o.Rect.Width / 2.0, o.Rect.Y + o.Rect.Height / 2.0);
        g.RotateTransform(o.Rotation);
        g.DrawImage(image, -w / 2, -h / 2, w, h);
        g.Restore(state);
    }

    private static void DrawForm(XGraphics g, Sheet sheet, FormOverlay f, List<XPdfForm> forms, List<XImage> images)
    {
        if (f.Path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var form = XPdfForm.FromFile(f.Path);
            forms.Add(form);
            var scale = Math.Min(sheet.Width / form.PointWidth, sheet.Height / form.PointHeight);
            double w = form.PointWidth * scale, h = form.PointHeight * scale;
            g.DrawImage(form, (sheet.Width - w) / 2, (sheet.Height - h) / 2, w, h);
        }
        else
        {
            var image = XImage.FromFile(f.Path);
            images.Add(image);
            var scale = Math.Min(sheet.Width / image.PointWidth, sheet.Height / image.PointHeight);
            double w = image.PointWidth * scale, h = image.PointHeight * scale;
            g.DrawImage(image, (sheet.Width - w) / 2, (sheet.Height - h) / 2, w, h);
        }
    }

    // ---- Picture sheets ----

    private static void DrawPicture(XGraphics g, Sheet sheet, PrintSettings s, SheetRenderer renderer, List<MemoryStream> streams, List<XImage> images, CancellationToken cancel)
    {
        using var bitmap = renderer.Render(sheet, s, Math.Clamp(s.PdfDpi, 72, 600) / 72f, cancel);
        var stream = new MemoryStream();
        if (s.PdfJpegQuality >= 100)
        {
            bitmap.Save(stream, ImageFormat.Png);
        }
        else
        {
            var encoder = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(s.PdfJpegQuality, 20, 99));
            bitmap.Save(stream, encoder, parameters);
        }
        stream.Position = 0;
        streams.Add(stream);
        var image = XImage.FromStream(stream);
        images.Add(image);
        g.DrawImage(image, 0, 0, sheet.Width, sheet.Height);
    }

    // ---- Security and fonts ----

    private static void ApplySecurity(PdfDocument document, PrintSettings s)
    {
        var restricted = !s.PdfAllowPrint || !s.PdfAllowCopy || !s.PdfAllowEdit;
        if (s.PdfUserPassword.Length == 0 && !restricted) return;

        var security = document.SecuritySettings;
        security.UserPassword = s.PdfUserPassword;
        // Permissions only hold with an owner password; without one the user's own is reused or a random one is made.
        security.OwnerPassword = s.PdfOwnerPassword.Length > 0 ? s.PdfOwnerPassword : Guid.NewGuid().ToString("N");
        security.PermitPrint = s.PdfAllowPrint;
        security.PermitFullQualityPrint = s.PdfAllowPrint;
        security.PermitExtractContent = s.PdfAllowCopy;
        security.PermitModifyDocument = s.PdfAllowEdit;
        security.PermitAnnotations = s.PdfAllowEdit;
        security.PermitAssembleDocument = s.PdfAllowEdit;
        security.PermitFormsFill = s.PdfAllowEdit;
    }

    private static XRect ToRect(RectangleF r) => new(r.X, r.Y, r.Width, r.Height);

    private static XColor ToColor(Color c) => XColor.FromArgb(c.A, c.R, c.G, c.B);

    private static bool fontsReady;

    public static void EnsureFonts()
    {
        if (fontsReady) return;
        GlobalFontSettings.FontResolver ??= new WindowsFontResolver();
        fontsReady = true;
    }

    /// <summary>Finds TrueType font files for PDFsharp through the list of installed fonts in the registry.</summary>
    private sealed class WindowsFontResolver : IFontResolver
    {
        private readonly Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);

        public WindowsFontResolver()
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                {
                    if (key.GetValue(name) is not string file || !file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)) continue;
                    var full = Path.IsPathRooted(file) ? file : Path.Combine(fonts, file);
                    files[name.Replace(" (TrueType)", "", StringComparison.OrdinalIgnoreCase).Trim()] = full;
                }
            }
        }

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            var wanted = familyName + (isBold ? " Bold" : "") + (isItalic ? " Italic" : "");
            foreach (var candidate in new[] { wanted, familyName + (isBold ? " Bold" : ""), familyName, "Segoe UI", "Arial" })
                if (files.ContainsKey(candidate)) return new FontResolverInfo(candidate);
            return new FontResolverInfo(files.Keys.FirstOrDefault() ?? "Arial");
        }

        public byte[]? GetFont(string faceName) => files.TryGetValue(faceName, out var path) && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
