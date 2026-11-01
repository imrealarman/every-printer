using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using EveryPrinter.Models;

namespace EveryPrinter.Services;

/// <summary>Looks at small pictures of pages: where the content is (for auto-crop and join), whether a page is blank, and how much ink it uses.</summary>
public sealed class PageAnalyzer(PageSource source)
{
    private const int Probe = 160;
    private const int Threshold = 235;

    private readonly ConcurrentDictionary<(int Job, int Page), RectangleF?> bounds = new();

    public bool HasResult(PageRef page) => bounds.ContainsKey((page.Job.Id, page.Index));

    /// <summary>The part of the page with content, as fractions of the unrotated page; null for a blank page. Uses what was found earlier, or the whole page if not looked at yet.</summary>
    public RectangleF? CachedBounds(PageRef page) =>
        bounds.TryGetValue((page.Job.Id, page.Index), out var known) ? known : new RectangleF(0, 0, 1, 1);

    public bool CachedBlank(PageRef page) => bounds.TryGetValue((page.Job.Id, page.Index), out var known) && known is null;

    /// <summary>Analyses every page not yet looked at. Runs on worker threads and reports how many are done.</summary>
    public Task AnalyzeAsync(IEnumerable<PageRef> pages, IProgress<int>? progress, CancellationToken cancel)
    {
        var todo = pages.Where(p => !p.IsBlank && !HasResult(p)).ToList();
        return Task.Run(() =>
        {
            var done = 0;
            Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cancel }, page =>
            {
                bounds[(page.Job.Id, page.Index)] = Measure(page);
                progress?.Report(Interlocked.Increment(ref done));
            });
        }, cancel);
    }

    private RectangleF? Measure(PageRef page)
    {
        try
        {
            var aspect = page.Height / Math.Max(1, page.Width);
            var h = Math.Clamp((int)(Probe * aspect), 32, 400);
            using var lease = source.Render(page, new RectangleF(0, 0, 1, 1), Probe, h);
            return lease is null ? null : ContentBounds(lease.Bitmap, lease.Source);
        }
        catch (Exception ex)
        {
            Log.Error($"Analyse page {page.Index + 1} of {page.Job.Name}", ex);
            return new RectangleF(0, 0, 1, 1);
        }
    }

    /// <summary>Bounding box of non-white pixels as fractions of <paramref name="area"/>; null if there is (almost) nothing there.</summary>
    public static RectangleF? ContentBounds(Bitmap bitmap, RectangleF area)
    {
        var rect = Rectangle.Round(area);
        rect.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        if (rect.Width < 2 || rect.Height < 2) return null;

        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[rect.Width * 4];
            int minX = rect.Width, minY = rect.Height, maxX = -1, maxY = -1, count = 0;
            for (var y = 0; y < rect.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = 0; x < rect.Width; x++)
                {
                    var i = x * 4;
                    if (row[i] > Threshold && row[i + 1] > Threshold && row[i + 2] > Threshold) continue;
                    count++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            // A speck or two is scanner dust, not content.
            if (count < Math.Max(3, rect.Width * rect.Height / 6000) || maxX < 0) return null;
            return new RectangleF(minX / (float)rect.Width, minY / (float)rect.Height, (maxX - minX + 1) / (float)rect.Width, (maxY - minY + 1) / (float)rect.Height);
        }
        finally { bitmap.UnlockBits(data); }
    }

    /// <summary>Average darkness of a bitmap, 0 (all white) to 1 (all black), sampled sparsely so it is cheap even on a full-size preview.</summary>
    public static double InkCoverage(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[bitmap.Width * 4];
            double sum = 0;
            long n = 0;
            for (var y = 0; y < bitmap.Height; y += 3)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = 0; x < bitmap.Width; x += 3)
                {
                    var i = x * 4;
                    sum += 1 - (row[i] * 0.114 + row[i + 1] * 0.587 + row[i + 2] * 0.299) / 255;
                    n++;
                }
            }
            return n == 0 ? 0 : sum / n;
        }
        finally { bitmap.UnlockBits(data); }
    }
}
