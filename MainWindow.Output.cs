using System.Diagnostics;
using EveryPrinter.Models;
using EveryPrinter.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EveryPrinter;

/// <summary>Printing, saving as PDF or pictures, the print history, and the "Every Printer" virtual printer that feeds the inbox.</summary>
public sealed partial class MainWindow
{
    private const string VirtualPrinterName = "Every Printer";

    private CancellationTokenSource? operationCts;
    private FileSystemWatcher? inbox;
    private readonly HashSet<string> inboxBusy = [];

    /// <summary>Sheets for printing and saving: the same layout as the preview, without the find highlights.</summary>
    private List<Sheet> OutputSheets(PrintSettings settings)
    {
        var hooks = new LayoutHooks
        {
            IsBlank = settings.SkipBlankPages ? analyzer.CachedBlank : null,
            ContentBounds = settings.Mode == LayoutMode.Join && settings.JoinSmart ? analyzer.CachedBounds : null,
        };
        return LayoutEngine.Build(doc.Pages, settings, hooks);
    }

    private string JobName => doc.Jobs.Count == 1 ? Path.GetFileNameWithoutExtension(doc.Jobs[0].Name) : "Every Printer";

    private void BusyCancelClick(object sender, RoutedEventArgs e) => operationCts?.Cancel();

    private void BeginOperation(string text)
    {
        operationCts = new CancellationTokenSource();
        SetBusy(true, text);
    }

    private void EndOperation()
    {
        SetBusy(false);
        ReportProgress(0);
        operationCts?.Dispose();
        operationCts = null;
    }

    // ---- Print ----

    private async void PrintClick(object sender, object e)
    {
        if (busy || sheets.Count == 0) return;
        var settings = print.Clone();
        if (string.IsNullOrWhiteSpace(settings.Printer)) { ShowToast("No printer is chosen. Pick one on the Output tab."); return; }
        var output = OutputSheets(settings);
        if (output.Count == 0) { ShowToast("There is nothing to print with these settings."); return; }

        var pages = output.SelectMany(s => s.Placements).Select(p => p.Number).Distinct().Count();
        var sides = settings.Duplex == DuplexMode.OneSided ? "one side" : settings.Duplex.ToString().Replace("Manual", "by hand, ").Replace("Edge", " edge");
        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"{pages} {(pages == 1 ? "page" : "pages")} on {output.Count} {(output.Count == 1 ? "sheet" : "sheets")}, {settings.Copies} {(settings.Copies == 1 ? "copy" : "copies")}\n"
                   + $"{settings.Printer}\n{sides}",
        };
        var answer = await ShowDialogAsync(new ContentDialog
        {
            Title = "Print?",
            Content = summary,
            PrimaryButtonText = "Print",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (answer != ContentDialogResult.Primary) return;

        BeginOperation($"Printing {output.Count} sheets…");
        try
        {
            await PrintService.PrintAsync(output, settings, renderer, JobName, AskFlipAsync, new Progress<double>(ReportProgress), operationCts!.Token);
            AddHistory("Printed", settings.Printer, null, pages);
            ShowToast("Sent to the printer.");
        }
        catch (OperationCanceledException) { ShowToast("Printing was cancelled."); }
        catch (Exception ex)
        {
            Log.Error("Print", ex);
            ShowToast($"Could not print: {ex.Message}");
        }
        finally { EndOperation(); }
    }

    private async Task<bool> AskFlipAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        DispatcherQueue.TryEnqueue(async () =>
        {
            var result = await ShowDialogAsync(new ContentDialog
            {
                Title = "Turn the stack over",
                Content = "The front sides are printed. Take the pages from the printer and put them back in the tray so the blank sides are printed on, in the same order they came out. Then print the backs.",
                PrimaryButtonText = "Print the backs",
                CloseButtonText = "Stop",
                DefaultButton = ContentDialogButton.Primary,
            });
            tcs.SetResult(result == ContentDialogResult.Primary);
        });
        return await tcs.Task;
    }

    // ---- Save as PDF / pictures ----

