using System.Drawing;
using System.Drawing.Printing;
using EveryPrinter.Models;

namespace EveryPrinter.Services;

/// <summary>Sends sheets to a real printer, with one-sided, automatic and manual two-sided printing.</summary>
public static class PrintService
{
    public static IReadOnlyList<string> Printers()
    {
        try { return PrinterSettings.InstalledPrinters.Cast<string>().ToList(); }
        catch (Exception ex) { Log.Error("List printers", ex); return []; }
    }

    public static string DefaultPrinter()
    {
        try { return new PrinterSettings().PrinterName; }
        catch (Exception ex) { Log.Error("Default printer", ex); return ""; }
    }

    public static bool SupportsDuplex(string printer)
    {
        try { return new PrinterSettings { PrinterName = printer }.CanDuplex; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Prints the sheets. For manual two-sided printing the fronts go first; <paramref name="askFlip"/> is then awaited so the
    /// person can turn the stack over, and the backs follow in reverse order.
    /// </summary>
    public static async Task PrintAsync(IReadOnlyList<Sheet> sheets, PrintSettings s, SheetRenderer renderer, string jobName,
        Func<Task<bool>> askFlip, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var manual = s.Duplex is DuplexMode.ManualLongEdge or DuplexMode.ManualShortEdge;
        var done = 0;
        void Step() => progress?.Report(++done / (double)sheets.Count);

        if (!manual)
        {
            await Task.Run(() => Run(sheets, s, renderer, jobName, rotateBacks: false, Step, cancel), cancel);
            return;
        }

        var fronts = sheets.Where(x => !x.IsBack).ToList();
        var backs = sheets.Where(x => x.IsBack).Reverse().ToList();
        await Task.Run(() => Run(fronts, s, renderer, jobName + " (fronts)", false, Step, cancel), cancel);
        if (backs.Count == 0) return;
        if (!await askFlip()) return;

        // Turning a stack over its short edge puts the backs upside down relative to the fronts.
        await Task.Run(() => Run(backs, s, renderer, jobName + " (backs)", s.Duplex == DuplexMode.ManualShortEdge, Step, cancel), cancel);
    }

    private static void Run(IReadOnlyList<Sheet> sheets, PrintSettings s, SheetRenderer renderer, string jobName, bool rotateBacks, Action step, CancellationToken cancel)
    {
        if (sheets.Count == 0) return;
        var index = 0;
        using var document = new PrintDocument { DocumentName = jobName };
        if (!string.IsNullOrEmpty(s.Printer)) document.PrinterSettings.PrinterName = s.Printer;
        if (!document.PrinterSettings.IsValid) throw new InvalidOperationException($"The printer “{s.Printer}” isn't available.");
        document.PrinterSettings.Copies = (short)Math.Clamp(s.Copies, 1, 999);
        document.PrinterSettings.Collate = s.Collate;
        document.DefaultPageSettings.Color = true;
        document.PrinterSettings.Duplex = s.Duplex switch
        {
            DuplexMode.LongEdge => Duplex.Vertical,
            DuplexMode.ShortEdge => Duplex.Horizontal,
            _ => Duplex.Simplex,
        };
        document.OriginAtMargins = false;

        document.QueryPageSettings += (_, e) =>
        {
            if (index >= sheets.Count) return;
            var sheet = sheets[index];
            e.PageSettings.Landscape = sheet.Width > sheet.Height;
            e.PageSettings.PaperSize = PickPaper(document.PrinterSettings, Math.Min(sheet.Width, sheet.Height), Math.Max(sheet.Width, sheet.Height));
        };

        document.PrintPage += (_, e) =>
        {
            if (cancel.IsCancellationRequested) { e.Cancel = true; return; }
            var sheet = sheets[index];
            var g = e.Graphics!;
            g.PageUnit = GraphicsUnit.Point;
            // The origin of a print page is the corner of the printable area, not of the paper.
            g.TranslateTransform(-e.PageSettings.HardMarginX * 0.72f, -e.PageSettings.HardMarginY * 0.72f);
            if (rotateBacks)
            {
                g.TranslateTransform(sheet.Width, sheet.Height);
                g.RotateTransform(180);
            }

            var pxPerPoint = Math.Clamp(s.PrintDpi, 72, 1200) / 72f;
            if (s.BitmapMode)
            {
                using var bitmap = renderer.Render(sheet, s, pxPerPoint, cancel);
                g.DrawImage(bitmap, new RectangleF(0, 0, sheet.Width, sheet.Height));
            }
            else
            {
                renderer.Draw(g, sheet, s, pxPerPoint, fillPaper: false, cancel);
            }

            step();
            index++;
            e.HasMorePages = index < sheets.Count;
        };

        document.Print();
    }

    /// <summary>The printer's own paper size closest to the sheet (within 3 mm), or a custom size.</summary>
    private static PaperSize PickPaper(PrinterSettings printer, float widthPt, float heightPt)
    {
        int w100 = (int)Math.Round(widthPt / 72 * 100), h100 = (int)Math.Round(heightPt / 72 * 100);
        const int tolerance = 12; // 3 mm in hundredths of an inch
        try
        {
            var match = printer.PaperSizes.Cast<PaperSize>()
                .Where(p => p.Kind != PaperKind.Custom || p.PaperName.Length > 0)
                .Select(p => (Paper: p, Error: Math.Abs(Math.Min(p.Width, p.Height) - w100) + Math.Abs(Math.Max(p.Width, p.Height) - h100)))
                .Where(x => x.Error <= tolerance * 2)
                .OrderBy(x => x.Error)
                .Select(x => x.Paper)
                .FirstOrDefault();
            if (match is not null) return match;
        }
        catch (Exception ex) { Log.Error("Read paper sizes", ex); }
        return new PaperSize("Custom", w100, h100);
    }
}
