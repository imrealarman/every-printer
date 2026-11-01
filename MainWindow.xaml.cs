using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using EveryPrinter.Models;
using EveryPrinter.Services;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace EveryPrinter;

/// <summary>What the preview, the thumbnails and the exports were built from, so a late result can tell it is out of date.</summary>
internal sealed record LayoutState(List<Sheet> Sheets, PrintSettings Settings, int Version);

public sealed partial class MainWindow : Window
{
    private readonly AppSettings app = AppSettings.Load();
    private PrintSettings print = new();
    private readonly DocumentSet doc = new();
    private readonly PageSource source = new();
    private readonly SheetRenderer renderer;
    private readonly PageAnalyzer analyzer;

    private LayoutState layout = new([], new PrintSettings(), 0);
    private List<SheetItem> sheetItems = [];
    private int current;
    private PageRef? selected;
    private int layoutVersion;
    private bool busy;
    private bool dialogOpen;
    private bool suppressSheetSelection;
    private CancellationTokenSource? analysisCts;

    private readonly Microsoft.UI.Xaml.DispatcherTimer layoutTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly Microsoft.UI.Xaml.DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly SemaphoreSlim thumbGate = new(2);
    private Action? toastAction;

    public MainWindow()
    {
        renderer = new SheetRenderer(source);
        analyzer = new PageAnalyzer(source);

        InitializeComponent();
        Title = "Every Printer";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TopBar);
        var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(1560, work.Width - 40);
        var height = Math.Min(960, work.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        ThemeStore.EnsureSamples();
        print = ThemeStore.Load(app.ThemeName);
        if (string.IsNullOrEmpty(print.Printer)) print.Printer = PrintService.DefaultPrinter();

        layoutTimer.Tick += (_, _) => { layoutTimer.Stop(); DoRelayout(); };
        toastTimer.Tick += (_, _) => HideToast();

        BuildInspector();
        RefreshThemeList();
        ApplyTheme();
        Root.ActualThemeChanged += (_, _) => { Palette.Apply(IsLight); StyleCaptionButtons(); RequestRender(); };
        Root.PreviewKeyDown += RootKeyDown;
        RulerToggle.IsChecked = app.ShowRulers;
        InitPreview();
        SyncInspector();
        UpdateChrome();

        Root.Loaded += async (_, _) =>
        {
            Root.Focus(FocusState.Programmatic);
            StartInboxWatcher();
            var args = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToList();
            if (args.Count > 0) await OpenFilesAsync(args);
        };
        Closed += (_, _) => { app.Save(); inbox?.Dispose(); };
    }

    // ---- Theme ----

