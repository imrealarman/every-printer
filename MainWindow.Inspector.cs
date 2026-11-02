using EveryPrinter.Models;
using Orientation = EveryPrinter.Models.Orientation;
using EveryPrinter.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EveryPrinter;

/// <summary>
/// The settings panel on the right. Controls are built in code from small helpers, each tied to one setting by a getter and a setter,
/// so adding an option is one line and loading a theme refreshes every control the same way.
/// </summary>
public sealed partial class MainWindow
{
    private readonly List<Action> syncs = [];        // push settings into the controls
    private readonly List<Action> visibilities = []; // show or hide controls that only apply to some modes
    private readonly List<Action> markSyncs = [];
    private bool syncing;
    private readonly List<(ToggleButton Button, FrameworkElement Panel)> tabs = [];

    private ListView marksList = null!;
    private StackPanel markEditor = null!;
    private StackPanel jobsPanel = null!;
    private ComboBox printerBox = null!;
    private TextBlock formLabel = null!;

    private Watermark? Mark => marksList is { SelectedIndex: >= 0 } && marksList.SelectedIndex < print.Watermarks.Count ? print.Watermarks[marksList.SelectedIndex] : null;

    // ---- Building ----

    private void BuildInspector()
    {
        AddTab("Layout", BuildLayoutTab());
        AddTab("Pages", BuildPagesTab());
        AddTab("Marks", BuildMarksTab());
        AddTab("Effects", BuildEffectsTab());
        AddTab("Output", BuildOutputTab());
        ShowTab(0);
    }

