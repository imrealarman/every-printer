using System.Drawing;
using EveryPrinter.Models;
using EveryPrinter.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EveryPrinter;

/// <summary>Blank pages of any paper size, and pictures placed on a page that can be dragged around, resized and removed.</summary>
public sealed partial class MainWindow
{
    private static readonly string[] PictureExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"];

    private Annotation? selectedPicture;
    private Annotation? dragPicture;
    private PointF dragPictureStart;
    private RectangleF dragPictureRect;
    private bool dragPictureBegun;
    private long lastPictureLayout;

    // ---- New blank page ----

    private async Task NewBlankPageAsync()
    {
        var paper = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var size in Paper.Sizes) paper.Items.Add($"{size.Name}  ({size.Width:0.#} × {size.Height:0.#} mm)");
        paper.Items.Add("Custom size");
        paper.SelectedIndex = Math.Max(0, Paper.Sizes.ToList().FindIndex(s => s.Name == print.Paper));

        var orientation = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Items = { "Portrait", "Landscape" }, SelectedIndex = 0 };
        var width = new NumberBox { Header = "Width (mm)", Minimum = 20, Maximum = 5000, Value = 210, Visibility = Visibility.Collapsed, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var height = new NumberBox { Header = "Height (mm)", Minimum = 20, Maximum = 5000, Value = 297, Visibility = Visibility.Collapsed, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        paper.SelectionChanged += (_, _) => width.Visibility = height.Visibility = paper.SelectedIndex == Paper.Sizes.Count ? Visibility.Visible : Visibility.Collapsed;
        var pickNext = new CheckBox { Content = "Choose a picture for it next", IsChecked = true };
        var printOnIt = new CheckBox { Content = "Print on this paper size", IsChecked = true };

        var form = new StackPanel { Spacing = 10, MinWidth = 340 };
        form.Children.Add(new TextBlock { Text = "Paper", Style = (Style)Application.Current.Resources["PanelHeader"] });
        form.Children.Add(paper);
        form.Children.Add(orientation);
        form.Children.Add(width);
        form.Children.Add(height);
        form.Children.Add(pickNext);
        form.Children.Add(printOnIt);

        var result = await ShowDialogAsync(new ContentDialog
        {
            Title = "New blank page",
            Content = form,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (result != ContentDialogResult.Primary) return;

        var custom = paper.SelectedIndex >= Paper.Sizes.Count;
        double mmW, mmH;
        string name;
        if (custom) { mmW = width.Value; mmH = height.Value; name = "Custom"; }
        else { var size = Paper.Sizes[Math.Max(0, paper.SelectedIndex)]; mmW = size.Width; mmH = size.Height; name = size.Name; }
        if (double.IsNaN(mmW) || double.IsNaN(mmH)) { ShowToast("Enter a width and a height."); return; }
        var landscape = orientation.SelectedIndex == 1;

        var targets = TargetIndexes();
        var at = targets.Count == 0 ? doc.Pages.Count : targets.Max() + 1;
        var page = doc.InsertBlank(at, Paper.Mm(landscape ? mmH : mmW), Paper.Mm(landscape ? mmW : mmH), $"Blank {name}");

        if (printOnIt.IsChecked == true)
        {
            print.Paper = name;
            if (custom) { print.CustomWidthMm = mmW; print.CustomHeightMm = mmH; }
            print.Orientation = landscape ? Models.Orientation.Landscape : Models.Orientation.Portrait;
            SyncInspector();
        }
        selected = page;
        selectedPicture = null;
        DoRelayout();
        var sheetIndex = sheets.FindIndex(s => s.Placements.Any(p => p.Page == page));
        if (sheetIndex >= 0) SelectSheet(sheetIndex, fit: false);
        if (pickNext.IsChecked == true) await AddPictureAsync();
    }

    // ---- Pictures on a page ----

    /// <summary>The page the next picture goes on: the selected page, or the first page on the sheet being viewed.</summary>
    private PageRef? PicturePage()
    {
        var targets = TargetIndexes();
        return targets.Count == 0 ? null : doc.Pages[targets[0]];
    }

    private async Task AddPictureAsync()
    {
        var page = PicturePage();
        if (page is null) { ShowToast("Add a page first, then add a picture to it."); return; }

        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        foreach (var ext in PictureExtensions) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        double aspect;
        try
        {
            using var image = System.Drawing.Image.FromFile(file.Path);
            aspect = (double)image.Width / Math.Max(1, image.Height);
        }
        catch (Exception ex) when (ex is OutOfMemoryException or IOException or ArgumentException)
        {
            ShowToast("That picture could not be read. Use a JPEG, PNG, BMP, GIF or TIFF file.");
            return;
        }

        var index = doc.Pages.IndexOf(page);
        if (index < 0) return;
        // Start at 80% of the page, centred, whatever the shape of the picture.
        var widthFraction = Math.Min(0.8, 0.8 * page.Height * aspect / page.Width);
        var heightFraction = widthFraction * page.Width / aspect / page.Height;
        var rect = new RectangleF((float)((1 - widthFraction) / 2), (float)((1 - heightFraction) / 2), (float)widthFraction, (float)heightFraction);
        doc.AddAnnotation(index, new Annotation { Kind = AnnotationKind.Picture, Rect = rect, Text = file.Path });
        selected = doc.Pages[index];
        selectedPicture = doc.Pages[index].Annotations[^1];
        DoRelayout();
        ShowToast("Drag the picture to move it. Use the picture buttons on the right to resize it.");
    }

    /// <summary>The picture the buttons act on: the one clicked, else the last one on the target page.</summary>
    private (PageRef Page, Annotation Picture)? CurrentPicture()
    {
        var page = PicturePage();
        if (page is null) return null;
        var picture = selectedPicture is not null && page.Annotations.Contains(selectedPicture)
            ? selectedPicture
            : page.Annotations.LastOrDefault(a => a.Kind == AnnotationKind.Picture);
        return picture is null ? null : (page, picture);
    }

    private void EditPicture(Func<PageRef, Annotation, RectangleF> change)
    {
        if (CurrentPicture() is not var (page, picture)) { ShowToast("There is no picture on this page."); return; }
        doc.BeginEdit();
        picture.Rect = change(page, picture);
        selected = page;
        selectedPicture = picture;
        doc.Touch();
        DoRelayout();
    }

    private void ResizePicture(float factor) => EditPicture((_, a) =>
    {
        var r = a.Rect;
        var w = r.Width * factor;
        var h = r.Height * factor;
        return new RectangleF(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
    });

    private void FitPicture() => EditPicture((page, a) =>
    {
        var aspect = a.Rect.Width * page.Width / (a.Rect.Height * page.Height);
        var w = Math.Min(1f, (float)(page.Height * aspect / page.Width));
        var h = (float)(w * page.Width / aspect / page.Height);
        return new RectangleF((1 - w) / 2, (1 - h) / 2, w, h);
    });

    private void CentrePicture() => EditPicture((_, a) => new RectangleF((1 - a.Rect.Width) / 2, (1 - a.Rect.Height) / 2, a.Rect.Width, a.Rect.Height));

    private void RemovePicture()
    {
        if (CurrentPicture() is not var (page, picture)) { ShowToast("There is no picture on this page."); return; }
        doc.BeginEdit();
        page.Annotations.Remove(picture);
        selectedPicture = null;
        doc.Touch();
        DoRelayout();
    }

    // ---- Dragging a picture in the preview ----

    private Annotation? PictureAt(Placement placement, Windows.Foundation.Point host)
    {
        var p = SheetPoint(host);
        return placement.Page.Annotations.LastOrDefault(a => a.Kind == AnnotationKind.Picture && placement.MapRect(a.Rect).Contains(p.X, p.Y));
    }

    private void BeginPictureDrag(Placement placement, Windows.Foundation.Point host)
    {
        dragPicture = tool == "Select" ? PictureAt(placement, host) : null;
        dragPictureBegun = false;
        if (dragPicture is null) return;
        var p = SheetPoint(host);
        dragPictureStart = placement.Unmap(p.X, p.Y);
        dragPictureRect = dragPicture.Rect;
    }

    private void DragPicture(Placement placement, Windows.Foundation.Point host)
    {
        if (dragPicture is null) return;
        if (!dragPictureBegun) { doc.BeginEdit(); dragPictureBegun = true; }
        var p = SheetPoint(host);
        var now = placement.Unmap(p.X, p.Y);
        var r = dragPictureRect;
        // Keep a corner of the picture on the page so it can't be lost.
        var x = Math.Clamp(r.X + now.X - dragPictureStart.X, 0.05f - r.Width, 0.95f);
        var y = Math.Clamp(r.Y + now.Y - dragPictureStart.Y, 0.05f - r.Height, 0.95f);
        dragPicture.Rect = new RectangleF(x, y, r.Width, r.Height);
        doc.Touch();
        var tick = Environment.TickCount64;
        if (tick - lastPictureLayout < 40) return;
        lastPictureLayout = tick;
        DoRelayout();
    }

    private void EndPictureDrag()
    {
        var finished = dragPictureBegun;
        dragPicture = null;
        dragPictureBegun = false;
        if (finished) DoRelayout();
    }
}
