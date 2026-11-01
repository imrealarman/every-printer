using System.Drawing;
using System.Drawing.Imaging;
using EveryPrinter.Models;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace EveryPrinter.Services;

public sealed class PasswordRequiredException(string path) : Exception($"“{System.IO.Path.GetFileName(path)}” is password protected.")
{
    public string Path { get; } = path;
}

/// <summary>
/// Opens PDFs, images and text files into pages, and renders any page (or part of a page) to a bitmap on request.
/// PDFs are drawn by the PDF reader built into Windows, so nothing extra is installed and rendering is fast.
/// </summary>
public sealed class PageSource
{
    public static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic"];
    public static readonly string[] TextExtensions = [".txt", ".log", ".md", ".csv"];

    private const int MaxPixels = 48_000_000;
    private const int MaxSide = 12_000;
    private const int LinesPerTextPage = 62;
    private const int TextWrap = 96;

    private readonly Dictionary<int, PdfDocument> pdfs = [];
    private readonly Dictionary<string, (SourceJob Job, PageRef Page)> forms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Bitmap> pasted = [];
    private readonly SemaphoreSlim renderGate = new(3);
    private readonly object gate = new();

    public PageCache Cache { get; } = new(384L * 1024 * 1024);

    public bool IsSupported(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext == ".pdf" || ImageExtensions.Contains(ext) || TextExtensions.Contains(ext);
    }

