namespace EveryPrinter.Models;

/// <summary>Standard paper sizes, in millimetres (portrait).</summary>
public static class Paper
{
    public const double PointsPerMm = 72 / 25.4;
    public const string Custom = "Custom";

    public static readonly IReadOnlyList<(string Name, double Width, double Height)> Sizes =
    [
        ("A0", 841, 1189), ("A1", 594, 841), ("A2", 420, 594), ("A3", 297, 420), ("A4", 210, 297), ("A5", 148, 210), ("A6", 105, 148),
        ("B4", 250, 353), ("B5", 176, 250),
        ("Letter", 215.9, 279.4), ("Legal", 215.9, 355.6), ("Tabloid", 279.4, 431.8), ("Executive", 184.15, 266.7),
        ("Statement", 139.7, 215.9), ("Folio", 210, 330),
    ];

    /// <summary>Portrait size of the named paper in points; unknown names fall back to A4.</summary>
    public static (float Width, float Height) Points(PrintSettings s)
    {
        if (s.Paper == Custom)
            return ((float)(Math.Max(10, s.CustomWidthMm) * PointsPerMm), (float)(Math.Max(10, s.CustomHeightMm) * PointsPerMm));
        var size = Sizes.FirstOrDefault(p => p.Name == s.Paper);
        if (size.Name is null) size = Sizes[4];
        return ((float)(size.Width * PointsPerMm), (float)(size.Height * PointsPerMm));
    }

    public static float Mm(double mm) => (float)(mm * PointsPerMm);
}
