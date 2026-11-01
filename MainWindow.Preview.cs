using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using EveryPrinter.Models;
using EveryPrinter.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace EveryPrinter;

/// <summary>
/// The preview: the current sheet drawn into a bitmap the size of the viewport. Zooming and panning move the old bitmap at once
/// and a fresh one follows a moment later, so it stays smooth even when every page has to be re-drawn.
/// </summary>
public sealed partial class MainWindow
{
    private const double Gutter = 24;
    private const double RulerSize = 18;
    private const double LoupeZoom = 3;

    private double viewScale = 1;      // DIPs per point
    private double panX, panY;         // DIP position of the sheet's top-left corner in the preview
    private bool fitMode = true;
    private double renderedScale = 1, renderedPanX, renderedPanY;
    private SizeF? previewSheetSize;
    private WriteableBitmap? previewBitmap;
    private CancellationTokenSource? renderCts;
    private CancellationTokenSource? loupeCts;
    private CancellationTokenSource? inkCts;
    private readonly Microsoft.UI.Xaml.DispatcherTimer zoomTimer = new() { Interval = TimeSpan.FromMilliseconds(55) };
    private string tool = "Select";

    // Pointer state
    private bool pressed;
    private Windows.Foundation.Point pressPoint, lastPoint;
    private bool moved;
    private Placement? dragPlacement;

    private double RasterScale => Content?.XamlRoot?.RasterizationScale ?? 1;

    private void InitPreview()
    {
        zoomTimer.Tick += (_, _) => { zoomTimer.Stop(); RequestRender(); };
    }

    // ---- View ----