    private bool IsLight => app.Theme switch
    {
        AppTheme.Light => true,
        AppTheme.Dark => false,
        _ => new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background).R > 128,
    };

    private void ApplyTheme()
    {
        Root.RequestedTheme = app.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        Palette.Apply(IsLight);
        StyleCaptionButtons();
    }

    private void StyleCaptionButtons()
    {
        var light = IsLight;
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = light ? ColorHelper.FromArgb(255, 0x22, 0x22, 0x22) : ColorHelper.FromArgb(255, 0xEA, 0xEA, 0xEA);
        bar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 0x9B, 0x9B, 0x9B);
        bar.ButtonHoverBackgroundColor = light ? ColorHelper.FromArgb(255, 0xE1, 0xE1, 0xE1) : ColorHelper.FromArgb(255, 0x3A, 0x3A, 0x3A);
        bar.ButtonHoverForegroundColor = light ? Colors.Black : Colors.White;
    }

    private void ThemeToggleClick(object sender, RoutedEventArgs e) => CycleTheme();

    private void CycleTheme()
    {
        app.Theme = (AppTheme)(((int)app.Theme + 1) % 3);
        app.Save();
        ApplyTheme();
        RequestRender();
        ShowToast($"Theme: {app.Theme}");
    }

    // ---- Opening files ----

    private async void OpenClick(object sender, RoutedEventArgs e) => await PickAndOpenAsync();

    private async Task PickAndOpenAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        foreach (var ext in new[] { ".pdf" }.Concat(PageSource.ImageExtensions).Concat(PageSource.TextExtensions)) picker.FileTypeFilter.Add(ext);
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 }) await OpenFilesAsync(files.Select(f => f.Path));
    }

    private async void PasteClick(object sender, RoutedEventArgs e) => await PasteAsync();

    private async Task PasteAsync()
    {
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await content.GetStorageItemsAsync();
                await OpenFilesAsync(items.Select(i => i.Path).Where(File.Exists));
            }
            else if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
            {
                var reference = await content.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                using var net = new MemoryStream();
                await System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(stream).CopyToAsync(net);
                net.Position = 0;
                using var decoded = new Bitmap(net);
                var copy = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(copy)) { g.Clear(System.Drawing.Color.White); g.DrawImageUnscaled(decoded, 0, 0); }
                doc.AddPages(source.AddBitmap(doc, copy, $"Pasted picture {DateTime.Now:HH-mm-ss}"));
                AfterPagesAdded(1, 1);
            }
            else ShowToast("The clipboard has no picture or files to paste.");
        }
        catch (Exception ex)
        {
            Log.Error("Paste", ex);
            ShowToast("Could not paste from the clipboard.");
        }
    }

    private void RootDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to Every Printer";
        }
    }

    private async void RootDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = new List<string>();
        foreach (var item in items)
        {
            if (Directory.Exists(item.Path)) paths.AddRange(Directory.EnumerateFiles(item.Path).Where(source.IsSupported).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            else paths.Add(item.Path);
        }
        await OpenFilesAsync(paths);
    }

    public async Task OpenFilesAsync(IEnumerable<string> paths)
    {
        var files = paths.Where(source.IsSupported).ToList();
        if (files.Count == 0) { ShowToast("Every Printer opens PDFs, pictures and text files."); return; }
        SetBusy(true, "Opening…");
        int pages = 0, opened = 0;
        string? failure = null;
        try
        {
            foreach (var path in files)
            {
                try
                {
                    BusyText.Text = $"Opening {Path.GetFileName(path)}…";
                    var list = await OpenOneAsync(path);
                    if (list is null) continue;
                    doc.AddPages(list);
                    pages += list.Count;
                    opened++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    Log.Error("Open file", ex);
                    failure = ex.Message;
                }
            }
        }
        finally { SetBusy(false); }

        if (opened > 0) AfterPagesAdded(opened, pages);
        if (failure is not null) ShowToast(failure);
    }

    private async Task<List<PageRef>?> OpenOneAsync(string path)
    {
        string? password = null;
        while (true)
        {
            try { return await source.OpenAsync(doc, path, password); }
            catch (PasswordRequiredException)
            {
                var box = new PasswordBox { PlaceholderText = "Password", MinWidth = 280 };
                var result = await ShowDialogAsync(new ContentDialog
                {
                    Title = "This PDF is password protected",
                    Content = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = Path.GetFileName(path), TextWrapping = TextWrapping.Wrap }, box } },
                    PrimaryButtonText = "Open",
                    CloseButtonText = "Skip",
                    DefaultButton = ContentDialogButton.Primary,
                });
                if (result != ContentDialogResult.Primary) return null;
                password = box.Password;
            }
        }
    }

    private void AfterPagesAdded(int files, int pages)
    {
        selected = null;
        DoRelayout();
        if (sheets.Count > 0) SelectSheet(Math.Min(current, sheets.Count - 1), fit: current == 0);
        ShowToast(files == 1 ? $"Added {pages} {(pages == 1 ? "page" : "pages")}." : $"Added {pages} pages from {files} files.");
    }

    // ---- Undo, redo and page operations ----

    private void UndoClick(object sender, RoutedEventArgs e) => Undo();
    private void RedoClick(object sender, RoutedEventArgs e) => Redo();

    private void Undo() { if (!doc.CanUndo) return; doc.Undo(); selected = null; DoRelayout(); }
    private void Redo() { if (!doc.CanRedo) return; doc.Redo(); selected = null; DoRelayout(); }

    /// <summary>The selected page, or every page on the sheet being viewed when none is selected.</summary>
    private List<int> TargetIndexes()
    {
        if (selected is not null)
        {
            var i = doc.Pages.IndexOf(selected);
            if (i >= 0) return [i];
        }
        if (current < 0 || current >= sheets.Count) return [];
        return sheets[current].Placements.Select(p => doc.Pages.IndexOf(p.Page)).Where(i => i >= 0).Distinct().ToList();
    }

    private void RotateLeftClick(object sender, RoutedEventArgs e) => RotateTargets(270);
    private void RotateRightClick(object sender, RoutedEventArgs e) => RotateTargets(90);

    private void RotateTargets(int degrees)
    {
        var targets = TargetIndexes();
        if (targets.Count == 0) return;
        doc.Rotate(targets, degrees);
        DoRelayout();
    }

    private void DeletePageClick(object sender, RoutedEventArgs e) => DeleteTargets();

    private void DeleteTargets()
    {
        var targets = TargetIndexes();
        if (targets.Count == 0) return;
        doc.SetDeleted(targets, true);
        selected = null;
        DoRelayout();
        ShowToast(targets.Count == 1 ? "Page removed from the job. Ctrl+Z brings it back." : $"{targets.Count} pages removed. Ctrl+Z brings them back.");
    }

    private void ResetPageClick(object sender, RoutedEventArgs e)
    {
        var targets = TargetIndexes();
        if (targets.Count == 0) return;
        doc.ResetEdits(targets);
        DoRelayout();
    }

    // ---- Layout ----

    private List<Sheet> sheets => layout.Sheets;

    /// <summary>Settings or pages changed: rebuild the sheets shortly, so dragging a slider doesn't rebuild on every tick.</summary>
    private void SettingsChanged()
    {
        layoutTimer.Stop();
        layoutTimer.Start();
    }

    private void DoRelayout()
    {
        layoutTimer.Stop();
        var settings = print.Clone();
        var hooks = new LayoutHooks
        {
            IsBlank = settings.SkipBlankPages ? analyzer.CachedBlank : null,
            ContentBounds = settings.Mode == LayoutMode.Join && settings.JoinSmart ? analyzer.CachedBounds : null,
        };
        var built = LayoutEngine.Build(doc.Pages, settings, hooks);
        AddFindOverlays(built);

        var sameCount = built.Count == sheetItems.Count;
        var sizeChanged = current < sheets.Count && current < built.Count && (sheets[current].Width != built[current].Width || sheets[current].Height != built[current].Height);
        layout = new LayoutState(built, settings, ++layoutVersion);

        if (current >= built.Count) current = Math.Max(0, built.Count - 1);

        if (sameCount)
        {
            for (var i = 0; i < built.Count; i++)
            {
                var item = sheetItems[i];
                item.Caption = CaptionOf(built[i], i);
                item.ThumbHeight = ThumbHeightOf(built[i]);
                item.Thumb = null;
            }
        }
        else
        {
            sheetItems = built.Select((s, i) => new SheetItem(i, CaptionOf(s, i), ThumbHeightOf(s))).ToList();
            suppressSheetSelection = true;
            SheetStrip.ItemsSource = sheetItems;
            suppressSheetSelection = false;
        }
        MarkSelectedSheet();
        foreach (var item in sheetItems.Where(i => i.Realized)) RequestThumb(item);

        EnsureAnalysis();
        UpdateChrome();
        if (sizeChanged || sheets.Count == 0) fitMode = true;
        RequestRender();
        UpdateInk();
    }

    private static string CaptionOf(Sheet sheet, int index)
    {
        var numbers = sheet.Placements.Select(p => p.Number).ToList();
        var pages = numbers.Count switch
        {
            0 => "blank",
            1 => $"page {numbers[0]}",
            _ => $"pages {numbers.Min()}–{numbers.Max()}",
        };
        var side = sheet.IsBack ? " · back" : "";
        return sheet.Label.Length > 0 ? $"{index + 1} · {sheet.Label}" : $"{index + 1} · {pages}{side}";
    }

    private static double ThumbHeightOf(Sheet sheet) => Math.Clamp(126 * sheet.Height / Math.Max(1, sheet.Width), 60, 200);

    /// <summary>Some options need to look at the pages first (blank pages, content edges). Do that in the background, then lay out again.</summary>
    private void EnsureAnalysis()
    {
        var needs = print.SkipBlankPages || (print.Mode == LayoutMode.Join && print.JoinSmart);
        if (!needs) return;
        var todo = doc.Pages.Where(p => !p.IsBlank && !analyzer.HasResult(p)).ToList();
        if (todo.Count == 0) return;

        analysisCts?.Cancel();
        var cts = analysisCts = new CancellationTokenSource();
        StatusText.Text = $"Looking at {todo.Count} pages…";
        _ = Task.Run(async () =>
        {
            try { await analyzer.AnalyzeAsync(todo, null, cts.Token); }
            catch (OperationCanceledException) { return; }
            DispatcherQueue.TryEnqueue(() => { if (!cts.IsCancellationRequested) DoRelayout(); });
        });
    }

    private void UpdateChrome()
    {
        var jobs = doc.Jobs.Count;
        var live = doc.LivePageCount;
        var empty = doc.Pages.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DocumentTitle.Text = empty ? "Nothing to print yet"
            : jobs == 1 ? doc.Jobs[0].Name : $"{jobs} files";
        SheetsHeader.Text = sheets.Count == 0 ? "SHEETS" : $"SHEETS · {sheets.Count}";
        RebuildJobs();
        UndoButton.IsEnabled = doc.CanUndo;
        RedoButton.IsEnabled = doc.CanRedo;
        PrintButton.IsEnabled = PdfButton.IsEnabled = ImagesButton.IsEnabled = sheets.Count > 0 && !busy;

        if (sheets.Count == 0) SheetPosition.Text = "—";
        else SheetPosition.Text = $"Sheet {current + 1} of {sheets.Count}";

        if (empty) { StatusText.Text = "Ready. Print to the Every Printer inbox, or drop files here."; return; }
        var removed = doc.Pages.Count - live;
        var paper = print.UseSourceSize && print.Mode == LayoutMode.Normal ? "page size" : print.Paper;
        var physical = sheets.Count == 0 ? 0 : sheets.Max(s => s.Physical);
        StatusText.Text = $"{live} {(live == 1 ? "page" : "pages")}" + (removed > 0 ? $" ({removed} removed)" : "")
            + $" · {sheets.Count} {(sheets.Count == 1 ? "sheet" : "sheets")}"
            + (print.Duplex != DuplexMode.OneSided || print.Mode == LayoutMode.Booklet ? $" · {physical} of paper" : "")
            + $" · {paper}";
    }

    // ---- Sheet strip and navigation ----

    private void SheetContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not SheetItem item) return;
        if (args.InRecycleQueue) { item.Realized = false; return; }
        item.Realized = true;
        if (item.Thumb is null) RequestThumb(item);
    }

    private async void RequestThumb(SheetItem item)
    {
        var state = layout;
        if (item.Index >= state.Sheets.Count) return;
        var sheet = state.Sheets[item.Index];
        await thumbGate.WaitAsync();
        try
        {
            if (state.Version != layoutVersion || !item.Realized) return;
            var (pixels, w, h) = await Task.Run(() =>
            {
                using var bitmap = renderer.Render(sheet, state.Settings, 126f / sheet.Width);
                return (ToBgra(bitmap), bitmap.Width, bitmap.Height);
            });
            if (state.Version != layoutVersion) return;
            item.Thumb = ToImage(pixels, w, h);
        }
        catch (Exception ex) { Log.Error("Thumbnail", ex); }
        finally { thumbGate.Release(); }
    }

    private void SheetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSheetSelection || SheetStrip.SelectedIndex < 0) return;
        SelectSheet(SheetStrip.SelectedIndex, fit: false);
    }

    private void SelectSheet(int index, bool fit)
    {
        if (sheets.Count == 0) { current = 0; UpdateChrome(); return; }
        var changed = index != current;
        current = Math.Clamp(index, 0, sheets.Count - 1);
        MarkSelectedSheet();
        suppressSheetSelection = true;
        SheetStrip.SelectedIndex = current;
        SheetStrip.ScrollIntoView(sheetItems[current]);
        suppressSheetSelection = false;
        if (fit || changed && !HasSameSize(current)) fitMode = true;
        selected = changed ? null : selected;
        UpdateChrome();
        RequestRender();
        UpdateInk();
    }

    private bool HasSameSize(int index) => previewSheetSize is { } size && sheets[index].Width == size.Width && sheets[index].Height == size.Height;

    private void MarkSelectedSheet()
    {
        for (var i = 0; i < sheetItems.Count; i++) sheetItems[i].Selected = i == current;
    }

    private void FirstSheetClick(object sender, RoutedEventArgs e) => SelectSheet(0, false);
    private void PreviousSheetClick(object sender, RoutedEventArgs e) => SelectSheet(current - 1, false);
    private void NextSheetClick(object sender, RoutedEventArgs e) => SelectSheet(current + 1, false);
    private void LastSheetClick(object sender, RoutedEventArgs e) => SelectSheet(sheets.Count - 1, false);

    // ---- Pixels to XAML images ----

    internal static byte[] ToBgra(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var pixels = new byte[bitmap.Width * bitmap.Height * 4];
            if (data.Stride == bitmap.Width * 4) Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            else for (var y = 0; y < bitmap.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bitmap.Width * 4, bitmap.Width * 4);
            return pixels;
        }
        finally { bitmap.UnlockBits(data); }
    }

    internal static WriteableBitmap ToImage(byte[] pixels, int width, int height, WriteableBitmap? reuse = null)
    {
        var target = reuse is not null && reuse.PixelWidth == width && reuse.PixelHeight == height ? reuse : new WriteableBitmap(width, height);
        using var stream = target.PixelBuffer.AsStream();
        stream.Write(pixels, 0, Math.Min(pixels.Length, (int)target.PixelBuffer.Length));
        target.Invalidate();
        return target;
    }

    // ---- Keyboard ----

    private bool TypingInTextBox() =>
        FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or PasswordBox or NumberBox or RichEditBox or AutoSuggestBox
        || FocusManager.GetFocusedElement(Content.XamlRoot) is ComboBox { IsEditable: true };

    private static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private async void RootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Down(VirtualKey.Control);
        var shift = Down(VirtualKey.Shift);
        var typing = TypingInTextBox();

        if (ctrl)
        {
            switch (e.Key)
            {
                case VirtualKey.O: e.Handled = true; await PickAndOpenAsync(); return;
                case VirtualKey.V when !typing: e.Handled = true; await PasteAsync(); return;
                case VirtualKey.P: e.Handled = true; PrintClick(this, e); return;
                case VirtualKey.S: e.Handled = true; PdfClick(this, e); return;
                case VirtualKey.E when shift: e.Handled = true; ImagesClick(this, e); return;
                case VirtualKey.Z when !typing: e.Handled = true; Undo(); return;
                case VirtualKey.Y when !typing: e.Handled = true; Redo(); return;
                case VirtualKey.F: e.Handled = true; FindToggle.IsChecked = true; FindToggleClick(this, e); return;
                case VirtualKey.T when shift: e.Handled = true; TransparentToggle.IsChecked = !TransparentToggle.IsChecked; TransparentClick(this, e); return;
                case VirtualKey.T: e.Handled = true; CycleTheme(); return;
            }
            return;
        }

        if (e.Key == VirtualKey.F1) { e.Handled = true; HelpClick(this, e); return; }
        if (e.Key == VirtualKey.F11) { e.Handled = true; FullScreenToggle.IsChecked = !FullScreenToggle.IsChecked; FullScreenClick(this, e); return; }
        if (e.Key == VirtualKey.Escape) { if (FullScreenToggle.IsChecked == true) { FullScreenToggle.IsChecked = false; FullScreenClick(this, e); e.Handled = true; } return; }
        if (typing) return;

        switch (e.Key)
        {
            case VirtualKey.Left or VirtualKey.PageUp: SelectSheet(current - 1, false); e.Handled = true; break;
            case VirtualKey.Right or VirtualKey.PageDown: SelectSheet(current + 1, false); e.Handled = true; break;
            case VirtualKey.Home: SelectSheet(0, false); e.Handled = true; break;
            case VirtualKey.End: SelectSheet(sheets.Count - 1, false); e.Handled = true; break;
            case VirtualKey.Delete: DeleteTargets(); e.Handled = true; break;
            case VirtualKey.R: RotateTargets(shift ? 270 : 90); e.Handled = true; break;
            case VirtualKey.V: PickTool("Select"); e.Handled = true; break;
            case VirtualKey.C: PickTool("Crop"); e.Handled = true; break;
            case VirtualKey.X: PickTool("Redact"); e.Handled = true; break;
            case VirtualKey.H: PickTool("Highlight"); e.Handled = true; break;
            case VirtualKey.T: PickTool("Text"); e.Handled = true; break;
            case VirtualKey.M: PickTool("Measure"); e.Handled = true; break;
            case VirtualKey.L: PickTool("Loupe"); e.Handled = true; break;
            case (VirtualKey)187 or VirtualKey.Add: ZoomBy(1.25); e.Handled = true; break;
            case (VirtualKey)189 or VirtualKey.Subtract: ZoomBy(0.8); e.Handled = true; break;
            case VirtualKey.Number0 or VirtualKey.NumberPad0: FitClick(this, e); e.Handled = true; break;
        }
    }

    private async void HelpClick(object sender, RoutedEventArgs e)
    {
        const string text = """
            Ctrl+O  Open files            Ctrl+V  Paste picture or files
            Ctrl+P  Print                 Ctrl+S  Save as PDF
            Ctrl+Shift+E  Save images     Ctrl+Z / Ctrl+Y  Undo / redo
            Ctrl+F  Find text             Ctrl+T  Light / dark theme
            F11  Full screen              Ctrl+Shift+T  Transparent window

            Left / Right  Previous / next sheet     Home / End  First / last sheet
            + / -  Zoom        0  Fit        Ctrl+wheel  Zoom at pointer
            V Select   C Crop   X Redact   H Highlight   T Text   M Measure   L Loupe
            R / Shift+R  Rotate page    Delete  Remove page

            Watermark macros: {page} {pages} {sheet} {sheets} {file} {date} {time} {user} {computer}
            """;
        await ShowDialogAsync(new ContentDialog
        {
            Title = "Keyboard shortcuts",
            Content = new TextBlock { Text = text, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12, IsTextSelectionEnabled = true },
            CloseButtonText = "Close",
        });
    }

    // ---- Toast, busy state, dialogs ----

    internal void ShowToast(string message, string? actionText = null, Action? action = null)
    {
        ToastText.Text = message;
        toastAction = action;
        ToastAction.Content = actionText;
        ToastAction.Visibility = actionText is null ? Visibility.Collapsed : Visibility.Visible;
        Toast.Visibility = Visibility.Visible;
        toastTimer.Stop();
        toastTimer.Start();
    }

    private void HideToast()
    {
        toastTimer.Stop();
        Toast.Visibility = Visibility.Collapsed;
    }

    private void ToastActionClick(object sender, RoutedEventArgs e)
    {
        var action = toastAction;
        HideToast();
        action?.Invoke();
    }

    private void SetBusy(bool value, string text = "")
    {
        busy = value;
        BusyVeil.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text;
        UpdateChrome();
    }

    private void ReportProgress(double fraction)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Progress.Visibility = fraction is > 0 and < 1 ? Visibility.Visible : Visibility.Collapsed;
            Progress.Value = fraction;
        });
    }

    internal async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (dialogOpen) return ContentDialogResult.None;
        dialogOpen = true;
        try
        {
            dialog.XamlRoot = Content.XamlRoot;
            dialog.RequestedTheme = Root.ActualTheme;
            return await dialog.ShowAsync();
        }
        finally { dialogOpen = false; }
    }

    private IntPtr Hwnd => WindowNative.GetWindowHandle(this);
}
