using System.Drawing;
using EveryPrinter.Models;
using EveryPrinter.Services;
using Xunit;

namespace EveryPrinter.Tests;

public class LayoutEngineTests
{
    private static List<PageRef> Pages(int count, float w = 595, float h = 842, string name = "doc.pdf")
    {
        var job = new SourceJob { Id = 1, Name = name, Kind = JobKind.Pdf, PageCount = count };
        return Enumerable.Range(0, count).Select(i => new PageRef { Job = job, Index = i, Width = w, Height = h }).ToList();
    }

    private static int Idx(Placement p) => p.Page.Index + 1;

    [Fact]
    public void Normal_puts_each_page_on_its_own_sheet_and_fits_it()
    {
        var sheets = LayoutEngine.Build(Pages(3), new PrintSettings());
        Assert.Equal(3, sheets.Count);
        var p = Assert.Single(sheets[0].Placements);
        Assert.InRange(p.Dest.Width / p.Dest.Height, 595f / 842 - 0.01f, 595f / 842 + 0.01f);
        Assert.True(p.Dest.Left >= 0 && p.Dest.Right <= sheets[0].Width + 0.01f);
    }

    [Fact]
    public void NUp_groups_pages_by_rows_times_columns()
    {
        var s = new PrintSettings { Mode = LayoutMode.NUp, Rows = 2, Columns = 2 };
        var sheets = LayoutEngine.Build(Pages(9), s);
        Assert.Equal(3, sheets.Count);
        Assert.Equal(4, sheets[0].Placements.Count);
        Assert.Single(sheets[2].Placements);
    }

    [Fact]
    public void NUp_order_left_then_down_mirrors_columns()
    {
        var s = new PrintSettings { Mode = LayoutMode.NUp, Rows = 1, Columns = 2, Order = PageOrder.LeftThenDown, Orientation = Orientation.Landscape };
        var sheet = LayoutEngine.Build(Pages(2), s)[0];
        var first = sheet.Placements.Single(p => Idx(p) == 1);
        var second = sheet.Placements.Single(p => Idx(p) == 2);
        Assert.True(first.Dest.X > second.Dest.X);
    }

    [Fact]
    public void Auto_orientation_picks_landscape_for_wide_pages()
    {
        var sheet = LayoutEngine.Build(Pages(1, 842, 595), new PrintSettings { AutoRotate = false })[0];
        Assert.True(sheet.Width > sheet.Height);
    }

    [Fact]
    public void Use_source_size_makes_sheet_match_each_page()
    {
        var job = new SourceJob { Id = 1, Name = "mixed", Kind = JobKind.Pdf, PageCount = 2 };
        var pages = new List<PageRef>
        {
            new() { Job = job, Index = 0, Width = 300, Height = 400 },
            new() { Job = job, Index = 1, Width = 800, Height = 500 },
        };
        var s = new PrintSettings { UseSourceSize = true, MarginLeftMm = 0, MarginRightMm = 0, MarginTopMm = 0, MarginBottomMm = 0 };
        var sheets = LayoutEngine.Build(pages, s);
        Assert.Equal(300, sheets[0].Width, 1);
        Assert.Equal(800, sheets[1].Width, 1);
        Assert.Equal(500, sheets[1].Height, 1);
    }

    [Fact]
    public void Booklet_of_eight_pages_is_imposed_for_folding()
    {
        var s = new PrintSettings { Mode = LayoutMode.Booklet };
        var sheets = LayoutEngine.Build(Pages(8), s);
        Assert.Equal(4, sheets.Count);

        int[] Order(Sheet sh) => sh.Placements.OrderBy(p => p.Dest.X).Select(Idx).ToArray();
        Assert.Equal([8, 1], Order(sheets[0]));
        Assert.Equal([2, 7], Order(sheets[1]));
        Assert.Equal([6, 3], Order(sheets[2]));
        Assert.Equal([4, 5], Order(sheets[3]));
        Assert.False(sheets[0].IsBack);
        Assert.True(sheets[1].IsBack);
        Assert.True(sheets.All(sh => sh.Width > sh.Height));
        Assert.Equal(1, sheets[0].Physical);
        Assert.Equal(1, sheets[1].Physical);
        Assert.Equal(2, sheets[2].Physical);
    }

    [Fact]
    public void Booklet_pads_to_a_multiple_of_four()
    {
        var sheets = LayoutEngine.Build(Pages(5), new PrintSettings { Mode = LayoutMode.Booklet });
        Assert.Equal(4, sheets.Count);
        Assert.Equal(5, sheets.Sum(sh => sh.Placements.Count));
    }

