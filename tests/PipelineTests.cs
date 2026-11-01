using System.Drawing;
using EveryPrinter.Models;
using EveryPrinter.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace EveryPrinter.Tests;

/// <summary>End to end: a real PDF goes in, sheets come out as bitmaps, PDFs and images.</summary>
public sealed class PipelineTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "everyprinter-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly PageSource source = new();

    public PipelineTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private string SamplePdf(int pages, string name = "sample.pdf")
    {
        PdfExporter.EnsureFonts();
        var path = Path.Combine(dir, name);
        using var doc = new PdfDocument();
        for (var i = 1; i <= pages; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            using var g = XGraphics.FromPdfPage(page);
            g.DrawString($"Page {i}", new XFont("Arial", 60, XFontStyleEx.Bold, new XPdfFontOptions(PdfFontEncoding.Unicode)), XBrushes.Black, 80, 200);
            g.DrawRectangle(XBrushes.Black, 100, 400, 200, 100);
        }
        doc.Save(path);
        return path;
    }

    private async Task<(DocumentSet Doc, List<PageRef> Pages)> Open(string path)
    {
        var doc = new DocumentSet();
        var pages = await source.OpenAsync(doc, path);
        doc.AddPages(pages);
        return (doc, pages);
    }

    [Fact]
    public void Writes_a_sample_document_for_manual_checks()
    {
        PdfExporter.EnsureFonts();
        var path = Path.Combine(Path.GetTempPath(), "everyprinter-sample.pdf");
        using var doc = new PdfDocument();
        for (var i = 1; i <= 8; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            using var g = XGraphics.FromPdfPage(page);
            g.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, 38, 128, 235)), 0, 0, 595, 90);
            g.DrawString($"Quarterly report - page {i}", new XFont("Arial", 26, XFontStyleEx.Bold, new XPdfFontOptions(PdfFontEncoding.Unicode)), XBrushes.White, 40, 58);
            for (var line = 0; line < 30; line++)
                g.DrawString($"Line {line + 1}: the quick brown fox jumps over the lazy dog, page {i}.", new XFont("Arial", 11, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode)), XBrushes.Black, 40, 130 + line * 22);
            g.DrawRectangle(XPens.Gray, 40, 130, 515, 660);
        }
        doc.Save(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Opens_a_pdf_with_correct_page_count_and_size()
    {
        var (_, pages) = await Open(SamplePdf(3));
        Assert.Equal(3, pages.Count);
        Assert.InRange(pages[0].Width, 594, 596);
        Assert.InRange(pages[0].Height, 841, 843);
    }

    [Fact]
    public async Task Renders_page_content_and_a_window_of_it()
    {
        var (_, pages) = await Open(SamplePdf(1));
        using var full = source.Render(pages[0], new RectangleF(0, 0, 1, 1), 300, 424);
        Assert.NotNull(full);
        Assert.True(PageAnalyzer.InkCoverage(full!.Bitmap) > 0.005);

        // The black box sits at x 100-300, y 400-500 of an 842 pt page: the window around it should be nearly solid black.
        var window = new RectangleF(100 / 595f, 400 / 842f, 200 / 595f, 100 / 842f);
        using var part = source.Render(pages[0], window, 200, 100);
        Assert.True(PageAnalyzer.InkCoverage(part!.Bitmap) > 0.9);
    }

    [Fact]
    public async Task Sheet_render_matches_the_layout()
    {
        var (doc, _) = await Open(SamplePdf(4));
        var s = new PrintSettings { Mode = LayoutMode.NUp, Rows = 2, Columns = 2 };
        var sheets = LayoutEngine.Build(doc.Pages, s);
        Assert.Single(sheets);
        using var bitmap = new SheetRenderer(source).Render(sheets[0], s, 1.5f);
        Assert.Equal((int)Math.Round(sheets[0].Width * 1.5f), bitmap.Width);
        Assert.True(PageAnalyzer.InkCoverage(bitmap) > 0.003);
        bitmap.Save(Path.Combine(Path.GetTempPath(), "everyprinter-nup.png"));
    }

    [Fact]
    public async Task Redaction_blacks_out_the_area_in_the_render()
    {
        var (doc, pages) = await Open(SamplePdf(1));
        doc.AddAnnotation(0, new Annotation { Kind = AnnotationKind.Redact, Rect = new RectangleF(0.1f, 0.2f, 0.5f, 0.1f) });
        var s = new PrintSettings().WithNoMargins();
        var sheet = LayoutEngine.Build(doc.Pages, s)[0];
        using var bitmap = new SheetRenderer(source).Render(sheet, s, 1f);
        var p = sheet.Placements[0];
        var centre = p.MapRect(new RectangleF(0.1f, 0.2f, 0.5f, 0.1f));
        var pixel = bitmap.GetPixel((int)(centre.X + centre.Width / 2), (int)(centre.Y + centre.Height / 2));
        Assert.True(pixel.R < 10 && pixel.G < 10 && pixel.B < 10, pixel.ToString());
    }

    [Fact]
    public async Task Pdf_export_keeps_pdf_sheets_as_vector_and_stays_small()
    {
        var path = SamplePdf(6);
        var (doc, _) = await Open(path);
        var s = new PrintSettings { Mode = LayoutMode.NUp, Rows = 1, Columns = 2 };
        s.Watermarks.Add(Watermark.PageNumber());
        var sheets = LayoutEngine.Build(doc.Pages, s);
        var output = Path.Combine(dir, "out.pdf");
        var result = PdfExporter.Save(sheets, s, new SheetRenderer(source), output, "test");

        Assert.Equal(3, result.Sheets);
        Assert.Equal(3, result.VectorSheets);
        using var check = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(3, check.PageCount);
        Assert.True(new FileInfo(output).Length < 200_000, new FileInfo(output).Length.ToString());
    }

    [Fact]
    public async Task Pdf_export_output_renders_back_with_content()
    {
        var (doc, _) = await Open(SamplePdf(2));
        var s = new PrintSettings { Mode = LayoutMode.NUp, Rows = 1, Columns = 2 };
        var output = Path.Combine(dir, "roundtrip.pdf");
        PdfExporter.Save(LayoutEngine.Build(doc.Pages, s), s, new SheetRenderer(source), output, "t");

        var again = new DocumentSet();
        var pages = await source.OpenAsync(again, output);
        Assert.Single(pages);
        using var lease = source.Render(pages[0], new RectangleF(0, 0, 1, 1), 600, 424);
        Assert.True(PageAnalyzer.InkCoverage(lease!.Bitmap) > 0.003);
        lease.Bitmap.Save(Path.Combine(Path.GetTempPath(), "everyprinter-roundtrip.png"));
    }

    [Fact]
    public async Task Effects_and_persian_marks_fall_back_to_pictures()
    {
        var (doc, _) = await Open(SamplePdf(2));
        var gray = new PrintSettings { Grayscale = true };
        var r1 = PdfExporter.Save(LayoutEngine.Build(doc.Pages, gray), gray, new SheetRenderer(source), Path.Combine(dir, "gray.pdf"), "t");
        Assert.Equal(2, r1.PictureSheets);

        var persian = new PrintSettings();
        persian.Watermarks.Add(new Watermark { Text = "محرمانه" });
        var r2 = PdfExporter.Save(LayoutEngine.Build(doc.Pages, persian), persian, new SheetRenderer(source), Path.Combine(dir, "fa.pdf"), "t");
        Assert.Equal(2, r2.PictureSheets);
    }

    [Fact]
    public async Task Encrypted_export_needs_a_password()
    {
        var (doc, _) = await Open(SamplePdf(1));
        var s = new PrintSettings { PdfUserPassword = "secret", PdfAllowCopy = false };
        var output = Path.Combine(dir, "locked.pdf");
        PdfExporter.Save(LayoutEngine.Build(doc.Pages, s), s, new SheetRenderer(source), output, "t");

        Assert.ThrowsAny<Exception>(() => PdfReader.Open(output, PdfDocumentOpenMode.Import));
        using var ok = PdfReader.Open(output, "secret", PdfDocumentOpenMode.Import);
        Assert.Equal(1, ok.PageCount);
        await Assert.ThrowsAsync<PasswordRequiredException>(() => source.OpenAsync(new DocumentSet(), output));
    }

    [Theory]
    [InlineData(RasterFormat.Png, ".png")]
    [InlineData(RasterFormat.Jpeg, ".jpg")]
    [InlineData(RasterFormat.Tiff, ".tif")]
    [InlineData(RasterFormat.Bmp, ".bmp")]
    [InlineData(RasterFormat.Tga, ".tga")]
    public async Task Image_export_writes_every_format(RasterFormat format, string extension)
    {
        var (doc, _) = await Open(SamplePdf(2));
        var s = new PrintSettings { ImageFormat = format, ImageDpi = 72 };
        var files = ImageExporter.Export(LayoutEngine.Build(doc.Pages, s), s, new SheetRenderer(source), Path.Combine(dir, "img"), "sheet");
        Assert.Equal(2, files.Count);
        Assert.All(files, f => { Assert.EndsWith(extension, f); Assert.True(new FileInfo(f).Length > 100); });
    }

    [Fact]
    public async Task Opens_images_and_text_files()
    {
        var png = Path.Combine(dir, "pic.png");
        using (var bmp = new Bitmap(300, 200)) { using var g = Graphics.FromImage(bmp); g.Clear(Color.Red); bmp.Save(png); }
        var (_, pages) = await Open(png);
        Assert.Single(pages);
        Assert.True(pages[0].Width > pages[0].Height);
        using var lease = source.Render(pages[0], new RectangleF(0, 0, 1, 1), 150, 100);
        Assert.Equal(Color.Red.R, lease!.Bitmap.GetPixel(10, 10).R);

        var txt = Path.Combine(dir, "note.txt");
        File.WriteAllText(txt, string.Join("\n", Enumerable.Range(1, 130).Select(i => "line " + i)));
        var (_, textPages) = await Open(txt);
        Assert.Equal(3, textPages.Count);
        using var textLease = source.Render(textPages[0], new RectangleF(0, 0, 1, 1), 300, 424);
        Assert.True(PageAnalyzer.InkCoverage(textLease!.Bitmap) > 0.001);
    }

    [Fact]
    public async Task Analyzer_finds_content_bounds_and_blank_pages()
    {
        var path = Path.Combine(dir, "mixed.pdf");
        PdfExporter.EnsureFonts();
        using (var d = new PdfDocument())
        {
            d.AddPage();
            var p = d.AddPage();
            using var g = XGraphics.FromPdfPage(p);
            g.DrawRectangle(XBrushes.Black, 200, 300, 100, 100);
            d.Save(path);
        }
        var (_, pages) = await Open(path);
        var analyzer = new PageAnalyzer(source);
        await analyzer.AnalyzeAsync(pages, null, CancellationToken.None);
        Assert.True(analyzer.CachedBlank(pages[0]));
        var box = analyzer.CachedBounds(pages[1])!.Value;
        Assert.InRange(box.X, 0.3f, 0.36f);
        Assert.InRange(box.Width, 0.15f, 0.2f);
    }

    [Fact]
    public void Effects_matrix_behaves()
    {
        Assert.Equal(Color.FromArgb(255, 0, 0, 0), Effects.Apply(new PrintSettings { Invert = true }, Color.White).WithoutAlpha());
        var gray = Effects.Apply(new PrintSettings { Grayscale = true }, Color.FromArgb(255, 255, 0, 0));
        Assert.Equal(gray.R, gray.G);
        Assert.InRange(Effects.Apply(new PrintSettings { InkSaving = 50 }, Color.Black).R, 120, 135);
        Assert.Equal(255, Effects.Apply(new PrintSettings { InkSaving = 50 }, Color.White).R);
    }

    [Fact]
    public void Themes_round_trip_through_json()
    {
        var s = new PrintSettings { Mode = LayoutMode.Booklet, Binding = BookletBinding.Right, Duplex = DuplexMode.ShortEdge };
        s.Watermarks.Add(new Watermark { Text = "X {page}", Anchor = Anchor.TopRight });
        var back = PrintSettings.FromJson(s.ToJson());
        Assert.Equal(LayoutMode.Booklet, back.Mode);
        Assert.Equal(BookletBinding.Right, back.Binding);
        Assert.Equal(Anchor.TopRight, back.Watermarks[0].Anchor);
    }
}

internal static class TestExtensions
{
    public static Color WithoutAlpha(this Color c) => Color.FromArgb(255, c.R, c.G, c.B);

    public static PrintSettings WithNoMargins(this PrintSettings s)
    {
        s.MarginTopMm = s.MarginBottomMm = s.MarginLeftMm = s.MarginRightMm = 0;
        return s;
    }
}