    private void AddTab(string title, FrameworkElement panel)
    {
        var button = new ToggleButton { Content = title, Style = (Style)Application.Current.Resources["ToolToggle"], Padding = new Thickness(11, 0, 11, 0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var index = tabs.Count;
        button.Click += (_, _) => ShowTab(index);
        TabStrip.Children.Add(button);
        panel.Visibility = Visibility.Collapsed;
        InspectorHost.Children.Add(panel);
        tabs.Add((button, panel));
    }

    private void ShowTab(int index)
    {
        for (var i = 0; i < tabs.Count; i++)
        {
            tabs[i].Button.IsChecked = i == index;
            tabs[i].Panel.Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static StackPanel Stack(double spacing = 10) => new() { Spacing = spacing, Padding = new Thickness(0, 6, 0, 0) };

    private static TextBlock Header(string text) =>
        new() { Text = text.ToUpperInvariant(), Style = (Style)Application.Current.Resources["PanelHeader"], Margin = new Thickness(0, 14, 0, 0) };

    private static TextBlock Note(string text) =>
        new() { Text = text, Style = (Style)Application.Current.Resources["MutedText"], TextWrapping = TextWrapping.Wrap };

    private static Grid Row(string label, FrameworkElement control, double labelWidth = 112)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.WrapWholeWords });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static Grid Pair(FrameworkElement a, FrameworkElement b)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(a);
        Grid.SetColumn(b, 1);
        grid.Children.Add(b);
        return grid;
    }

    private void ShowWhen(UIElement element, Func<bool> condition)
    {
        void Update() => element.Visibility = condition() ? Visibility.Visible : Visibility.Collapsed;
        visibilities.Add(Update);
        Update();
    }

    private ComboBox Combo<T>((string Label, T Value)[] options, Func<T> get, Action<T> set, List<Action>? into = null) where T : notnull
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var o in options) box.Items.Add(o.Label);
        (into ?? syncs).Add(() => box.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => EqualityComparer<T>.Default.Equals(o.Value, get()))));
        box.SelectionChanged += (_, _) =>
        {
            if (syncing || box.SelectedIndex < 0) return;
            set(options[box.SelectedIndex].Value);
            Changed();
        };
        return box;
    }

    private NumberBox Num(double min, double max, double step, Func<double> get, Action<double> set, List<Action>? into = null, string? unit = null)
    {
        var box = new NumberBox
        {
            Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch, NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter { FractionDigits = step < 1 ? 1 : 0, IntegerDigits = 1 },
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
        };
        if (unit is not null) box.Header = null;
        (into ?? syncs).Add(() => box.Value = get());
        box.ValueChanged += (_, e) =>
        {
            if (syncing || double.IsNaN(e.NewValue)) return;
            set(e.NewValue);
            Changed();
        };
        return box;
    }

    private CheckBox Check(string text, Func<bool> get, Action<bool> set, List<Action>? into = null)
    {
        var box = new CheckBox { Content = text, MinWidth = 0 };
        (into ?? syncs).Add(() => box.IsChecked = get());
        box.Click += (_, _) =>
        {
            if (syncing) return;
            set(box.IsChecked == true);
            Changed();
        };
        return box;
    }

    private Slider Slide(double min, double max, Func<double> get, Action<double> set, List<Action>? into = null, double step = 1)
    {
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, HorizontalAlignment = HorizontalAlignment.Stretch };
        (into ?? syncs).Add(() => slider.Value = get());
        slider.ValueChanged += (_, e) =>
        {
            if (syncing) return;
            set(e.NewValue);
            Changed();
        };
        return slider;
    }

    private TextBox Text(Func<string> get, Action<string> set, string placeholder = "", List<Action>? into = null)
    {
        var box = new TextBox { PlaceholderText = placeholder, HorizontalAlignment = HorizontalAlignment.Stretch };
        (into ?? syncs).Add(() => { if (box.Text != get()) box.Text = get(); });
        box.TextChanged += (_, _) =>
        {
            if (syncing) return;
            set(box.Text);
            Changed();
        };
        return box;
    }

    private static Button Action_(string text, Action click, string? tip = null)
    {
        var button = new Button { Content = text, Style = (Style)Application.Current.Resources["TileButton"], HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) => click();
        if (tip is not null) ToolTipService.SetToolTip(button, tip);
        return button;
    }

    /// <summary>A setting changed: refresh what depends on it and lay the sheets out again.</summary>
    private void Changed()
    {
        foreach (var update in visibilities) update();
        SettingsChanged();
    }

    private void SyncInspector()
    {
        syncing = true;
        try
        {
            foreach (var sync in syncs) sync();
            RefreshMarks();
            foreach (var update in visibilities) update();
        }
        finally { syncing = false; }
    }

    // ---- Layout tab ----

    private FrameworkElement BuildLayoutTab()
    {
        var p = Stack();

        p.Children.Add(Row("Mode", Combo(new[]
        {
            ("One page per sheet", LayoutMode.Normal), ("Several pages per sheet", LayoutMode.NUp), ("Booklet", LayoutMode.Booklet),
            ("Poster", LayoutMode.Poster), ("Join pages side by side", LayoutMode.Join),
        }, () => print.Mode, v =>
        {
            print.Mode = v;
            if (v == LayoutMode.Booklet && print.Duplex == DuplexMode.OneSided) print.Duplex = DuplexMode.ShortEdge;
        })));
        p.Children.Add(Note("Booklet puts the pages in folding order. Print on both sides, flipping on the short edge, then fold the stack."));
        ShowWhen(p.Children[^1], () => print.Mode == LayoutMode.Booklet);

        // Paper
        p.Children.Add(Header("Paper"));
        var paperOptions = Paper.Sizes.Select(s => (s.Name, s.Name)).Append(("Custom size", Paper.Custom)).ToArray();
        var paper = Row("Size", Combo(paperOptions, () => print.Paper, v => print.Paper = v));
        p.Children.Add(paper);
        var custom = Pair(Num(10, 3000, 1, () => print.CustomWidthMm, v => print.CustomWidthMm = v), Num(10, 3000, 1, () => print.CustomHeightMm, v => print.CustomHeightMm = v));
        var customRow = Row("Width × height (mm)", custom);
        p.Children.Add(customRow);
        ShowWhen(customRow, () => print.Paper == Paper.Custom);
        p.Children.Add(Row("Orientation", Combo(new[] { ("Automatic", Orientation.Auto), ("Portrait", Orientation.Portrait), ("Landscape", Orientation.Landscape) },
            () => print.Orientation, v => print.Orientation = v)));
        var source = Check("Each sheet as big as its page (mixed sizes)", () => print.UseSourceSize, v => print.UseSourceSize = v);
        p.Children.Add(source);
        ShowWhen(source, () => print.Mode == LayoutMode.Normal);

        // Mode specific
        var nup = Stack(8);
        nup.Padding = new Thickness(0);
        nup.Children.Add(Header("Pages per sheet"));
        nup.Children.Add(Row("Rows × columns", Pair(Num(1, 12, 1, () => print.Rows, v => print.Rows = (int)v), Num(1, 12, 1, () => print.Columns, v => print.Columns = (int)v))));
        nup.Children.Add(Row("Order", Combo(new[]
        {
            ("Across, then down", PageOrder.RightThenDown), ("Right to left, then down", PageOrder.LeftThenDown),
            ("Down, then across", PageOrder.DownThenRight), ("Down, then right to left", PageOrder.DownThenLeft),
        }, () => print.Order, v => print.Order = v)));
        p.Children.Add(nup);
        ShowWhen(nup, () => print.Mode == LayoutMode.NUp);

        var join = Stack(8);
        join.Padding = new Thickness(0);
        join.Children.Add(Header("Join"));
        join.Children.Add(Row("Pages in a row", Num(2, 12, 1, () => print.JoinCount, v => print.JoinCount = (int)v)));
        join.Children.Add(Check("Trim empty margins first (smart mode for Excel)", () => print.JoinSmart, v => print.JoinSmart = v));
        join.Children.Add(Note("Use this for tables that run across several pages: the pages are joined edge to edge on one sheet."));
        p.Children.Add(join);
        ShowWhen(join, () => print.Mode == LayoutMode.Join);

        var booklet = Stack(8);
        booklet.Padding = new Thickness(0);
        booklet.Children.Add(Header("Booklet"));
        booklet.Children.Add(Row("Binding", Combo(new[] { ("Left (English)", BookletBinding.Left), ("Right (Persian, Arabic)", BookletBinding.Right) }, () => print.Binding, v => print.Binding = v)));
        booklet.Children.Add(Row("Fold", Combo(new[] { ("Side by side", BookletAxis.Horizontal), ("Top and bottom", BookletAxis.Vertical) }, () => print.BookletAxis, v => print.BookletAxis = v)));
        booklet.Children.Add(Row("Sheets per fold", Num(0, 40, 1, () => print.SheetsPerFold, v => print.SheetsPerFold = (int)v)));
        booklet.Children.Add(Note("0 folds everything as one stack. Thick booklets are easier to fold in stacks of 4 to 8 sheets."));
        p.Children.Add(booklet);
        ShowWhen(booklet, () => print.Mode == LayoutMode.Booklet);

        var poster = Stack(8);
        poster.Padding = new Thickness(0);
        poster.Children.Add(Header("Poster"));
        poster.Children.Add(Row("Sheets across × down", Pair(Num(1, 12, 1, () => print.PosterColumns, v => print.PosterColumns = (int)v), Num(1, 12, 1, () => print.PosterRows, v => print.PosterRows = (int)v))));
        poster.Children.Add(Row("Overlap (mm)", Num(0, 60, 1, () => print.PosterOverlapMm, v => print.PosterOverlapMm = v)));
        poster.Children.Add(Check("Cut marks", () => print.PosterMarks, v => print.PosterMarks = v));
        poster.Children.Add(Check("Row and column labels", () => print.PosterLabels, v => print.PosterLabels = v));
        p.Children.Add(poster);
        ShowWhen(poster, () => print.Mode == LayoutMode.Poster);

        // Margins and spacing
        p.Children.Add(Header("Margins (mm)"));
        p.Children.Add(Pair(Labeled("Top", Num(0, 100, 1, () => print.MarginTopMm, v => print.MarginTopMm = v)), Labeled("Bottom", Num(0, 100, 1, () => print.MarginBottomMm, v => print.MarginBottomMm = v))));
        p.Children.Add(Pair(Labeled("Left", Num(0, 100, 1, () => print.MarginLeftMm, v => print.MarginLeftMm = v)), Labeled("Right", Num(0, 100, 1, () => print.MarginRightMm, v => print.MarginRightMm = v))));
        var gutters = Pair(Labeled("Between columns", Num(0, 60, 1, () => print.GutterXMm, v => print.GutterXMm = v)), Labeled("Between rows", Num(0, 60, 1, () => print.GutterYMm, v => print.GutterYMm = v)));
        p.Children.Add(gutters);
        ShowWhen(gutters, () => print.Mode is LayoutMode.NUp or LayoutMode.Booklet);

        // Scale
        p.Children.Add(Header("Scale and position"));
        p.Children.Add(Row("Scale", Combo(new[]
        {
            ("Fit to the sheet", ScaleMode.Fit), ("Shrink to fit only", ScaleMode.ShrinkOnly), ("Fill and trim", ScaleMode.Fill),
            ("Actual size", ScaleMode.Actual), ("Custom %", ScaleMode.Custom),
        }, () => print.Scale, v => print.Scale = v)));
        var percent = Row("Percent", Num(5, 800, 5, () => print.ScalePercent, v => print.ScalePercent = v));
        p.Children.Add(percent);
        ShowWhen(percent, () => print.Scale == ScaleMode.Custom);
        p.Children.Add(Row("Position", Combo(AnchorOptions, () => print.ContentAnchor, v => print.ContentAnchor = v)));
        p.Children.Add(Check("Turn pages to use the sheet better", () => print.AutoRotate, v => print.AutoRotate = v));

        // Decoration
        p.Children.Add(Header("Page decoration"));
        p.Children.Add(Pair(Check("Page borders", () => print.PageBorders, v => print.PageBorders = v), Check("Crop marks", () => print.CropMarks, v => print.CropMarks = v)));
        p.Children.Add(Pair(Check("Mirror left to right", () => print.FlipHorizontal, v => print.FlipHorizontal = v), Check("Mirror top to bottom", () => print.FlipVertical, v => print.FlipVertical = v)));
        return p;
    }

    private static readonly (string, Anchor)[] AnchorOptions =
    [
        ("Centre", Anchor.Center), ("Top left", Anchor.TopLeft), ("Top", Anchor.Top), ("Top right", Anchor.TopRight), ("Left", Anchor.Left), ("Right", Anchor.Right),
        ("Bottom left", Anchor.BottomLeft), ("Bottom", Anchor.Bottom), ("Bottom right", Anchor.BottomRight),
    ];

    private static StackPanel Labeled(string label, FrameworkElement control) =>
        new() { Spacing = 3, Children = { new TextBlock { Text = label, Style = (Style)Application.Current.Resources["MutedText"] }, control } };

    // ---- Pages tab ----

    private FrameworkElement BuildPagesTab()
    {
        var p = Stack();
        p.Children.Add(Header("Files in this job"));
        jobsPanel = new StackPanel { Spacing = 4 };
        p.Children.Add(jobsPanel);
        p.Children.Add(Pair(Action_("Add files…", () => _ = PickAndOpenAsync()), Action_("Clear all", ClearAll)));

        p.Children.Add(Header("Which pages"));
        var range = Text(() => print.PageRange, v => print.PageRange = v, "All pages, or for example 1-5, 8, 10-");
        range.TextChanged += (_, _) => range.BorderBrush = LayoutEngine.IsValidRange(range.Text) ? null : new SolidColorBrush(ColorHelper.FromArgb(255, 0xE3, 0x48, 0x50));
        p.Children.Add(Row("Page range", range));
        p.Children.Add(Row("Print", Combo(new[] { ("All pages", OddEven.All), ("Odd pages only", OddEven.Odd), ("Even pages only", OddEven.Even) }, () => print.OddEven, v => print.OddEven = v)));
        p.Children.Add(Check("Reverse the order", () => print.Reverse, v => print.Reverse = v));
        p.Children.Add(Check("Leave out blank pages", () => print.SkipBlankPages, v => print.SkipBlankPages = v));

        p.Children.Add(Header("Selected page"));
        p.Children.Add(Note("Click a page in the preview to select it. With nothing selected, these act on the sheet you are viewing."));
        p.Children.Add(Pair(Action_("Insert blank page after", InsertBlankAfter), Action_("Duplicate", DuplicateTargets)));
        p.Children.Add(Action_("New blank page (A0–A6, custom)…", () => _ = NewBlankPageAsync()));
        p.Children.Add(Pair(Action_("Move earlier", () => MoveTargets(-1)), Action_("Move later", () => MoveTargets(+1))));
        p.Children.Add(Pair(Action_("Remove", DeleteTargets), Action_("Restore removed pages", RestoreRemoved)));
        p.Children.Add(Header("Picture on the page"));
        p.Children.Add(Note("Add a picture to the selected page, then drag it in the preview to move it."));
        p.Children.Add(Action_("Add picture…", () => _ = AddPictureAsync()));
        p.Children.Add(Pair(Action_("Smaller", () => ResizePicture(0.9f)), Action_("Bigger", () => ResizePicture(1.1f))));
        p.Children.Add(Pair(Action_("Fit to page", FitPicture), Action_("Centre", CentrePicture)));
        p.Children.Add(Action_("Remove picture", RemovePicture));
        p.Children.Add(Action_("Remove all marks on this page (redactions, highlights, text, pictures)", () =>
        {
            var targets = TargetIndexes();
            if (targets.Count == 0) return;
            doc.ClearAnnotations(targets);
            DoRelayout();
        }));
        return p;
    }

    private void RebuildJobs()
    {
        if (jobsPanel is null) return;
        jobsPanel.Children.Clear();
        if (doc.Jobs.Count == 0) { jobsPanel.Children.Add(Note("Nothing added yet.")); return; }
        foreach (var job in doc.Jobs.ToList())
        {
            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(10, 6, 6, 6), CornerRadius = new CornerRadius(4), Background = (Brush)Application.Current.Resources["TileBrush"] };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel { Spacing = 1 };
            info.Children.Add(new TextBlock { Text = job.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock { Text = $"{job.PageCount} {(job.PageCount == 1 ? "page" : "pages")}", Style = (Style)Application.Current.Resources["MutedText"] });
            row.Children.Add(info);
            var remove = new Button { Style = (Style)Application.Current.Resources["ToolButton"], Width = 30, Height = 30, Content = new FontIcon { Glyph = "", FontSize = 11, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") } };
            ToolTipService.SetToolTip(remove, "Take this file out of the job");
            remove.Click += (_, _) => { doc.RemoveJob(job); selected = null; DoRelayout(); };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            jobsPanel.Children.Add(row);
        }
    }

    private void ClearAll()
    {
        if (doc.Pages.Count == 0) return;
        doc.Clear();
        selected = null;
        current = 0;
        DoRelayout();
        ShowToast("Cleared. Ctrl+Z brings everything back.");
    }

    private void InsertBlankAfter()
    {
        var targets = TargetIndexes();
        var at = targets.Count == 0 ? doc.Pages.Count : targets.Max() + 1;
        var (w, h) = Paper.Points(print);
        doc.InsertBlank(at, w, h);
        DoRelayout();
    }

    private void DuplicateTargets()
    {
        var targets = TargetIndexes();
        if (targets.Count == 0) return;
        doc.Duplicate(targets);
        DoRelayout();
    }

    private void MoveTargets(int direction)
    {
        var targets = TargetIndexes();
        if (targets.Count == 0) return;
        var target = direction < 0 ? Math.Max(0, targets.Min() - 1) : Math.Min(doc.Pages.Count, targets.Max() + 2);
        doc.Move(targets, target);
        DoRelayout();
    }

    private void RestoreRemoved()
    {
        var removed = doc.Pages.Select((p, i) => (p, i)).Where(x => x.p.Deleted).Select(x => x.i).ToList();
        if (removed.Count == 0) { ShowToast("No pages have been removed."); return; }
        doc.SetDeleted(removed, false);
        DoRelayout();
    }

    // ---- Marks tab ----

    private FrameworkElement BuildMarksTab()
    {
        var p = Stack();
        p.Children.Add(Header("Watermarks, notes and page numbers"));
        marksList = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 150, MinHeight = 44, CornerRadius = new CornerRadius(4), Background = (Brush)Application.Current.Resources["TileBrush"] };
        marksList.SelectionChanged += (_, _) =>
        {
            markEditor.Visibility = Mark is null ? Visibility.Collapsed : Visibility.Visible;
            syncing = true;
            try { foreach (var sync in markSyncs) sync(); } finally { syncing = false; }
            foreach (var update in visibilities) update();
        };
        p.Children.Add(marksList);

        var add = Pair(Action_("Add text", () => AddMark(new Watermark())), Action_("Add picture…", () => _ = AddImageMarkAsync()));
        p.Children.Add(add);
        p.Children.Add(Pair(Action_("Add page numbers", () => AddMark(Watermark.PageNumber())), Action_("Remove", RemoveMark)));

        markEditor = Stack(8);
        markEditor.Padding = new Thickness(0);
        markEditor.Visibility = Visibility.Collapsed;
        var m = markSyncs;
        markEditor.Children.Add(Row("Name", Text(() => Mark?.Name ?? "", v => { if (Mark is { } w) w.Name = v; }, "", m)));
        markEditor.Children.Add(Check("Show this mark", () => Mark?.Enabled ?? true, v => { if (Mark is { } w) w.Enabled = v; }, m));

        var textPanel = Stack(8);
        textPanel.Padding = new Thickness(0);
        textPanel.Children.Add(Row("Text", Text(() => Mark?.Text ?? "", v => { if (Mark is { } w) w.Text = v; }, "{page} / {pages}", m)));
        textPanel.Children.Add(Note("Macros: {page} {pages} {sheet} {sheets} {file} {date} {time} {user} {computer}"));
        var fonts = System.Drawing.FontFamily.Families.Select(f => (f.Name, f.Name)).ToArray();
        textPanel.Children.Add(Row("Font", Combo(fonts, () => Mark?.FontFamily ?? "Segoe UI", v => { if (Mark is { } w) w.FontFamily = v; }, m)));
        textPanel.Children.Add(Row("Size (pt)", Num(2, 600, 1, () => Mark?.FontSize ?? 12, v => { if (Mark is { } w) w.FontSize = (float)v; }, m)));
        textPanel.Children.Add(Pair(Check("Bold", () => Mark?.Bold ?? false, v => { if (Mark is { } w) w.Bold = v; }, m), Check("Italic", () => Mark?.Italic ?? false, v => { if (Mark is { } w) w.Italic = v; }, m)));
        textPanel.Children.Add(Row("Colour", ColourButton()));
        markEditor.Children.Add(textPanel);
        ShowWhen(textPanel, () => Mark?.Kind != WatermarkKind.Image);

        var imagePanel = Stack(8);
        imagePanel.Padding = new Thickness(0);
        imagePanel.Children.Add(Row("Width (% of page)", Num(2, 100, 1, () => (Mark?.ImageWidth ?? 0.4) * 100, v => { if (Mark is { } w) w.ImageWidth = v / 100; }, m)));
        markEditor.Children.Add(imagePanel);
        ShowWhen(imagePanel, () => Mark?.Kind == WatermarkKind.Image);

        markEditor.Children.Add(Row("Opacity", Slide(0, 100, () => (Mark?.Opacity ?? 0.3) * 100, v => { if (Mark is { } w) w.Opacity = v / 100; }, m)));
        markEditor.Children.Add(Row("Angle", Slide(-180, 180, () => Mark?.Angle ?? 0, v => { if (Mark is { } w) w.Angle = v; }, m)));
        markEditor.Children.Add(Row("Position", Combo(AnchorOptions, () => Mark?.Anchor ?? Anchor.Center, v => { if (Mark is { } w) w.Anchor = v; }, m)));
        markEditor.Children.Add(Row("Move (mm)", Pair(Num(-300, 300, 1, () => Mark?.OffsetXMm ?? 0, v => { if (Mark is { } w) w.OffsetXMm = v; }, m), Num(-300, 300, 1, () => Mark?.OffsetYMm ?? 0, v => { if (Mark is { } w) w.OffsetYMm = v; }, m))));
        markEditor.Children.Add(Check("Repeat across the page", () => Mark?.Tile ?? false, v => { if (Mark is { } w) w.Tile = v; }, m));
        markEditor.Children.Add(Row("Draw on", Combo(new[] { ("Every page", WatermarkScope.PerPage), ("Every sheet", WatermarkScope.PerSheet) }, () => Mark?.Scope ?? WatermarkScope.PerPage, v => { if (Mark is { } w) w.Scope = v; }, m)));
        markEditor.Children.Add(Row("Which", Combo(new[]
        {
            ("All", WatermarkPages.All), ("First only", WatermarkPages.First), ("Last only", WatermarkPages.Last), ("Odd", WatermarkPages.Odd), ("Even", WatermarkPages.Even),
        }, () => Mark?.Pages ?? WatermarkPages.All, v => { if (Mark is { } w) w.Pages = v; }, m)));
        p.Children.Add(markEditor);

        p.Children.Add(Header("Letterhead or form"));
        formLabel = Note("None");
        p.Children.Add(formLabel);
        p.Children.Add(Pair(Action_("Choose file…", () => _ = ChooseFormAsync()), Action_("Remove", () => { print.FormPath = ""; UpdateFormLabel(); Changed(); })));
        p.Children.Add(Check("First sheet only", () => print.FormFirstSheetOnly, v => print.FormFirstSheetOnly = v));
        p.Children.Add(Note("The form is printed behind every sheet: invoices on a letterhead, or stock forms."));
        return p;
    }

    private Button ColourButton()
    {
        var swatch = new Border { Width = 28, Height = 18, CornerRadius = new CornerRadius(3), BorderBrush = (Brush)Application.Current.Resources["BorderBrush2"], BorderThickness = new Thickness(1) };
        var button = new Button { Content = swatch, Style = (Style)Application.Current.Resources["TileButton"], HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 0, 10, 0) };
        var picker = new ColorPicker { IsAlphaEnabled = false, IsHexInputVisible = true, IsMoreButtonVisible = false };
        button.Flyout = new Flyout { Content = picker };
        markSyncs.Add(() =>
        {
            var c = OverlayMath.ParseColor(Mark?.Color ?? "#000000");
            var color = ColorHelper.FromArgb(255, c.R, c.G, c.B);
            swatch.Background = new SolidColorBrush(color);
            if (picker.Color != color) picker.Color = color;
        });
        picker.ColorChanged += (_, e) =>
        {
            if (syncing || Mark is not { } w) return;
            w.Color = $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}";
            swatch.Background = new SolidColorBrush(e.NewColor);
            Changed();
        };
        return button;
    }

    private void RefreshMarks(int? select = null)
    {
        if (marksList is null) return;
        var keep = select ?? marksList.SelectedIndex;
        marksList.Items.Clear();
        foreach (var w in print.Watermarks)
            marksList.Items.Add(w.Kind == WatermarkKind.Image ? $"{w.Name} (picture)" : $"{w.Name} · {Trim(w.Text)}");
        marksList.SelectedIndex = print.Watermarks.Count == 0 ? -1 : Math.Clamp(keep, 0, print.Watermarks.Count - 1);
        UpdateFormLabel();
    }

    private static string Trim(string text) => text.Length > 22 ? text[..22] + "…" : text;

    private void AddMark(Watermark mark)
    {
        print.Watermarks.Add(mark);
        RefreshMarks(print.Watermarks.Count - 1);
        Changed();
    }

    private async Task AddImageMarkAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, Hwnd);
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" }) picker.FileTypeFilter.Add(ext);
        if (await picker.PickSingleFileAsync() is not { } file) return;
        AddMark(new Watermark { Kind = WatermarkKind.Image, Name = Path.GetFileNameWithoutExtension(file.Path), ImagePath = file.Path, Opacity = 0.5, Angle = 0, Anchor = Anchor.BottomRight });
    }

    private void RemoveMark()
    {
        if (Mark is not { } w) return;
        print.Watermarks.Remove(w);
        RefreshMarks();
        Changed();
    }

    private async Task ChooseFormAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, Hwnd);
        foreach (var ext in new[] { ".pdf", ".png", ".jpg", ".jpeg" }) picker.FileTypeFilter.Add(ext);
        if (await picker.PickSingleFileAsync() is not { } file) return;
        print.FormPath = file.Path;
        UpdateFormLabel();
        Changed();
    }

    private void UpdateFormLabel()
    {
        if (formLabel is null) return;
        formLabel.Text = string.IsNullOrEmpty(print.FormPath) ? "None" : Path.GetFileName(print.FormPath);
    }

    // ---- Effects tab ----

    private FrameworkElement BuildEffectsTab()
    {
        var p = Stack();
        p.Children.Add(Header("Colour"));
        p.Children.Add(Pair(Check("Grayscale", () => print.Grayscale, v => print.Grayscale = v), Check("Invert colours", () => print.Invert, v => print.Invert = v)));
        p.Children.Add(Note("Inverting suits scanned documents with a dark background: it turns them into black text on white paper."));
        p.Children.Add(Row("Brightness", Slide(-100, 100, () => print.Brightness, v => print.Brightness = (int)v)));
        p.Children.Add(Row("Contrast", Slide(-100, 100, () => print.Contrast, v => print.Contrast = (int)v)));

        p.Children.Add(Header("Ink saving"));
        p.Children.Add(Row("Lighten", Slide(0, 80, () => print.InkSaving, v => print.InkSaving = (int)v)));
        p.Children.Add(Note("Lightens the whole page to use less ink or toner. The ink estimate in the status bar shows what it saves on the sheet you are viewing."));
        p.Children.Add(Action_("Reset effects", () =>
        {
            print.Grayscale = print.Invert = false;
            print.Brightness = print.Contrast = print.InkSaving = 0;
            SyncInspector();
            Changed();
        }));
        return p;
    }

    // ---- Output tab ----

    private FrameworkElement BuildOutputTab()
    {
        var p = Stack();
        p.Children.Add(Header("Printer"));
        printerBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        printerBox.SelectionChanged += (_, _) =>
        {
            if (syncing || printerBox.SelectedItem is not string name) return;
            print.Printer = name;
            Changed();
        };
        syncs.Add(RefreshPrinters);
        p.Children.Add(Row("Printer", printerBox));
        p.Children.Add(Row("Copies", Num(1, 999, 1, () => print.Copies, v => print.Copies = (int)v)));
        p.Children.Add(Check("Collate copies", () => print.Collate, v => print.Collate = v));
        p.Children.Add(Row("Two-sided", Combo(new[]
        {
            ("One side only", DuplexMode.OneSided), ("Automatic, long edge", DuplexMode.LongEdge), ("Automatic, short edge", DuplexMode.ShortEdge),
            ("By hand, long edge", DuplexMode.ManualLongEdge), ("By hand, short edge", DuplexMode.ManualShortEdge),
        }, () => print.Duplex, v => print.Duplex = v)));
        p.Children.Add(Note("By hand: the front sides print first, then you are asked to turn the stack over and the backs follow."));
        p.Children.Add(Row("Print quality", Combo(new[] { ("150 dpi (draft)", 150), ("300 dpi", 300), ("600 dpi", 600), ("1200 dpi", 1200) }, () => print.PrintDpi, v => print.PrintDpi = v)));
        p.Children.Add(Check("Send as pictures (for printers with weak drivers)", () => print.BitmapMode, v => print.BitmapMode = v));

        p.Children.Add(Header("PDF"));
        p.Children.Add(Check("Keep text and drawings sharp when possible", () => print.PdfKeepVector, v => print.PdfKeepVector = v));
        p.Children.Add(Note("Sheets with effects, redactions, scans or pictures are saved as pictures at the quality below; the rest stay vector and small."));
        p.Children.Add(Row("Picture quality", Combo(new[] { ("100 dpi (smallest)", 100), ("150 dpi", 150), ("200 dpi", 200), ("300 dpi", 300), ("600 dpi (largest)", 600) }, () => print.PdfDpi, v => print.PdfDpi = v)));
        p.Children.Add(Row("JPEG quality", Slide(40, 100, () => print.PdfJpegQuality, v => print.PdfJpegQuality = (int)v)));
        p.Children.Add(Row("Title", Text(() => print.PdfTitle, v => print.PdfTitle = v, "Document title")));
        p.Children.Add(Row("Author", Text(() => print.PdfAuthor, v => print.PdfAuthor = v, "Your name")));
        p.Children.Add(Header("Protect the PDF"));
        p.Children.Add(Row("Open password", PasswordField(() => print.PdfUserPassword, v => print.PdfUserPassword = v)));
        p.Children.Add(Row("Owner password", PasswordField(() => print.PdfOwnerPassword, v => print.PdfOwnerPassword = v)));
        p.Children.Add(Check("Allow printing", () => print.PdfAllowPrint, v => print.PdfAllowPrint = v));
        p.Children.Add(Check("Allow copying text", () => print.PdfAllowCopy, v => print.PdfAllowCopy = v));
        p.Children.Add(Check("Allow editing", () => print.PdfAllowEdit, v => print.PdfAllowEdit = v));
        p.Children.Add(Note("Passwords are never saved in themes."));

        p.Children.Add(Header("Pictures"));
        p.Children.Add(Row("Format", Combo(new[] { ("PNG", RasterFormat.Png), ("JPEG", RasterFormat.Jpeg), ("TIFF", RasterFormat.Tiff), ("BMP", RasterFormat.Bmp), ("TGA", RasterFormat.Tga) }, () => print.ImageFormat, v => print.ImageFormat = v)));
        p.Children.Add(Row("Resolution", Combo(new[] { ("100 dpi", 100), ("150 dpi", 150), ("200 dpi", 200), ("300 dpi", 300), ("600 dpi", 600) }, () => print.ImageDpi, v => print.ImageDpi = v)));

        p.Children.Add(Header("Virtual printer"));
        p.Children.Add(Check("Open PDFs that arrive in the inbox folder", () => app.WatchInbox, v => { app.WatchInbox = v; StartInboxWatcher(); }));
        p.Children.Add(Note("Print from any program to the “Every Printer” printer and the pages appear here. Install it once:"));
        p.Children.Add(Pair(Action_("Install printer…", () => _ = InstallVirtualPrinterAsync(true)), Action_("Remove printer…", () => _ = InstallVirtualPrinterAsync(false))));
        p.Children.Add(Action_("Open the inbox folder", () => OpenFolder(app.InboxFolder)));
        return p;
    }

    private PasswordBox PasswordField(Func<string> get, Action<string> set)
    {
        var box = new PasswordBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        syncs.Add(() => { if (box.Password != get()) box.Password = get(); });
        box.PasswordChanged += (_, _) =>
        {
            if (syncing) return;
            set(box.Password);
        };
        return box;
    }

    private void RefreshPrinters()
    {
        if (printerBox is null) return;
        var names = PrintService.Printers();
        printerBox.Items.Clear();
        foreach (var name in names) printerBox.Items.Add(name);
        if (names.Count == 0) { printerBox.PlaceholderText = "No printers installed"; return; }
        var index = names.ToList().IndexOf(print.Printer);
        if (index < 0)
        {
            index = Math.Max(0, names.ToList().IndexOf(PrintService.DefaultPrinter()));
            print.Printer = names[index];
        }
        printerBox.SelectedIndex = index;
    }

    // ---- Themes ----

    private bool loadingThemes;

    private void RefreshThemeList()
    {
        loadingThemes = true;
        ThemeBox.Items.Clear();
        foreach (var name in ThemeStore.Names()) ThemeBox.Items.Add(name);
        var index = ThemeBox.Items.IndexOf(app.ThemeName);
        ThemeBox.SelectedIndex = index < 0 ? 0 : index;
        loadingThemes = false;
    }

    private void ThemeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (loadingThemes || ThemeBox.SelectedItem is not string name) return;
        var printer = print.Printer;
        print = ThemeStore.Load(name);
        if (string.IsNullOrEmpty(print.Printer) || !PrintService.Printers().Contains(print.Printer)) print.Printer = printer;
        app.ThemeName = name;
        app.Save();
        SyncInspector();
        DoRelayout();
        ShowToast($"Theme “{name}” applied.");
    }

    private void ThemeSaveClick(object sender, RoutedEventArgs e)
    {
        var name = ThemeBox.SelectedItem as string ?? ThemeStore.DefaultName;
        if (name == ThemeStore.DefaultName) { _ = SaveThemeAsAsync(); return; }
        ThemeStore.Save(name, print);
        ShowToast($"Theme “{name}” saved.");
    }

    private void ThemeSaveAsClick(object sender, RoutedEventArgs e) => _ = SaveThemeAsAsync();

    private async Task SaveThemeAsAsync()
    {
        var box = new TextBox { PlaceholderText = "Theme name", MinWidth = 280 };
        var result = await ShowDialogAsync(new ContentDialog
        {
            Title = "Save theme",
            Content = new StackPanel { Spacing = 8, Children = { box, Note("Saves the layout, margins, effects, marks and output choices. Passwords are left out.") } },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (result != ContentDialogResult.Primary || box.Text.Trim().Length == 0) return;
        var name = ThemeStore.Sanitize(box.Text);
        if (name == ThemeStore.DefaultName) name = "My theme";
        ThemeStore.Save(name, print);
        app.ThemeName = name;
        RefreshThemeList();
        ShowToast($"Theme “{name}” saved.");
    }

    private void ThemeDeleteClick(object sender, RoutedEventArgs e)
    {
        var name = ThemeBox.SelectedItem as string;
        if (name is null || name == ThemeStore.DefaultName) { ShowToast("The Default theme can't be deleted."); return; }
        ThemeStore.Delete(name);
        app.ThemeName = ThemeStore.DefaultName;
        RefreshThemeList();
        print = ThemeStore.Load(ThemeStore.DefaultName);
        print.Printer = PrintService.DefaultPrinter();
        SyncInspector();
        DoRelayout();
    }

    private async void ThemeImportClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeFilter.Add(".json");
        if (await picker.PickSingleFileAsync() is not { } file) return;
        try
        {
            var imported = PrintSettings.FromJson(await File.ReadAllTextAsync(file.Path));
            var name = ThemeStore.Sanitize(Path.GetFileNameWithoutExtension(file.Path));
            ThemeStore.Save(name, imported);
            app.ThemeName = name;
            RefreshThemeList();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            ShowToast("That file isn't a theme Every Printer can read.");
        }
    }

    private async void ThemeExportClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedFileName = ThemeBox.SelectedItem as string ?? "theme" };
        InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeChoices.Add("Theme", [".json"]);
        if (await picker.PickSaveFileAsync() is not { } file) return;
        var copy = print.Clone();
        copy.PdfUserPassword = copy.PdfOwnerPassword = "";
        await File.WriteAllTextAsync(file.Path, copy.ToJson());
        ShowToast("Theme exported.");
    }

    private void ThemeFolderClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ThemeStore.Folder);
        OpenFolder(ThemeStore.Folder);
    }
}
