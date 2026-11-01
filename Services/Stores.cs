using System.Text.Json;
using System.Text.Json.Serialization;
using EveryPrinter.Models;

namespace EveryPrinter.Services;

public enum AppTheme { System, Light, Dark }

/// <summary>Preferences that belong to this PC and this person rather than to a theme. Saved as JSON under %AppData%\EveryPrinter.</summary>
public sealed class AppSettings
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EveryPrinter");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public string ThemeName { get; set; } = "Default";
    public string SaveFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Every Printer");
    public bool WatchInbox { get; set; } = true;
    public bool KeepHistory { get; set; } = true;
    public double Zoom { get; set; }
    public bool ShowRulers { get; set; } = true;

    public string InboxFolder => Path.Combine(SaveFolder, "Inbox");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (IOException) { }
    }
}

/// <summary>Named print settings ("themes") saved as JSON files, which can be copied between computers.</summary>
public static class ThemeStore
{
    public static string Folder => Path.Combine(AppSettings.Folder, "themes");

    public const string DefaultName = "Default";

    public static IReadOnlyList<string> Names()
    {
        var names = new List<string> { DefaultName };
        try
        {
            if (Directory.Exists(Folder))
                names.AddRange(Directory.EnumerateFiles(Folder, "*.json").Select(Path.GetFileNameWithoutExtension).Where(n => n is not null && n != DefaultName)!.OrderBy(n => n)!);
        }
        catch (IOException) { }
        return names;
    }

    public static PrintSettings Load(string name)
    {
        if (name == DefaultName) return DefaultTheme();
        try { return PrintSettings.FromJson(File.ReadAllText(PathOf(name))); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return DefaultTheme(); }
    }

    /// <summary>Printer, copies and PDF passwords are about this job, so a theme leaves them out.</summary>
    public static void Save(string name, PrintSettings settings)
    {
        Directory.CreateDirectory(Folder);
        var copy = settings.Clone();
        copy.PdfUserPassword = "";
        copy.PdfOwnerPassword = "";
        copy.PageRange = "";
        File.WriteAllText(PathOf(name), copy.ToJson());
    }

    public static void Delete(string name)
    {
        try { File.Delete(PathOf(name)); } catch (IOException) { }
    }

    public static string Sanitize(string name)
    {
        var cleaned = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim('.', ' ');
        return cleaned.Length == 0 ? "Theme" : cleaned;
    }

    public static PrintSettings DefaultTheme() => new();

    /// <summary>A few ready-made themes to start from; they are written once, so edits to them stick.</summary>
    public static void EnsureSamples()
    {
        if (Directory.Exists(Folder) && Directory.EnumerateFiles(Folder, "*.json").Any()) return;

        Save("2-up saver", new PrintSettings { Mode = LayoutMode.NUp, Rows = 1, Columns = 2, Orientation = Orientation.Landscape, InkSaving = 30, Grayscale = true });
        Save("Booklet A5", new PrintSettings { Mode = LayoutMode.Booklet, Duplex = DuplexMode.ShortEdge, Paper = "A4", GutterXMm = 8 });
        Save("Draft, low ink", new PrintSettings { Grayscale = true, InkSaving = 50 });
        var confidential = new PrintSettings();
        confidential.Watermarks.Add(new Watermark());
        confidential.Watermarks.Add(Watermark.PageNumber());
        Save("Confidential", confidential);
        Save("Poster 2 x 2", new PrintSettings { Mode = LayoutMode.Poster, PosterColumns = 2, PosterRows = 2 });
    }

    private static string PathOf(string name) => Path.Combine(Folder, Sanitize(name) + ".json");
}

public sealed class HistoryEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public int Pages { get; set; }
    public long Bytes { get; set; }
    public DateTime Date { get; set; } = DateTime.Now;
    public string Action { get; set; } = "";
}

/// <summary>A log of what was printed or saved, newest first, that can be searched by name.</summary>
public static class HistoryStore
{
    private const int Keep = 500;
    private static readonly string FilePath = Path.Combine(AppSettings.Folder, "history.json");
    private static readonly object Gate = new();

    public static List<HistoryEntry> Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath)) return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath)) ?? [];
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
            return [];
        }
    }

    public static void Add(HistoryEntry entry)
    {
        lock (Gate)
        {
            var all = Load();
            all.Insert(0, entry);
            if (all.Count > Keep) all.RemoveRange(Keep, all.Count - Keep);
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(all));
            }
            catch (IOException) { }
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            try { File.Delete(FilePath); } catch (IOException) { }
        }
    }
}