    private async void PdfClick(object sender, object e)
    {
        if (busy || sheets.Count == 0) return;
        var picker = new FileSavePicker { SuggestedFileName = JobName, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeChoices.Add("PDF document", [".pdf"]);
        if (await picker.PickSaveFileAsync() is not { } file) return;

        var settings = print.Clone();
        var output = OutputSheets(settings);
        var path = file.Path;
        BeginOperation($"Saving {output.Count} sheets…");
        try
        {
            var progress = new Progress<double>(ReportProgress);
            var token = operationCts!.Token;
            var result = await Task.Run(() => PdfExporter.Save(output, settings, renderer, path, JobName, progress, token), token);
            var pages = output.SelectMany(s => s.Placements).Select(p => p.Number).Distinct().Count();
            AddHistory("Saved PDF", path, new FileInfo(path).Length, pages);
            var detail = result.PictureSheets == 0 ? "text kept sharp" : result.VectorSheets == 0 ? "saved as pictures" : $"{result.VectorSheets} sharp, {result.PictureSheets} as pictures";
            ShowToast($"Saved {result.Sheets} {(result.Sheets == 1 ? "sheet" : "sheets")} ({detail}, {FormatSize(new FileInfo(path).Length)}).", "Show in folder", () => ShowInFolder(path));
        }
        catch (OperationCanceledException)
        {
            TryDelete(path);
            ShowToast("Saving was cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error("Save PDF", ex);
            TryDelete(path);
            ShowToast($"Could not save the PDF: {ex.Message}");
        }
        finally { EndOperation(); }
    }

    private async void ImagesClick(object sender, object e)
    {
        if (busy || sheets.Count == 0) return;
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeFilter.Add("*");
        if (await picker.PickSingleFolderAsync() is not { } folder) return;

        var settings = print.Clone();
        var output = OutputSheets(settings);
        BeginOperation($"Saving {output.Count} pictures…");
        try
        {
            var progress = new Progress<double>(ReportProgress);
            var token = operationCts!.Token;
            var files = await Task.Run(() => ImageExporter.Export(output, settings, renderer, folder.Path, JobName, progress, token), token);
            AddHistory("Saved pictures", folder.Path, files.Sum(f => new FileInfo(f).Length), output.Count);
            ShowToast($"Saved {files.Count} {(files.Count == 1 ? "picture" : "pictures")}.", "Show in folder", () => ShowInFolder(files[0]));
        }
        catch (OperationCanceledException) { ShowToast("Saving was cancelled."); }
        catch (Exception ex)
        {
            Log.Error("Save pictures", ex);
            ShowToast($"Could not save the pictures: {ex.Message}");
        }
        finally { EndOperation(); }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024:0.#} MB",
    };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void ShowInFolder(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else OpenFolder(path);
        }
        catch (Exception ex) { Log.Error("Show in folder", ex); }
    }

    internal static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start("explorer.exe", $"\"{folder}\"");
        }
        catch (Exception ex) { Log.Error("Open folder", ex); }
    }

    // ---- History ----

    private void AddHistory(string action, string target, long? bytes, int pages)
    {
        if (!app.KeepHistory) return;
        HistoryStore.Add(new HistoryEntry { Name = JobName, Path = target, Action = action, Pages = pages, Bytes = bytes ?? 0, Date = DateTime.Now });
    }

    private async void HistoryClick(object sender, RoutedEventArgs e)
    {
        var all = HistoryStore.Load();
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 360, MinWidth = 520 };
        var search = new TextBox { PlaceholderText = "Search by name", Margin = new Thickness(0, 0, 0, 10) };
        var empty = Note(all.Count == 0 ? "Nothing has been printed or saved yet." : "");
        List<HistoryEntry> shown = all;

        void Fill()
        {
            shown = all.Where(h => search.Text.Length == 0 || h.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || h.Path.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Items.Clear();
            foreach (var h in shown)
            {
                var row = new StackPanel { Spacing = 2, Padding = new Thickness(0, 6, 0, 6) };
                row.Children.Add(new TextBlock { Text = $"{h.Name} · {h.Action}" });
                row.Children.Add(new TextBlock
                {
                    Text = $"{h.Date:g} · {h.Pages} {(h.Pages == 1 ? "page" : "pages")}" + (h.Bytes > 0 ? $" · {FormatSize(h.Bytes)}" : "") + $" · {h.Path}",
                    Style = (Style)Application.Current.Resources["MutedText"], TextTrimming = TextTrimming.CharacterEllipsis,
                });
                list.Items.Add(row);
            }
        }
        search.TextChanged += (_, _) => Fill();
        Fill();

        var panel = new StackPanel { Children = { search, list, empty } };
        var result = await ShowDialogAsync(new ContentDialog
        {
            Title = "Print history",
            Content = panel,
            PrimaryButtonText = "Show in folder",
            SecondaryButtonText = "Clear history",
            CloseButtonText = "Close",
        });
        if (result == ContentDialogResult.Primary && list.SelectedIndex >= 0) ShowInFolder(shown[list.SelectedIndex].Path);
        else if (result == ContentDialogResult.Secondary) { HistoryStore.Clear(); ShowToast("History cleared."); }
    }

    // ---- Virtual printer and inbox ----

    private void StartInboxWatcher()
    {
        inbox?.Dispose();
        inbox = null;
        if (!app.WatchInbox) return;
        try
        {
            Directory.CreateDirectory(app.InboxFolder);
            inbox = new FileSystemWatcher(app.InboxFolder, "*.pdf")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            inbox.Created += (_, e) => ReceiveFromInbox(e.FullPath);
            inbox.Changed += (_, e) => ReceiveFromInbox(e.FullPath);
            inbox.Renamed += (_, e) => ReceiveFromInbox(e.FullPath);
            // Jobs printed while the app was closed are waiting in the folder.
            foreach (var waiting in Directory.EnumerateFiles(app.InboxFolder, "*.pdf")) ReceiveFromInbox(waiting);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Error("Inbox watcher", ex);
        }
    }

    /// <summary>A PDF landed in the inbox: wait until the printer driver has finished writing it, move it aside, and open it.</summary>
    private void ReceiveFromInbox(string path)
    {
        lock (inboxBusy) if (!inboxBusy.Add(path)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                string? moved = null;
                long lastSize = -1;
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    await Task.Delay(400);
                    if (!File.Exists(path)) return;
                    var size = new FileInfo(path).Length;
                    if (size == 0 || size != lastSize) { lastSize = size; continue; }
                    try
                    {
                        var received = Path.Combine(app.InboxFolder, "Received");
                        Directory.CreateDirectory(received);
                        var target = Path.Combine(received, $"Print {DateTime.Now:yyyy-MM-dd HH-mm-ss}.pdf");
                        for (var n = 2; File.Exists(target); n++) target = Path.Combine(received, $"Print {DateTime.Now:yyyy-MM-dd HH-mm-ss} ({n}).pdf");
                        File.Move(path, target);
                        moved = target;
                        break;
                    }
                    catch (IOException) { lastSize = -1; } // still being written
                }
                if (moved is null) return;
                DispatcherQueue.TryEnqueue(async () =>
                {
                    Activate();
                    await OpenFilesAsync([moved]);
                });
            }
            catch (Exception ex) { Log.Error("Receive print job", ex); }
            finally { lock (inboxBusy) inboxBusy.Remove(path); }
        });
    }

    /// <summary>Files from a later launch (right-click Print, a print job): bring the window forward and open them.</summary>
    public async Task ReceiveAsync(IReadOnlyList<string> paths)
    {
        Activate();
        if (paths.Count > 0) await OpenFilesAsync(paths);
    }

    private async Task InstallVirtualPrinterAsync(bool install)
    {
        var result = await ShowDialogAsync(new ContentDialog
        {
            Title = install ? "Install the Every Printer printer?" : "Remove the Every Printer printer?",
            Content = install
                ? "Windows will ask for administrator permission once. It adds a printer called “Every Printer” that saves what any program prints into the inbox folder. Every Printer starts and opens it whenever you print to it.\n\nIt uses Windows' own “Microsoft Print to PDF” driver, which must be turned on in Windows Features."
                : "Windows will ask for administrator permission. The printer and its port are removed; your files are not touched.",
            PrimaryButtonText = install ? "Install" : "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (result != ContentDialogResult.Primary) return;

        var script = Path.Combine(AppContext.BaseDirectory, "Assets", "printer-setup.ps1");
        if (!File.Exists(script)) { ShowToast("The printer setup script is missing. Reinstall Every Printer."); return; }
        var exe = Environment.ProcessPath ?? "";
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Action {(install ? "Install" : "Remove")} -Inbox \"{app.InboxFolder}\" -Exe \"{exe}\" -User \"{user}\"";

        SetBusy(true, install ? "Installing the printer…" : "Removing the printer…");
        try
        {
            var exit = await Task.Run(() =>
            {
                var start = new ProcessStartInfo("powershell.exe", arguments)
                {
                    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                };
                using var process = Process.Start(start)!;
                process.WaitForExit();
                return process.ExitCode;
            });
            if (exit == 0)
            {
                RefreshPrinters();
                ShowToast(install ? "The “Every Printer” printer is ready. Print to it from any program." : "The printer was removed.");
            }
            else ShowToast("Windows could not complete that. Make sure “Microsoft Print to PDF” is turned on in Windows Features.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowToast("The change was cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error("Virtual printer", ex);
            ShowToast("Could not change the printer.");
        }
        finally
        {
            SetBusy(false);
        }
    }
}