    private void PreviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (fitMode) FitToHost();
        RequestRender();
    }

    private void FitToHost()
    {
        if (sheets.Count == 0 || current >= sheets.Count) return;
        var sheet = sheets[current];
        var ruler = app.ShowRulers ? RulerSize : 0;
        var w = Math.Max(50, PreviewHost.ActualWidth - ruler - Gutter * 2);
        var h = Math.Max(50, PreviewHost.ActualHeight - ruler - Gutter * 2);
        viewScale = Math.Min(w / sheet.Width, h / sheet.Height);
        panX = ruler + Gutter + (w - sheet.Width * viewScale) / 2;
        panY = ruler + Gutter + (h - sheet.Height * viewScale) / 2;
        fitMode = true;
    }

    private void FitClick(object sender, RoutedEventArgs e)
    {
        fitMode = true;
        FitToHost();
        ApplyMoveFeedback();
        RequestRender();
    }

    private void ZoomInClick(object sender, RoutedEventArgs e) => ZoomBy(1.25);
    private void ZoomOutClick(object sender, RoutedEventArgs e) => ZoomBy(0.8);

    private void ZoomBy(double factor) => ZoomAt(factor, new Windows.Foundation.Point(PreviewHost.ActualWidth / 2, PreviewHost.ActualHeight / 2));

    private void ZoomAt(double factor, Windows.Foundation.Point at)
    {
        if (sheets.Count == 0) return;
        var next = Math.Clamp(viewScale * factor, 0.04, 24);
        factor = next / viewScale;
        panX = at.X - (at.X - panX) * factor;
        panY = at.Y - (at.Y - panY) * factor;
        viewScale = next;
        fitMode = false;
        ApplyMoveFeedback();
        zoomTimer.Stop();
        zoomTimer.Start();
    }

    private void PreviewWheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(PreviewHost);
        var delta = point.Properties.MouseWheelDelta;
        var ctrl = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);
        if (ctrl) ZoomAt(Math.Pow(1.0015, delta), point.Position);
        else
        {
            if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift)) panX += delta; else panY += delta;
            fitMode = false;
            ApplyMoveFeedback();
            zoomTimer.Stop();
            zoomTimer.Start();
        }
        e.Handled = true;
    }

    /// <summary>Moves and scales the bitmap already on screen to where the new view will be, until the real one arrives.</summary>
    private void ApplyMoveFeedback()
    {
        var s = viewScale / renderedScale;
        PreviewMove.ScaleX = PreviewMove.ScaleY = s;
        PreviewMove.TranslateX = panX - renderedPanX * s;
        PreviewMove.TranslateY = panY - renderedPanY * s;
        UpdateSelectionBox();
        ZoomText.Text = $"{viewScale * 75:0}%";
    }

    private void RulerToggleClick(object sender, RoutedEventArgs e)
    {
        app.ShowRulers = RulerToggle.IsChecked == true;
        if (fitMode) FitToHost();
        RequestRender();
    }

    private void FullScreenClick(object sender, RoutedEventArgs e)
    {
        AppWindow.SetPresenter(FullScreenToggle.IsChecked == true ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
    }

    // Layered window: the whole window gets see-through, which is what "transparent mode" is for (lining a preview up over something else).
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    private void TransparentClick(object sender, RoutedEventArgs e)
    {
        const int GwlExStyle = -20;
        const long WsExLayered = 0x80000;
        var on = TransparentToggle.IsChecked == true;
        var style = GetWindowLongPtr(Hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(Hwnd, GwlExStyle, new IntPtr(on ? style | WsExLayered : style & ~WsExLayered));
        if (on) SetLayeredWindowAttributes(Hwnd, 0, 150, 2);
    }

    // ---- Rendering ----

    private sealed record RenderRequest(Sheet Sheet, PrintSettings Settings, double Scale, double PanX, double PanY, double Width, double Height,
        double Raster, bool Rulers, bool Light, bool Outline);

    internal void RequestRender()
    {
        renderCts?.Cancel();
        if (sheets.Count == 0 || current >= sheets.Count || PreviewHost.ActualWidth < 20 || PreviewHost.ActualHeight < 20)
        {
            PreviewImage.Source = null;
            UpdateSelectionBox();
            return;
        }
        if (fitMode) FitToHost();

        var request = new RenderRequest(sheets[current], layout.Settings, viewScale, panX, panY, PreviewHost.ActualWidth, PreviewHost.ActualHeight,
            RasterScale, app.ShowRulers, IsLight, true);
        var cts = renderCts = new CancellationTokenSource();
        _ = RenderAsync(request, cts.Token);
    }

    private async Task RenderAsync(RenderRequest request, CancellationToken cancel)
    {
        try
        {
            var (pixels, w, h) = await Task.Run(() =>
            {
                using var bitmap = RenderPreview(request, cancel);
                return (ToBgra(bitmap), bitmap.Width, bitmap.Height);
            }, cancel);
            if (cancel.IsCancellationRequested) return;

            previewBitmap = ToImage(pixels, w, h, previewBitmap);
            PreviewImage.Source = previewBitmap;
            PreviewImage.Width = request.Width;
            PreviewImage.Height = request.Height;
            renderedScale = request.Scale;
            renderedPanX = request.PanX;
            renderedPanY = request.PanY;
            previewSheetSize = new SizeF(request.Sheet.Width, request.Sheet.Height);
            ApplyMoveFeedback();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Preview", ex); }
    }

    private Bitmap RenderPreview(RenderRequest r, CancellationToken cancel)
    {
        var pw = Math.Max(1, (int)Math.Ceiling(r.Width * r.Raster));
        var ph = Math.Max(1, (int)Math.Ceiling(r.Height * r.Raster));
        var bitmap = new Bitmap(pw, ph, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        var canvas = r.Light ? System.Drawing.Color.FromArgb(0xE6, 0xE6, 0xE6) : System.Drawing.Color.FromArgb(0x1B, 0x1B, 0x1B);
        g.Clear(canvas);

        g.ScaleTransform((float)r.Raster, (float)r.Raster);
        var sheet = r.Sheet;

        // Paper with a soft shadow.
        var paper = new RectangleF((float)r.PanX, (float)r.PanY, (float)(sheet.Width * r.Scale), (float)(sheet.Height * r.Scale));
        for (var i = 5; i >= 1; i--)
        {
            using var shade = new SolidBrush(System.Drawing.Color.FromArgb(r.Light ? 10 : 22, 0, 0, 0));
            g.FillRectangle(shade, paper.X - i + 2, paper.Y - i + 4, paper.Width + 2 * i, paper.Height + 2 * i);
        }

        var state = g.Save();
        g.TranslateTransform((float)r.PanX, (float)r.PanY);
        g.ScaleTransform((float)r.Scale, (float)r.Scale);
        renderer.Draw(g, sheet, r.Settings, (float)(r.Scale * r.Raster), true, cancel);

        if (r.Outline)
        {
            using var pen = new Pen(System.Drawing.Color.FromArgb(46, 0, 0, 0), (float)(1 / r.Scale));
            foreach (var p in sheet.Placements)
                if (sheet.Placements.Count > 1 || p.Page.IsBlank) g.DrawRectangle(pen, p.Dest.X, p.Dest.Y, p.Dest.Width, p.Dest.Height);
        }
        g.Restore(state);

        if (r.Rulers) DrawRulers(g, r);
        return bitmap;
    }

    private static void DrawRulers(Graphics g, RenderRequest r)
    {
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var back = r.Light ? System.Drawing.Color.FromArgb(0xF5, 0xF5, 0xF5) : System.Drawing.Color.FromArgb(0x25, 0x25, 0x25);
        var ink = r.Light ? System.Drawing.Color.FromArgb(0x6E, 0x6E, 0x6E) : System.Drawing.Color.FromArgb(0x9B, 0x9B, 0x9B);
        using var backBrush = new SolidBrush(back);
        using var pen = new Pen(ink, 1);
        using var font = new Font("Segoe UI", 7.5f);
        using var textBrush = new SolidBrush(ink);

        var size = (float)RulerSize;
        g.FillRectangle(backBrush, 0, 0, (float)r.Width, size);
        g.FillRectangle(backBrush, 0, 0, size, (float)r.Height);

        var mmPerDip = 1 / (Paper.PointsPerMm * r.Scale);
        var step = new[] { 1, 2, 5, 10, 20, 50, 100, 200, 500 }.FirstOrDefault(s => s / mmPerDip >= 7, 1000);
        var major = step >= 10 ? step * 5 : step * 10;
        for (var axis = 0; axis < 2; axis++)
        {
            var origin = axis == 0 ? r.PanX : r.PanY;
            var length = axis == 0 ? r.Width : r.Height;
            var extent = (axis == 0 ? r.Sheet.Width : r.Sheet.Height) / Paper.PointsPerMm;
            for (var mm = 0; mm <= extent + step; mm += step)
            {
                var pos = (float)(origin + mm * Paper.PointsPerMm * r.Scale);
                if (pos < size || pos > length) continue;
                var isMajor = mm % major == 0;
                var tick = isMajor ? size * 0.7f : size * 0.35f;
                if (axis == 0)
                {
                    g.DrawLine(pen, pos, size - tick, pos, size);
                    if (isMajor) g.DrawString(mm.ToString(), font, textBrush, pos + 2, 1);
                }
                else
                {
                    g.DrawLine(pen, size - tick, pos, size, pos);
                    if (isMajor)
                    {
                        var st = g.Save();
                        g.TranslateTransform(1, pos + 2);
                        g.RotateTransform(90);
                        g.DrawString(mm.ToString(), font, textBrush, 0, -size + 1);
                        g.Restore(st);
                    }
                }
            }
        }
        g.DrawLine(pen, 0, size, (float)r.Width, size);
        g.DrawLine(pen, size, 0, size, (float)r.Height);
    }

    /// <summary>Shows how much ink the current sheet needs, and what the ink-saving setting saves.</summary>
    private void UpdateInk()
    {
        inkCts?.Cancel();
        if (sheets.Count == 0 || current >= sheets.Count) { InkText.Text = ""; return; }
        var cts = inkCts = new CancellationTokenSource();
        var sheet = sheets[current];
        var settings = layout.Settings;
        _ = Task.Run(() =>
        {
            try
            {
                var scale = 160f / sheet.Width;
                using var withSaving = renderer.Render(sheet, settings, scale, cts.Token);
                var used = PageAnalyzer.InkCoverage(withSaving);
                double? saved = null;
                if (settings.InkSaving > 0)
                {
                    var plain = settings.Clone();
                    plain.InkSaving = 0;
                    using var without = renderer.Render(sheet, plain, scale, cts.Token);
                    var full = PageAnalyzer.InkCoverage(without);
                    if (full > 0.0005) saved = 1 - used / full;
                }
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    InkText.Text = $"Ink on this sheet: {used * 100:0.0}%" + (saved is { } s ? $" · saving {s * 100:0}%" : "");
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error("Ink estimate", ex); }
        });
    }

    // ---- Tools ----

    private void ToolClick(object sender, RoutedEventArgs e) => PickTool((string)((FrameworkElement)sender).Tag);

    private void PickTool(string name)
    {
        tool = name;
        foreach (var (key, button) in new[] { ("Select", ToolSelect), ("Crop", ToolCrop), ("Redact", ToolRedact), ("Highlight", ToolHighlight),
                     ("Text", ToolText), ("Measure", ToolMeasure), ("Loupe", ToolLoupe) })
            button.IsChecked = key == name;
        RubberBand.Visibility = MeasureLine.Visibility = MeasureLabel.Visibility = LoupeFrame.Visibility = Visibility.Collapsed;
    }

    private Placement? PlacementAt(Windows.Foundation.Point host)
    {
        if (sheets.Count == 0 || current >= sheets.Count) return null;
        var sheet = sheets[current];
        var x = (host.X - panX) / viewScale;
        var y = (host.Y - panY) / viewScale;
        if (sheet.FlipHorizontal) x = sheet.Width - x;
        if (sheet.FlipVertical) y = sheet.Height - y;
        return sheet.Placements.LastOrDefault(p => p.Dest.Contains((float)x, (float)y));
    }

    private System.Drawing.PointF SheetPoint(Windows.Foundation.Point host)
    {
        var sheet = sheets[current];
        var x = (float)((host.X - panX) / viewScale);
        var y = (float)((host.Y - panY) / viewScale);
        return new System.Drawing.PointF(sheet.FlipHorizontal ? sheet.Width - x : x, sheet.FlipVertical ? sheet.Height - y : y);
    }

    private void PreviewPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(PreviewHost);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsMiddleButtonPressed) return;
        if (sheets.Count == 0) return;
        pressed = true;
        moved = false;
        pressPoint = lastPoint = point.Position;
        dragPlacement = point.Properties.IsMiddleButtonPressed ? null : PlacementAt(point.Position);
        PreviewHost.CapturePointer(e.Pointer);

        if (tool == "Measure" && !point.Properties.IsMiddleButtonPressed)
        {
            MeasureLine.X1 = MeasureLine.X2 = pressPoint.X;
            MeasureLine.Y1 = MeasureLine.Y2 = pressPoint.Y;
            MeasureLine.Visibility = MeasureLabel.Visibility = Visibility.Visible;
        }
    }

    private void PreviewPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(PreviewHost).Position;
        if (tool == "Loupe" && sheets.Count > 0) ShowLoupe(position);

        if (!pressed) return;
        if (!moved && Math.Abs(position.X - pressPoint.X) + Math.Abs(position.Y - pressPoint.Y) > 4) moved = true;

        var dragging = tool is "Crop" or "Redact" or "Highlight" && dragPlacement is not null;
        if (dragging)
        {
            var area = PlacementViewRect(dragPlacement!);
            var x1 = Math.Clamp(pressPoint.X, area.Left, area.Right);
            var y1 = Math.Clamp(pressPoint.Y, area.Top, area.Bottom);
            var x2 = Math.Clamp(position.X, area.Left, area.Right);
            var y2 = Math.Clamp(position.Y, area.Top, area.Bottom);
            Canvas.SetLeft(RubberBand, Math.Min(x1, x2));
            Canvas.SetTop(RubberBand, Math.Min(y1, y2));
            RubberBand.Width = Math.Abs(x2 - x1);
            RubberBand.Height = Math.Abs(y2 - y1);
            RubberBand.Visibility = Visibility.Visible;
        }
        else if (tool == "Measure")
        {
            MeasureLine.X2 = position.X;
            MeasureLine.Y2 = position.Y;
            var dx = (position.X - pressPoint.X) / viewScale / Paper.PointsPerMm;
            var dy = (position.Y - pressPoint.Y) / viewScale / Paper.PointsPerMm;
            MeasureText.Text = $"{Math.Sqrt(dx * dx + dy * dy):0.0} mm   ({Math.Abs(dx):0.0} × {Math.Abs(dy):0.0})";
            Canvas.SetLeft(MeasureLabel, position.X + 10);
            Canvas.SetTop(MeasureLabel, position.Y + 10);
        }
        else if (moved)
        {
            // Everything else drags the view.
            panX += position.X - lastPoint.X;
            panY += position.Y - lastPoint.Y;
            fitMode = false;
            ApplyMoveFeedback();
        }
        lastPoint = position;
    }

    private void PreviewPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!pressed) return;
        pressed = false;
        PreviewHost.ReleasePointerCapture(e.Pointer);
        var position = e.GetCurrentPoint(PreviewHost).Position;
        RubberBand.Visibility = Visibility.Collapsed;

        if (tool is "Crop" or "Redact" or "Highlight" && dragPlacement is not null && moved) FinishRegion(dragPlacement, position);
        else if (tool == "Text" && !moved && dragPlacement is not null) _ = AddTextAsync(dragPlacement, position);
        else if (tool == "Select" && !moved) SelectAt(position);
        else if (moved && tool is not ("Measure" or "Crop" or "Redact" or "Highlight")) RequestRender();
        dragPlacement = null;
    }

    private void PreviewPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!pressed) return;
        pressed = false;
        RubberBand.Visibility = Visibility.Collapsed;
        dragPlacement = null;
        RequestRender();
    }

    private void PreviewPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (tool == "Loupe") { loupeCts?.Cancel(); LoupeFrame.Visibility = Visibility.Collapsed; }
    }

    private Windows.Foundation.Rect PlacementViewRect(Placement p)
    {
        var sheet = sheets[current];
        float x = p.Dest.X, y = p.Dest.Y, w = p.Dest.Width, h = p.Dest.Height;
        if (sheet.FlipHorizontal) x = sheet.Width - x - w;
        if (sheet.FlipVertical) y = sheet.Height - y - h;
        return new Windows.Foundation.Rect(panX + x * viewScale, panY + y * viewScale, w * viewScale, h * viewScale);
    }

    private void SelectAt(Windows.Foundation.Point position)
    {
        selected = PlacementAt(position)?.Page;
        UpdateSelectionBox();
    }

    private void UpdateSelectionBox()
    {
        var placement = selected is null || sheets.Count == 0 || current >= sheets.Count ? null : sheets[current].Placements.FirstOrDefault(p => p.Page == selected);
        if (placement is null) { SelectionBox.Visibility = Visibility.Collapsed; return; }
        var r = PlacementViewRect(placement);
        Canvas.SetLeft(SelectionBox, r.X);
        Canvas.SetTop(SelectionBox, r.Y);
        SelectionBox.Width = Math.Max(1, r.Width);
        SelectionBox.Height = Math.Max(1, r.Height);
        SelectionBox.Visibility = Visibility.Visible;
    }

    /// <summary>Turns a drag on a page into a crop, a redaction or a highlight in the page's own coordinates.</summary>
    private void FinishRegion(Placement placement, Windows.Foundation.Point end)
    {
        var area = PlacementViewRect(placement);
        double x1 = Math.Clamp(pressPoint.X, area.Left, area.Right), y1 = Math.Clamp(pressPoint.Y, area.Top, area.Bottom);
        double x2 = Math.Clamp(end.X, area.Left, area.Right), y2 = Math.Clamp(end.Y, area.Top, area.Bottom);
        if (Math.Abs(x2 - x1) < 4 || Math.Abs(y2 - y1) < 4) return;

        var a = placement.Unmap(SheetPoint(new Windows.Foundation.Point(x1, y1)).X, SheetPoint(new Windows.Foundation.Point(x1, y1)).Y);
        var b = placement.Unmap(SheetPoint(new Windows.Foundation.Point(x2, y2)).X, SheetPoint(new Windows.Foundation.Point(x2, y2)).Y);
        var rect = RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        rect = RectangleF.Intersect(rect, new RectangleF(0, 0, 1, 1));
        var index = doc.Pages.IndexOf(placement.Page);
        if (index < 0 || rect.Width <= 0 || rect.Height <= 0) return;

        switch (tool)
        {
            case "Crop":
                doc.SetCrop(index, rect);
                PickTool("Select");
                break;
            case "Redact":
                doc.AddAnnotation(index, new Annotation { Kind = AnnotationKind.Redact, Rect = rect });
                break;
            case "Highlight":
                doc.AddAnnotation(index, new Annotation { Kind = AnnotationKind.Highlight, Rect = rect, Color = "#FFEB3B" });
                break;
        }
        DoRelayout();
    }

    private async Task AddTextAsync(Placement placement, Windows.Foundation.Point at)
    {
        var box = new TextBox { AcceptsReturn = false, MinWidth = 320, PlaceholderText = "Text to add to the page" };
        var result = await ShowDialogAsync(new ContentDialog
        {
            Title = "Add text",
            Content = box,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (result != ContentDialogResult.Primary || box.Text.Trim().Length == 0) return;

        var p = SheetPoint(at);
        var origin = placement.Unmap(p.X, p.Y);
        var index = doc.Pages.IndexOf(placement.Page);
        if (index < 0) return;
        var rect = new RectangleF(Math.Clamp(origin.X, 0, 0.95f), Math.Clamp(origin.Y, 0, 0.97f), 0.6f, 0.04f);
        doc.AddAnnotation(index, new Annotation { Kind = AnnotationKind.Text, Rect = rect, Text = box.Text.Trim(), Color = "#D7373F", FontSize = 0.02f });
        DoRelayout();
    }

    private void ShowLoupe(Windows.Foundation.Point position)
    {
        loupeCts?.Cancel();
        var cts = loupeCts = new CancellationTokenSource();
        var sheet = sheets[current];
        var settings = layout.Settings;
        double raster = RasterScale, scale = viewScale, px = panX, py = panY;
        var size = 190.0;

        Canvas.SetLeft(LoupeFrame, Math.Clamp(position.X + 18, 0, Math.Max(0, PreviewHost.ActualWidth - size)));
        Canvas.SetTop(LoupeFrame, Math.Clamp(position.Y - size - 18 < 0 ? position.Y + 18 : position.Y - size - 18, 0, Math.Max(0, PreviewHost.ActualHeight - size)));
        LoupeFrame.Visibility = Visibility.Visible;

        _ = Task.Run(() =>
        {
            try
            {
                var side = (int)Math.Ceiling((size - 6) * raster);
                using var bitmap = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.Clear(System.Drawing.Color.White);
                    g.ScaleTransform((float)raster, (float)raster);
                    g.TranslateTransform((float)((size - 6) / 2), (float)((size - 6) / 2));
                    g.ScaleTransform((float)LoupeZoom, (float)LoupeZoom);
                    g.TranslateTransform((float)(px - position.X), (float)(py - position.Y));
                    g.ScaleTransform((float)scale, (float)scale);
                    renderer.Draw(g, sheet, settings, (float)(scale * LoupeZoom * raster), true, cts.Token);
                }
                var pixels = ToBgra(bitmap);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    LoupeImage.Source = ToImage(pixels, bitmap.Width, bitmap.Height);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error("Loupe", ex); }
        });
    }
}