    [Fact]
    public void Booklet_right_binding_swaps_sides()
    {
        var s = new PrintSettings { Mode = LayoutMode.Booklet, Binding = BookletBinding.Right };
        var sheet = LayoutEngine.Build(Pages(4), s)[0];
        Assert.Equal([1, 4], sheet.Placements.OrderBy(p => p.Dest.X).Select(Idx).ToArray());
    }

    [Fact]
    public void Booklet_signatures_restart_the_imposition()
    {
        var s = new PrintSettings { Mode = LayoutMode.Booklet, SheetsPerFold = 1 };
        var sheets = LayoutEngine.Build(Pages(8), s);
        Assert.Equal(4, sheets.Count);
        Assert.Equal([4, 1], sheets[0].Placements.OrderBy(p => p.Dest.X).Select(Idx).ToArray());
        Assert.Equal([8, 5], sheets[2].Placements.OrderBy(p => p.Dest.X).Select(Idx).ToArray());
    }

    [Fact]
    public void Poster_tiles_cover_the_whole_page()
    {
        var s = new PrintSettings { Mode = LayoutMode.Poster, PosterColumns = 2, PosterRows = 3, Orientation = Orientation.Portrait };
        var sheets = LayoutEngine.Build(Pages(1), s);
        Assert.InRange(sheets.Count, 4, 6);
        var windows = sheets.Select(sh => sh.Placements.Single().Window).ToList();
        Assert.Equal(0f, windows.Min(w => w.Left), 3);
        Assert.Equal(0f, windows.Min(w => w.Top), 3);
        Assert.Equal(1f, windows.Max(w => w.Right), 3);
        Assert.Equal(1f, windows.Max(w => w.Bottom), 3);
    }

    [Fact]
    public void Poster_tile_scale_is_consistent_between_sheets()
    {
        var s = new PrintSettings { Mode = LayoutMode.Poster, PosterColumns = 2, PosterRows = 2, Orientation = Orientation.Portrait };
        var sheets = LayoutEngine.Build(Pages(1), s);
        foreach (var p in sheets.SelectMany(sh => sh.Placements))
            Assert.Equal(p.Scale * p.Window.Width * 595, p.Dest.Width, 1);
    }

    [Fact]
    public void Join_places_pages_side_by_side_without_gutter()
    {
        var s = new PrintSettings { Mode = LayoutMode.Join, JoinCount = 3, JoinSmart = false };
        var sheet = LayoutEngine.Build(Pages(3), s)[0];
        Assert.Equal(3, sheet.Placements.Count);
        Assert.True(sheet.Width > sheet.Height);
        var ordered = sheet.Placements.OrderBy(p => p.Dest.X).ToList();
        Assert.Equal([1, 2, 3], ordered.Select(Idx).ToArray());
    }

    [Fact]
    public void Join_smart_trims_empty_margins()
    {
        var hooks = new LayoutHooks { ContentBounds = _ => new RectangleF(0.1f, 0.1f, 0.8f, 0.8f) };
        var s = new PrintSettings { Mode = LayoutMode.Join, JoinCount = 2, JoinSmart = true };
        var sheet = LayoutEngine.Build(Pages(2), s, hooks)[0];
        Assert.All(sheet.Placements, p => Assert.True(p.Window.Width < 0.9f));
    }

    [Fact]
    public void Range_odd_even_reverse_and_deleted_pages()
    {
        var pages = Pages(10);
        pages[0].Deleted = true;
        var s = new PrintSettings { PageRange = "1-4, 8-", OddEven = OddEven.Odd, Reverse = true };
        // live pages are 2..10 = positions 1..9; the range keeps positions 1-4 and 8-9; odd keeps 1, 3 and 9 = source pages 2, 4 and 10
        var result = LayoutEngine.Build(pages, s).Select(sh => Idx(sh.Placements.Single())).ToArray();
        Assert.Equal([10, 4, 2], result);
    }

    [Theory]
    [InlineData("1-3", 5, "1,2,3")]
    [InlineData("2,4", 5, "2,4")]
    [InlineData("-2", 5, "1,2")]
    [InlineData("4-", 6, "4,5,6")]
    [InlineData("5-3", 6, "3,4,5")]
    public void ParseRange_understands_ranges(string text, int count, string expected)
    {
        var set = LayoutEngine.ParseRange(text, count);
        Assert.Equal(expected, string.Join(",", set!.OrderBy(n => n)));
    }

