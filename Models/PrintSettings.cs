using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveryPrinter.Models;

public enum LayoutMode { Normal, NUp, Booklet, Poster, Join }
public enum Orientation { Auto, Portrait, Landscape }
public enum ScaleMode { Fit, ShrinkOnly, Fill, Actual, Custom }
public enum PageOrder { RightThenDown, LeftThenDown, DownThenRight, DownThenLeft }
public enum Anchor { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }
public enum DuplexMode { OneSided, LongEdge, ShortEdge, ManualLongEdge, ManualShortEdge }
public enum BookletBinding { Left, Right }
public enum BookletAxis { Horizontal, Vertical }
public enum OddEven { All, Odd, Even }
public enum WatermarkKind { Text, Image }
public enum WatermarkScope { PerPage, PerSheet }
public enum WatermarkPages { All, First, Last, Odd, Even }
public enum RasterFormat { Png, Jpeg, Tiff, Bmp, Tga }

/// <summary>A text or image mark drawn over the pages: watermark, note, stamp or page number. Text may use macros such as {page}.</summary>
public sealed class Watermark
{
    public string Name { get; set; } = "Watermark";
    public bool Enabled { get; set; } = true;
    public WatermarkKind Kind { get; set; } = WatermarkKind.Text;
    public string Text { get; set; } = "CONFIDENTIAL";
    public string ImagePath { get; set; } = "";
    public string FontFamily { get; set; } = "Segoe UI";
    public float FontSize { get; set; } = 60;
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public string Color { get; set; } = "#E34850";
    public double Opacity { get; set; } = 0.25;
    public double Angle { get; set; } = -35;
    public Anchor Anchor { get; set; } = Anchor.Center;
    public double OffsetXMm { get; set; }
    public double OffsetYMm { get; set; }
    public bool Tile { get; set; }
    public WatermarkScope Scope { get; set; } = WatermarkScope.PerPage;
    public WatermarkPages Pages { get; set; } = WatermarkPages.All;

    /// <summary>For image marks: width as a fraction of the page (or sheet) width.</summary>
    public double ImageWidth { get; set; } = 0.4;

    public Watermark Clone() => (Watermark)MemberwiseClone();

    public static Watermark PageNumber() => new()
    {
        Name = "Page number", Text = "{page} / {pages}", FontSize = 9, Bold = false, Color = "#444444", Opacity = 1,
        Angle = 0, Anchor = Anchor.Bottom, OffsetYMm = -6,
    };
}

/// <summary>
/// Everything that decides how pages become sheets and how the sheets are printed or saved. A saved copy of this object is a "theme":
/// one pick restores page layout, margins, gutters, effects, watermarks and output options together.
/// </summary>
public sealed class PrintSettings
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // ---- Page selection ----
    public string PageRange { get; set; } = "";
    public OddEven OddEven { get; set; } = OddEven.All;
    public bool Reverse { get; set; }
    public bool SkipBlankPages { get; set; }

    // ---- Paper ----
    public string Paper { get; set; } = "A4";
    public double CustomWidthMm { get; set; } = 210;
    public double CustomHeightMm { get; set; } = 297;

    /// <summary>One page per sheet, each sheet as big as its page: a job can mix paper sizes.</summary>
    public bool UseSourceSize { get; set; }
    public Orientation Orientation { get; set; } = Orientation.Auto;

    // ---- Layout ----
    public LayoutMode Mode { get; set; } = LayoutMode.Normal;
    public int Rows { get; set; } = 2;
    public int Columns { get; set; } = 2;
    public PageOrder Order { get; set; } = PageOrder.RightThenDown;
    public double MarginTopMm { get; set; } = 6;
    public double MarginBottomMm { get; set; } = 6;
    public double MarginLeftMm { get; set; } = 6;
    public double MarginRightMm { get; set; } = 6;
    public double GutterXMm { get; set; } = 4;
    public double GutterYMm { get; set; } = 4;
    public ScaleMode Scale { get; set; } = ScaleMode.Fit;
    public double ScalePercent { get; set; } = 100;
    public Anchor ContentAnchor { get; set; } = Anchor.Center;
    public bool AutoRotate { get; set; } = true;

    /// <summary>Join mode: how many pages are placed side by side on one sheet.</summary>
    public int JoinCount { get; set; } = 2;

    /// <summary>Join mode: trim the empty margins of each page first, so tables that run across pages meet cleanly.</summary>
    public bool JoinSmart { get; set; } = true;

    // ---- Booklet ----
    public BookletBinding Binding { get; set; } = BookletBinding.Left;
    public BookletAxis BookletAxis { get; set; } = BookletAxis.Horizontal;

    /// <summary>Sheets per fold (a signature). 0 folds the whole job into one stack.</summary>
    public int SheetsPerFold { get; set; }

    // ---- Poster ----
    public int PosterColumns { get; set; } = 2;
    public int PosterRows { get; set; } = 2;
    public double PosterOverlapMm { get; set; } = 8;
    public bool PosterMarks { get; set; } = true;
    public bool PosterLabels { get; set; } = true;

    // ---- Decoration ----
    public bool PageBorders { get; set; }
    public double BorderWidthPt { get; set; } = 0.75;
    public bool CropMarks { get; set; }
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
    public List<Watermark> Watermarks { get; set; } = [];

    /// <summary>A PDF or image printed behind every sheet: a letterhead or pre-printed form.</summary>
    public string FormPath { get; set; } = "";
    public bool FormFirstSheetOnly { get; set; }

    // ---- Effects ----
    public bool Grayscale { get; set; }
    public bool Invert { get; set; }
    public int Brightness { get; set; }
    public int Contrast { get; set; }

    /// <summary>Lightens the output to use less ink or toner (0 = off, up to 80 %).</summary>
    public int InkSaving { get; set; }

    // ---- Printing ----
    public string Printer { get; set; } = "";
    public int Copies { get; set; } = 1;
    public bool Collate { get; set; } = true;
    public DuplexMode Duplex { get; set; } = DuplexMode.OneSided;

    /// <summary>Send sheets as bitmaps, for printers whose driver draws vector pages badly.</summary>
    public bool BitmapMode { get; set; }
    public int PrintDpi { get; set; } = 300;

    // ---- PDF ----
    public bool PdfKeepVector { get; set; } = true;
    public int PdfDpi { get; set; } = 200;
    public int PdfJpegQuality { get; set; } = 85;
    public string PdfTitle { get; set; } = "";
    public string PdfAuthor { get; set; } = "";
    public string PdfUserPassword { get; set; } = "";
    public string PdfOwnerPassword { get; set; } = "";
    public bool PdfAllowPrint { get; set; } = true;
    public bool PdfAllowCopy { get; set; } = true;
    public bool PdfAllowEdit { get; set; } = true;

    // ---- Images ----
    public RasterFormat ImageFormat { get; set; } = RasterFormat.Png;
    public int ImageDpi { get; set; } = 200;

    public bool HasEffects => Grayscale || Invert || Brightness != 0 || Contrast != 0 || InkSaving > 0;

    public PrintSettings Clone()
    {
        var copy = (PrintSettings)MemberwiseClone();
        copy.Watermarks = Watermarks.Select(w => w.Clone()).ToList();
        return copy;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static PrintSettings FromJson(string json) => JsonSerializer.Deserialize<PrintSettings>(json, Json) ?? new PrintSettings();
}
