using System.Drawing;
using System.Drawing.Imaging;
using EveryPrinter.Models;
using EveryPrinter.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace EveryPrinter;

/// <summary>
/// Find text in the pages. PDFs from the print queue are pictures of pages as far as Windows' PDF reader is concerned,
/// so pages are read with Windows OCR (offline), one at a time starting from the sheet being viewed, and matches light up as they are found.
/// </summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<(int Job, int Page), List<(string Text, RectangleF Box)>> ocrCache = [];
    private Dictionary<(int Job, int Page), List<RectangleF>> findHits = [];
    private CancellationTokenSource? findCts;
    private readonly Microsoft.UI.Xaml.DispatcherTimer findTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private bool findTimerHooked;

    private void FindToggleClick(object sender, RoutedEventArgs e)
    {
        var on = FindToggle.IsChecked == true;
        FindBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!findTimerHooked) { findTimer.Tick += (_, _) => { findTimer.Stop(); StartFind(); }; findTimerHooked = true; }
        if (on) FindBox.Focus(FocusState.Programmatic);
        else { findCts?.Cancel(); findHits.Clear(); FindStatus.Text = ""; DoRelayout(); }
    }

    private void FindTextChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
    {
        findTimer.Stop();
        findTimer.Start();
    }

    private void FindKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            if (Down(Windows.System.VirtualKey.Shift)) FindPreviousClick(this, e); else FindNextClick(this, e);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            FindToggle.IsChecked = false;
            FindToggleClick(this, e);
        }
    }

    private async void StartFind()
    {
        findCts?.Cancel();
        var query = FindBox.Text.Trim();
        findHits = [];
        if (query.Length == 0) { FindStatus.Text = ""; DoRelayout(); return; }
        if (!OcrService.Available) { FindStatus.Text = "Windows has no OCR language installed (Settings > Time & language)."; return; }

        var cts = findCts = new CancellationTokenSource();
        var pages = doc.Pages.Where(p => !p.Deleted && !p.IsBlank).ToList();
        var first = sheets.Count > 0 && current < sheets.Count ? sheets[current].Placements.FirstOrDefault()?.Page : null;
        var start = first is null ? 0 : Math.Max(0, pages.IndexOf(first));
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lastRefresh = DateTime.UtcNow;
        var matches = 0;

        try
        {
            for (var n = 0; n < pages.Count; n++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var page = pages[(start + n) % pages.Count];
                FindStatus.Text = $"Reading page {n + 1} of {pages.Count} · {matches} {(matches == 1 ? "match" : "matches")}";
                var words = await OcrPageAsync(page, cts.Token);
                if (words is null) continue;

                var text = string.Join(" ", words.Select(w => w.Text));
                if (!text.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                var boxes = words.Where(w => tokens.Any(t => w.Text.Contains(t, StringComparison.OrdinalIgnoreCase))).Select(w => Inflate(w.Box)).ToList();
                if (boxes.Count == 0) continue;

                findHits[(page.Job.Id, page.Index)] = boxes;
                matches += boxes.Count;
                if ((DateTime.UtcNow - lastRefresh).TotalMilliseconds > 350 || matches == boxes.Count) { lastRefresh = DateTime.UtcNow; DoRelayout(); }
            }
            DoRelayout();
            FindStatus.Text = matches == 0 ? "No matches" : $"{matches} {(matches == 1 ? "match" : "matches")} on {findHits.Count} {(findHits.Count == 1 ? "page" : "pages")}";
        }
        catch (OperationCanceledException) { }
    }

    private static RectangleF Inflate(RectangleF box)
    {
        box.Inflate(0.002f, 0.001f);
        return box;
    }

    private async Task<List<(string Text, RectangleF Box)>?> OcrPageAsync(PageRef page, CancellationToken cancel)
    {
        var key = (page.Job.Id, page.Index);
        if (ocrCache.TryGetValue(key, out var cached)) return cached;

        var png = await Task.Run(() =>
        {
            using var lease = source.Render(page, new RectangleF(0, 0, 1, 1), 1600, Math.Max(100, (int)(1600 * page.Height / Math.Max(1, page.Width))));
            if (lease is null) return null;
            using var stream = new MemoryStream();
            lease.Bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }, cancel);
        if (png is null) return null;

        var result = await OcrService.RecognizeAsync(png);
        var words = result?.Words.Select(w => (w.Text, new RectangleF((float)w.X, (float)w.Y, (float)w.Width, (float)w.Height))).ToList() ?? [];
        ocrCache[key] = words;
        return words;
    }

    private void AddFindOverlays(List<Sheet> built)
    {
        if (findHits.Count == 0) return;
        foreach (var sheet in built)
            foreach (var p in sheet.Placements)
                if (findHits.TryGetValue((p.Page.Job.Id, p.Page.Index), out var boxes))
                    foreach (var box in boxes)
                        sheet.Front.Add(new RectOverlay(p.MapRect(box), "#FF9800", 0.5, "#E65100", 1, p.Clip));
    }

    private void FindNextClick(object sender, RoutedEventArgs e) => JumpToMatch(+1);
    private void FindPreviousClick(object sender, RoutedEventArgs e) => JumpToMatch(-1);

    private void JumpToMatch(int direction)
    {
        var withHits = sheets.Select((s, i) => (s, i))
            .Where(x => x.s.Placements.Any(p => findHits.ContainsKey((p.Page.Job.Id, p.Page.Index))))
            .Select(x => x.i).ToList();
        if (withHits.Count == 0) return;
        var target = direction > 0
            ? withHits.FirstOrDefault(i => i > current, withHits[0])
            : withHits.LastOrDefault(i => i < current, withHits[^1]);
        SelectSheet(target, false);
    }
}