    public async Task<List<PageRef>> OpenAsync(DocumentSet document, string path, string? password = null)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var name = System.IO.Path.GetFileName(path);
        if (ext == ".pdf") return await OpenPdfAsync(document, path, name, password);
        if (ImageExtensions.Contains(ext)) return await Task.Run(() => OpenImage(document, path, name));
        if (TextExtensions.Contains(ext)) return await Task.Run(() => OpenText(document, path, name));
        throw new InvalidOperationException($"Every Printer can't open {ext} files yet.");
    }

    private async Task<List<PageRef>> OpenPdfAsync(DocumentSet document, string path, string name, string? password)
    {
        PdfDocument pdf;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            pdf = password is null ? await PdfDocument.LoadFromFileAsync(file) : await PdfDocument.LoadFromFileAsync(file, password);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x8007052B) || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new PasswordRequiredException(path);
        }
        catch (Exception ex)
        {
            Log.Error("Open PDF", ex);
            throw new InvalidOperationException($"Could not open “{name}”. It may be damaged.");
        }
        if (pdf.IsPasswordProtected && password is null) throw new PasswordRequiredException(path);

        var job = document.NewJob(name, path, JobKind.Pdf);
        job.PageCount = (int)pdf.PageCount;
        lock (gate) pdfs[job.Id] = pdf;

        // Page sizes are read on a worker thread: for a very large file this is the slowest part of opening it.
        return await Task.Run(() =>
        {
            var pages = new List<PageRef>((int)pdf.PageCount);
            var last = new Windows.Foundation.Size(595 * 96 / 72, 842 * 96 / 72);
            for (uint i = 0; i < pdf.PageCount; i++)
            {
                try
                {
                    using var page = pdf.GetPage(i);
                    last = page.Size;
                }
                catch (Exception ex) { Log.Error($"Page {i + 1} size of {name}", ex); }
                pages.Add(new PageRef { Job = job, Index = (int)i, Width = (float)(last.Width * 0.75), Height = (float)(last.Height * 0.75) });
            }
            return pages;
        });
    }

    private List<PageRef> OpenImage(DocumentSet document, string path, string name)
    {
        using var image = Image.FromFile(path);
        var frames = 1;
        var dimension = new FrameDimension(image.FrameDimensionsList[0]);
        if (path.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
            frames = image.GetFrameCount(dimension);

        var job = document.NewJob(name, path, JobKind.Image);
        job.PageCount = frames;
        var pages = new List<PageRef>(frames);
        for (var f = 0; f < frames; f++)
        {
            if (frames > 1) image.SelectActiveFrame(dimension, f);
            var (w, h) = OrientedSize(image);
            var (width, height) = PointSize(w, h, image.HorizontalResolution);
            pages.Add(new PageRef { Job = job, Index = f, Width = width, Height = height });
        }
        return pages;
    }

    /// <summary>Adds a picture from the clipboard or a screenshot as a one-page job.</summary>
    public List<PageRef> AddBitmap(DocumentSet document, Bitmap bitmap, string name)
    {
        var job = document.NewJob(name, "", JobKind.Image);
        job.PageCount = 1;
        lock (gate) pasted[job.Id] = bitmap;
        var (w, h) = PointSize(bitmap.Width, bitmap.Height, 96);
        return [new PageRef { Job = job, Index = 0, Width = w, Height = h }];
    }

    private static (float W, float H) PointSize(int pxW, int pxH, float dpi)
    {
        if (dpi < 20 || dpi > 1200) dpi = 96;
        float w = pxW * 72f / dpi, h = pxH * 72f / dpi;
        // Photos straight from a camera claim to be a couple of metres wide; keep them within roughly a poster.
        var limit = 1700f / Math.Max(w, h);
        return limit < 1 ? (w * limit, h * limit) : (w, h);
    }

    private static (int W, int H) OrientedSize(Image image)
    {
        var swap = ExifOrientation(image) is 5 or 6 or 7 or 8;
        return swap ? (image.Height, image.Width) : (image.Width, image.Height);
    }

    private static int ExifOrientation(Image image)
    {
        try { return image.PropertyIdList.Contains(0x0112) ? image.GetPropertyItem(0x0112)!.Value![0] : 1; }
        catch (ArgumentException) { return 1; }
    }

    private List<PageRef> OpenText(DocumentSet document, string path, string name)
    {
        var lines = new List<string>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Replace("\t", "    ");
            if (line.Length == 0) { lines.Add(""); continue; }
            for (var i = 0; i < line.Length; i += TextWrap) lines.Add(line.Substring(i, Math.Min(TextWrap, line.Length - i)));
        }
        var pageTexts = new List<string>();
        for (var i = 0; i < Math.Max(1, lines.Count); i += LinesPerTextPage)
            pageTexts.Add(string.Join("\n", lines.Skip(i).Take(LinesPerTextPage)));

        var job = document.NewJob(name, path, JobKind.Text);
        job.PageCount = pageTexts.Count;
        job.TextPages = pageTexts.ToArray();
        return pageTexts.Select((_, i) => new PageRef { Job = job, Index = i, Width = 595, Height = 842 }).ToList();
    }

    /// <summary>The first page of a letterhead or form file, kept apart from the document's own pages.</summary>
    public PageRef? FormPage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        lock (gate)
        {
            if (forms.TryGetValue(path, out var known)) return known.Page;
        }
        try
        {
            var scratch = new DocumentSet();
            var pages = OpenAsync(scratch, path).GetAwaiter().GetResult();
            if (pages.Count == 0) return null;
            lock (gate) forms[path] = (pages[0].Job, pages[0]);
            return pages[0];
        }
        catch (Exception ex)
        {
            Log.Error("Open form", ex);
            return null;
        }
    }

    public void Forget(SourceJob job)
    {
        lock (gate)
        {
            pdfs.Remove(job.Id);
            if (pasted.Remove(job.Id, out var bitmap)) bitmap.Dispose();
        }
        Cache.Remove($"{job.Id}:");
    }

    public bool IsPdf(PageRef page) => page.Job.Kind == JobKind.Pdf;

    // ---- Rendering ----

    /// <summary>
    /// Renders the part of a page named by <paramref name="window"/> (fractions of the unrotated page) at about the given pixel size.
    /// Block until done: call it from a worker thread. Blank pages have nothing to draw and return null.
    /// </summary>
    public BitmapLease? Render(PageRef page, RectangleF window, int pxW, int pxH)
    {
        if (page.Job.Kind == JobKind.Blank) return null;
        pxW = Math.Clamp(pxW, 8, MaxSide);
        pxH = Math.Clamp(pxH, 8, MaxSide);
        if ((long)pxW * pxH > MaxPixels)
        {
            var shrink = Math.Sqrt(MaxPixels / ((double)pxW * pxH));
            pxW = Math.Max(8, (int)(pxW * shrink));
            pxH = Math.Max(8, (int)(pxH * shrink));
        }

        var job = page.Job;
        switch (job.Kind)
        {
            case JobKind.Pdf:
            {
                // Quantise the size so small zoom changes reuse the same picture.
                pxW = Quantise(pxW);
                pxH = Math.Max(8, (int)Math.Round(pxW * (window.Height * page.Height) / (window.Width * page.Width)));
                var key = $"{job.Id}:{page.Index}:pdf:{window.X:F4},{window.Y:F4},{window.Width:F4},{window.Height:F4}:{pxW}x{pxH}";
                return Cache.Lease(key, () => RenderPdf(page, window, pxW, pxH), b => new RectangleF(0, 0, b.Width, b.Height));
            }
            case JobKind.Image:
            {
                var bucket = ImageBucket(Math.Max(pxW / Math.Max(window.Width, 0.01f), pxH / Math.Max(window.Height, 0.01f)));
                var key = $"{job.Id}:{page.Index}:img:{bucket}";
                return Cache.Lease(key, () => LoadImage(page, bucket),
                    b => new RectangleF(window.X * b.Width, window.Y * b.Height, window.Width * b.Width, window.Height * b.Height));
            }
            default:
            {
                pxW = Quantise(Math.Max(8, (int)(pxW / Math.Max(window.Width, 0.01f))));
                var key = $"{job.Id}:{page.Index}:txt:{pxW}";
                return Cache.Lease(key, () => RenderText(page, pxW),
                    b => new RectangleF(window.X * b.Width, window.Y * b.Height, window.Width * b.Width, window.Height * b.Height));
            }
        }
    }

    private static int Quantise(int px)
    {
        // Steps of about 12 %: close enough that the picture never looks soft, coarse enough to be reused.
        var step = Math.Max(16, (int)Math.Pow(2, Math.Floor(Math.Log2(Math.Max(px, 16))) - 3.5));
        return (px + step - 1) / step * step;
    }

    private static int ImageBucket(float fullWidthPx) => (int)Math.Min(MaxSide, Quantise((int)Math.Max(64, fullWidthPx)));

    private Bitmap RenderPdf(PageRef page, RectangleF window, int pxW, int pxH)
    {
        PdfDocument pdf;
        lock (gate) pdf = pdfs[page.Job.Id];

        renderGate.Wait();
        try
        {
            using var pdfPage = pdf.GetPage((uint)page.Index);
            var size = pdfPage.Size;
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)pxW,
                DestinationHeight = (uint)pxH,
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                BitmapEncoderId = BitmapEncoder.BmpEncoderId,
            };
            if (window.X > 0.0005f || window.Y > 0.0005f || window.Width < 0.999f || window.Height < 0.999f)
                options.SourceRect = new Windows.Foundation.Rect(window.X * size.Width, window.Y * size.Height, window.Width * size.Width, window.Height * size.Height);

            using var stream = new InMemoryRandomAccessStream();
            pdfPage.RenderToStreamAsync(stream, options).AsTask().GetAwaiter().GetResult();
            stream.Seek(0);
            using var net = new MemoryStream();
            System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(stream).CopyTo(net);
            net.Position = 0;
            using var decoded = new Bitmap(net);
            return ToPremultiplied(decoded);
        }
        finally { renderGate.Release(); }
    }

    private Bitmap LoadImage(PageRef page, int maxWidthPx)
    {
        Bitmap source;
        var owned = false;
        lock (gate)
        {
            if (pasted.TryGetValue(page.Job.Id, out var held)) { source = held; }
            else
            {
                using var file = Image.FromFile(page.Job.Path);
                if (page.Job.PageCount > 1) file.SelectActiveFrame(new FrameDimension(file.FrameDimensionsList[0]), page.Index);
                var rotation = ExifOrientation(file);
                source = new Bitmap(file);
                owned = true;
                ApplyExif(source, rotation);
            }
        }

        try
        {
            var scale = Math.Min(1.0, (double)maxWidthPx / Math.Max(1, source.Width));
            var w = Math.Max(1, (int)(source.Width * scale));
            var h = Math.Max(1, (int)(source.Height * scale));
            var result = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(result);
            g.Clear(Color.White);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(source, 0, 0, w, h);
            return result;
        }
        finally { if (owned) source.Dispose(); }
    }

    private static void ApplyExif(Bitmap bitmap, int orientation)
    {
        var flip = orientation switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.RotateNoneFlipY,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };
        if (flip != RotateFlipType.RotateNoneFlipNone) bitmap.RotateFlip(flip);
    }

    private Bitmap RenderText(PageRef page, int pxW)
    {
        var scale = pxW / 595f;
        var result = new Bitmap(pxW, (int)(842 * scale), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(result);
        g.Clear(Color.White);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.ScaleTransform(scale, scale);
        var text = page.Job.TextPages![page.Index];
        using var font = new Font("Consolas", 8.6f, FontStyle.Regular, GraphicsUnit.Point);
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces };
        var y = 48f;
        foreach (var line in text.Split('\n'))
        {
            var rtl = line.Any(c => c is >= '֐' and <= 'ࣿ');
            var f = new StringFormat(format);
            if (rtl) f.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
            g.DrawString(line, font, Brushes.Black, new RectangleF(48, y, 499, 13), f);
            y += 12.9f;
        }
        return result;
    }

    private static Bitmap ToPremultiplied(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(result);
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        g.DrawImageUnscaled(source, 0, 0);
        return result;
    }
}
