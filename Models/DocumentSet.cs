using System.Drawing;

namespace EveryPrinter.Models;

public enum JobKind { Pdf, Image, Text, Blank }

/// <summary>A file (or pasted picture) that was sent to Every Printer. Its pages are listed in <see cref="DocumentSet.Pages"/>.</summary>
public sealed class SourceJob
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public JobKind Kind { get; init; }
    public DateTime Added { get; init; } = DateTime.Now;
    public int PageCount { get; set; }

    /// <summary>False when the PDF can't be copied into the output as vector pages (it is encrypted, for example), so it is drawn as images.</summary>
    public bool VectorOk { get; set; } = true;

    /// <summary>For text jobs: the lines of the file, already wrapped into pages by the page source.</summary>
    public string[]? TextPages { get; set; }
}

public enum AnnotationKind { Redact, Highlight, Text, Picture }

/// <summary>A mark on one page, positioned as fractions (0..1) of the unrotated page.</summary>
public sealed class Annotation
{
    public AnnotationKind Kind { get; set; }
    public RectangleF Rect { get; set; }
    public string Text { get; set; } = "";
    public string Color { get; set; } = "#FFEB3B";

    /// <summary>Text size as a fraction of the page height.</summary>
    public float FontSize { get; set; } = 0.02f;
}

/// <summary>One page of a job plus the edits made to it. Edits never touch the source file.</summary>
public sealed class PageRef
{
    public required SourceJob Job { get; init; }
    public int Index { get; init; }

    /// <summary>Natural size in points.</summary>
    public float Width { get; init; }
    public float Height { get; init; }

    /// <summary>Extra clockwise rotation in degrees (0, 90, 180 or 270).</summary>
    public int Rotation { get; set; }

    /// <summary>Visible part of the page as fractions of the unrotated page.</summary>
    public RectangleF Crop { get; set; } = new(0, 0, 1, 1);

    public bool Deleted { get; set; }
    public List<Annotation> Annotations { get; set; } = [];

    public bool IsBlank => Job.Kind == JobKind.Blank;
    public bool IsCropped => Crop.X > 0.0005f || Crop.Y > 0.0005f || Crop.Width < 0.999f || Crop.Height < 0.999f;

    public PageRef CloneEdits() => new()
    {
        Job = Job, Index = Index, Width = Width, Height = Height, Rotation = Rotation, Crop = Crop, Deleted = Deleted,
        Annotations = Annotations.Select(a => new Annotation { Kind = a.Kind, Rect = a.Rect, Text = a.Text, Color = a.Color, FontSize = a.FontSize }).ToList(),
    };

    /// <summary>Size in points of what is visible (cropped and rotated), for a given total rotation.</summary>
    public SizeF VisibleSize(int rotation, RectangleF crop)
    {
        var w = Width * crop.Width;
        var h = Height * crop.Height;
        return rotation % 180 == 0 ? new SizeF(w, h) : new SizeF(h, w);
    }
}

/// <summary>
/// The pages waiting to be printed, from every job. Editing operations keep an undo history; pages are copied on each step,
/// which stays cheap even for very large jobs because a page is a handful of numbers.
/// </summary>
public sealed class DocumentSet
{
    private const int MaxHistory = 60;

    private readonly Stack<PageRef[]> undo = new();
    private readonly Stack<PageRef[]> redo = new();
    private int nextJobId = 1;

    public List<SourceJob> Jobs { get; } = [];
    public List<PageRef> Pages { get; private set; } = [];

    /// <summary>Changes on every edit, so callers can tell whether to refresh.</summary>
    public int Version { get; private set; }

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public int LivePageCount => Pages.Count(p => !p.Deleted);

    public SourceJob NewJob(string name, string path, JobKind kind) => new() { Id = nextJobId++, Name = name, Path = path, Kind = kind };

    public void AddPages(IEnumerable<PageRef> pages, int? insertAt = null)
    {
        Checkpoint();
        var list = pages.ToList();
        foreach (var job in list.Select(p => p.Job).Distinct().Where(j => !Jobs.Contains(j))) Jobs.Add(job);
        if (insertAt is { } at && at >= 0 && at <= Pages.Count) Pages.InsertRange(at, list);
        else Pages.AddRange(list);
        Changed();
    }

    public PageRef InsertBlank(int at, float width, float height, string name = "Blank page")
    {
        var job = NewJob(name, "", JobKind.Blank);
        job.PageCount = 1;
        var page = new PageRef { Job = job, Index = 0, Width = width, Height = height };
        AddPages([page], at);
        return page;
    }

    /// <summary>Saves an undo step before a drag that will change a page in place with <see cref="Touch"/>.</summary>
    public void BeginEdit() => Checkpoint();

