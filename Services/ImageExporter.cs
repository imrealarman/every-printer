using System.Drawing;
using System.Drawing.Imaging;
using EveryPrinter.Models;

namespace EveryPrinter.Services;

/// <summary>Saves each sheet as a picture file: PNG, JPEG, TIFF, BMP or TGA.</summary>
public static class ImageExporter
{
    public static string Extension(RasterFormat format) => format switch
    {
        RasterFormat.Jpeg => ".jpg",
        RasterFormat.Tiff => ".tif",
        RasterFormat.Bmp => ".bmp",
        RasterFormat.Tga => ".tga",
        _ => ".png",
    };

    public static List<string> Export(IReadOnlyList<Sheet> sheets, PrintSettings s, SheetRenderer renderer, string folder, string baseName,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(folder);
        var digits = Math.Max(2, sheets.Count.ToString().Length);
        var written = new List<string>();
        for (var i = 0; i < sheets.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            using var bitmap = renderer.Render(sheets[i], s, Math.Clamp(s.ImageDpi, 50, 600) / 72f, cancel);
            var path = Path.Combine(folder, $"{baseName}-{(i + 1).ToString().PadLeft(digits, '0')}{Extension(s.ImageFormat)}");
            Save(bitmap, path, s);
            written.Add(path);
            progress?.Report((i + 1.0) / sheets.Count);
        }
        return written;
    }

    public static void Save(Bitmap bitmap, string path, PrintSettings s)
    {
        switch (s.ImageFormat)
        {
            case RasterFormat.Jpeg:
            {
                var encoder = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(s.PdfJpegQuality, 20, 100));
                using (var flat = Flatten(bitmap)) flat.Save(path, encoder, parameters);
                break;
            }
            case RasterFormat.Tiff: bitmap.Save(path, ImageFormat.Tiff); break;
            case RasterFormat.Bmp: bitmap.Save(path, ImageFormat.Bmp); break;
            case RasterFormat.Tga: WriteTga(bitmap, path); break;
            default: bitmap.Save(path, ImageFormat.Png); break;
        }
    }

    private static Bitmap Flatten(Bitmap source)
    {
        var flat = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        flat.SetResolution(source.HorizontalResolution, source.VerticalResolution);
        using var g = Graphics.FromImage(flat);
        g.Clear(Color.White);
        g.DrawImageUnscaled(source, 0, 0);
        return flat;
    }

    /// <summary>Uncompressed 32-bit Targa, top-left origin. GDI+ can't write TGA, and the format is simple enough to do by hand.</summary>
    private static void WriteTga(Bitmap bitmap, string path)
    {
        using var source = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(source)) { g.Clear(Color.White); g.DrawImageUnscaled(bitmap, 0, 0); }

        var data = source.LockBits(new Rectangle(0, 0, source.Width, source.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var file = new BinaryWriter(File.Create(path));
            file.Write(new byte[] { 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
            file.Write((ushort)source.Width);
            file.Write((ushort)source.Height);
            file.Write((byte)32);
            file.Write((byte)0x28); // 8 alpha bits, origin at the top left
            var row = new byte[source.Width * 4];
            for (var y = 0; y < source.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                file.Write(row);
            }
        }
        finally { source.UnlockBits(data); }
    }
}
