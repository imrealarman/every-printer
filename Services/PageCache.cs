using System.Drawing;

namespace EveryPrinter.Services;

/// <summary>
/// A page picture on loan from the cache. The cache never frees a bitmap while it is leased, so drawing on a worker thread
/// can't race with eviction triggered by the preview thread.
/// </summary>
public sealed class BitmapLease : IDisposable
{
    private readonly PageCache cache;
    private readonly PageCache.Entry entry;
    private bool disposed;

    internal BitmapLease(PageCache cache, PageCache.Entry entry, RectangleF source)
    {
        this.cache = cache;
        this.entry = entry;
        Source = source;
    }

    public Bitmap Bitmap => entry.Bitmap;

    /// <summary>The part of <see cref="Bitmap"/> to draw, in pixels.</summary>
    public RectangleF Source { get; }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cache.Release(entry);
    }
}

/// <summary>Keeps recently rendered page pictures so scrolling, zooming and re-layout don't render the same page again.</summary>
public sealed class PageCache(long budgetBytes)
{
    internal sealed class Entry(string key, Bitmap bitmap)
    {
        public string Key { get; } = key;
        public Bitmap Bitmap { get; } = bitmap;
        public long Bytes { get; } = (long)bitmap.Width * bitmap.Height * 4;
        public int Leases;
        public bool Evicted;
    }

    private readonly object gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> map = [];
    private readonly LinkedList<Entry> order = new(); // most recent first
    private long used;

    public BitmapLease Lease(string key, Func<Bitmap> create, Func<Bitmap, RectangleF> source)
    {
        lock (gate)
        {
            if (map.TryGetValue(key, out var hit))
            {
                order.Remove(hit);
                order.AddFirst(hit);
                hit.Value.Leases++;
                return new BitmapLease(this, hit.Value, source(hit.Value.Bitmap));
            }
        }

        var bitmap = create(); // rendering happens outside the lock so other pages can be served meanwhile
        lock (gate)
        {
            if (map.TryGetValue(key, out var raced))
            {
                bitmap.Dispose();
                order.Remove(raced);
                order.AddFirst(raced);
                raced.Value.Leases++;
                return new BitmapLease(this, raced.Value, source(raced.Value.Bitmap));
            }
            var entry = new Entry(key, bitmap) { Leases = 1 };
            map[key] = order.AddFirst(entry);
            used += entry.Bytes;
            Trim();
            return new BitmapLease(this, entry, source(bitmap));
        }
    }

    internal void Release(Entry entry)
    {
        lock (gate)
        {
            entry.Leases--;
            if (entry.Evicted && entry.Leases <= 0) entry.Bitmap.Dispose();
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            foreach (var entry in order.ToList()) Evict(entry);
        }
    }

    /// <summary>Drops every cached picture whose key starts with the prefix (used when a job is removed).</summary>
    public void Remove(string prefix)
    {
        lock (gate)
        {
            foreach (var entry in order.Where(e => e.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList()) Evict(entry);
        }
    }

    private void Trim()
    {
        while (used > budgetBytes && order.Count > 1)
            Evict(order.Last!.Value);
    }

    private void Evict(Entry entry)
    {
        if (!map.Remove(entry.Key, out var node)) return;
        order.Remove(node);
        used -= entry.Bytes;
        entry.Evicted = true;
        if (entry.Leases <= 0) entry.Bitmap.Dispose();
    }
}