    /// <summary>Marks the pages as changed after an in-place edit.</summary>
    public void Touch() => Changed();

    public void Duplicate(IReadOnlyCollection<int> indexes)
    {
        if (indexes.Count == 0) return;
        Checkpoint();
        foreach (var i in indexes.OrderByDescending(i => i))
            if (i >= 0 && i < Pages.Count) Pages.Insert(i + 1, Pages[i].CloneEdits());
        Changed();
    }

    /// <summary>Marks pages deleted (they stay visible in the page list, greyed out, so the delete can be undone or repeated).</summary>
    public void SetDeleted(IEnumerable<int> indexes, bool deleted)
    {
        Checkpoint();
        foreach (var i in indexes) if (i >= 0 && i < Pages.Count) Pages[i].Deleted = deleted;
        Changed();
    }

    public void Remove(IEnumerable<int> indexes)
    {
        Checkpoint();
        var set = indexes.ToHashSet();
        Pages = Pages.Where((_, i) => !set.Contains(i)).ToList();
        PruneJobs();
        Changed();
    }

    public void RemoveJob(SourceJob job)
    {
        Checkpoint();
        Pages = Pages.Where(p => p.Job != job).ToList();
        PruneJobs();
        Changed();
    }

    public void Rotate(IEnumerable<int> indexes, int degrees)
    {
        Checkpoint();
        foreach (var i in indexes) if (i >= 0 && i < Pages.Count) Pages[i].Rotation = ((Pages[i].Rotation + degrees) % 360 + 360) % 360;
        Changed();
    }

    public void SetCrop(int index, RectangleF crop)
    {
        if (index < 0 || index >= Pages.Count) return;
        Checkpoint();
        Pages[index].Crop = crop;
        Changed();
    }

    public void ResetEdits(IEnumerable<int> indexes)
    {
        Checkpoint();
        foreach (var i in indexes.Where(i => i >= 0 && i < Pages.Count))
        {
            var p = Pages[i];
            p.Rotation = 0; p.Crop = new RectangleF(0, 0, 1, 1); p.Deleted = false; p.Annotations.Clear();
        }
        Changed();
    }

    public void AddAnnotation(int index, Annotation annotation)
    {
        if (index < 0 || index >= Pages.Count) return;
        Checkpoint();
        Pages[index].Annotations.Add(annotation);
        Changed();
    }

    public void ClearAnnotations(IEnumerable<int> indexes)
    {
        Checkpoint();
        foreach (var i in indexes.Where(i => i >= 0 && i < Pages.Count)) Pages[i].Annotations.Clear();
        Changed();
    }

    /// <summary>Moves the pages at <paramref name="indexes"/> so they start at <paramref name="target"/> (an index into the list before the move).</summary>
    public void Move(IReadOnlyCollection<int> indexes, int target)
    {
        var sorted = indexes.Where(i => i >= 0 && i < Pages.Count).Distinct().OrderBy(i => i).ToList();
        if (sorted.Count == 0) return;
        Checkpoint();
        var moving = sorted.Select(i => Pages[i]).ToList();
        var before = sorted.Count(i => i < target);
        var set = sorted.ToHashSet();
        var rest = Pages.Where((_, i) => !set.Contains(i)).ToList();
        rest.InsertRange(Math.Clamp(target - before, 0, rest.Count), moving);
        Pages = rest;
        Changed();
    }

    public void Clear()
    {
        Checkpoint();
        Pages = [];
        Jobs.Clear();
        Changed();
    }

    public void Undo()
    {
        if (undo.Count == 0) return;
        redo.Push(Snapshot());
        Pages = undo.Pop().ToList();
        PruneJobs();
        Version++;
    }

    public void Redo()
    {
        if (redo.Count == 0) return;
        undo.Push(Snapshot());
        Pages = redo.Pop().ToList();
        Version++;
    }

    private PageRef[] Snapshot() => Pages.Select(p => p.CloneEdits()).ToArray();

    private void Checkpoint()
    {
        undo.Push(Snapshot());
        redo.Clear();
        if (undo.Count > MaxHistory)
        {
            var keep = undo.Take(MaxHistory).Reverse().ToArray();
            undo.Clear();
            foreach (var s in keep) undo.Push(s);
        }
    }

    private void PruneJobs()
    {
        var used = Pages.Select(p => p.Job).ToHashSet();
        Jobs.RemoveAll(j => !used.Contains(j));
        foreach (var page in Pages) if (!Jobs.Contains(page.Job)) Jobs.Add(page.Job);
    }

    private void Changed() => Version++;
}