    [Fact]
    public void ParseRange_rejects_garbage_and_empty_means_all()
    {
        Assert.Null(LayoutEngine.ParseRange("abc", 5));
        Assert.Null(LayoutEngine.ParseRange("", 5));
        Assert.False(LayoutEngine.IsValidRange("1-x"));
        Assert.True(LayoutEngine.IsValidRange(""));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Map_and_unmap_round_trip(int rotation)
    {
        var page = Pages(1)[0];
        page.Rotation = rotation;
        var s = new PrintSettings { AutoRotate = false };
        var p = LayoutEngine.Build([page], s)[0].Placements[0];
        var mapped = p.MapPoint(0.3f, 0.7f);
        var back = p.Unmap(mapped.X, mapped.Y);
        Assert.Equal(0.3f, back.X, 3);
        Assert.Equal(0.7f, back.Y, 3);
    }

    [Fact]
    public void Rotation_90_maps_page_top_left_to_the_top_right_of_the_placed_page()
    {
        var page = Pages(1)[0];
        page.Rotation = 90;
        var p = LayoutEngine.Build([page], new PrintSettings { AutoRotate = false })[0].Placements[0];
        var tl = p.MapPoint(0, 0);
        Assert.Equal(p.Dest.Right, tl.X, 2);
        Assert.Equal(p.Dest.Top, tl.Y, 2);
    }

    [Fact]
    public void Watermark_macros_and_filters()
    {
        var s = new PrintSettings();
        s.Watermarks.Add(Watermark.PageNumber());
        s.Watermarks.Add(new Watermark { Text = "FIRST {file}", Pages = WatermarkPages.First });
        var sheets = LayoutEngine.Build(Pages(3, name: "a.pdf"), s);
        var number = sheets[1].Front.OfType<TextOverlay>().Single();
        Assert.Equal("2 / 3", number.Text);
        Assert.Contains(sheets[0].Front.OfType<TextOverlay>(), t => t.Text == "FIRST a.pdf");
        Assert.DoesNotContain(sheets[1].Front.OfType<TextOverlay>(), t => t.Text.StartsWith("FIRST"));
    }

    [Fact]
    public void Redaction_becomes_an_opaque_black_rect_inside_the_page()
    {
        var page = Pages(1)[0];
        page.Annotations.Add(new Annotation { Kind = AnnotationKind.Redact, Rect = new RectangleF(0.2f, 0.2f, 0.3f, 0.1f) });
        var sheet = LayoutEngine.Build([page], new PrintSettings())[0];
        var rect = sheet.Front.OfType<RectOverlay>().Single();
        Assert.Equal("#000000", rect.Fill);
        Assert.Equal(1, rect.FillOpacity);
        Assert.True(sheet.Placements[0].NeedsRaster);
        Assert.True(sheet.Placements[0].Dest.Contains(rect.Rect));
    }

    [Fact]
    public void Form_only_on_first_sheet_when_asked()
    {
        var s = new PrintSettings { FormPath = "letterhead.pdf", FormFirstSheetOnly = true };
        var sheets = LayoutEngine.Build(Pages(2), s);
        Assert.Single(sheets[0].Behind);
        Assert.Empty(sheets[1].Behind);
    }

    [Fact]
    public void Crop_shrinks_the_visible_size()
    {
        var page = Pages(1)[0];
        page.Crop = new RectangleF(0, 0, 0.5f, 1);
        var p = LayoutEngine.Build([page], new PrintSettings { AutoRotate = false })[0].Placements[0];
        Assert.True(p.Dest.Height > p.Dest.Width);
        Assert.Equal(0.5f, p.Window.Width);
    }

    [Fact]
    public void Document_undo_restores_previous_edits()
    {
        var doc = new DocumentSet();
        doc.AddPages(Pages(3));
        doc.Rotate([0], 90);
        doc.SetDeleted([1], true);
        Assert.Equal(90, doc.Pages[0].Rotation);
        doc.Undo();
        Assert.False(doc.Pages[1].Deleted);
        doc.Undo();
        Assert.Equal(0, doc.Pages[0].Rotation);
        doc.Redo();
        Assert.Equal(90, doc.Pages[0].Rotation);
    }

    [Fact]
    public void Document_move_reorders_pages()
    {
        var doc = new DocumentSet();
        doc.AddPages(Pages(5));
        doc.Move([3, 4], 0);
        Assert.Equal([3, 4, 0, 1, 2], doc.Pages.Select(p => p.Index).ToArray());
        doc.Move([0], 5);
        Assert.Equal([4, 0, 1, 2, 3], doc.Pages.Select(p => p.Index).ToArray());
    }
}
