using System.Drawing;
using System.Drawing.Imaging;
using EveryPrinter.Models;

namespace EveryPrinter.Services;

/// <summary>Colour effects (grayscale, invert, brightness, contrast, ink saving) as one 5x5 colour matrix.</summary>
public static class Effects
{
    private static readonly float[,] Identity =
    {
        { 1, 0, 0, 0, 0 }, { 0, 1, 0, 0, 0 }, { 0, 0, 1, 0, 0 }, { 0, 0, 0, 1, 0 }, { 0, 0, 0, 0, 1 },
    };

    /// <summary>Drawing attributes for page pictures: the colour effects, plus edge wrapping that avoids a half-pixel fringe on scaled images.</summary>
    public static ImageAttributes Create(PrintSettings s, double alpha = 1)
    {
        var attributes = new ImageAttributes();
        attributes.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
        if (s.HasEffects || alpha < 0.999)
            attributes.SetColorMatrix(new ColorMatrix(ToJagged(Matrix(s, alpha))), ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
        return attributes;
    }

    public static float[,] Matrix(PrintSettings s, double alpha = 1)
    {
        var m = (float[,])Identity.Clone();
        if (s.Grayscale)
        {
            m = Multiply(m, new float[,]
            {
                { 0.299f, 0.299f, 0.299f, 0, 0 }, { 0.587f, 0.587f, 0.587f, 0, 0 }, { 0.114f, 0.114f, 0.114f, 0, 0 }, { 0, 0, 0, 1, 0 }, { 0, 0, 0, 0, 1 },
            });
        }
        if (s.Invert)
            m = Multiply(m, new float[,] { { -1, 0, 0, 0, 0 }, { 0, -1, 0, 0, 0 }, { 0, 0, -1, 0, 0 }, { 0, 0, 0, 1, 0 }, { 1, 1, 1, 0, 1 } });
        if (s.Contrast != 0)
        {
            // Pivot around mid grey: c = 1 at 0, up to 3x at +100 and down to flat grey at -100.
            var c = s.Contrast >= 0 ? 1 + s.Contrast / 50f : 1 + s.Contrast / 100f;
            var t = 0.5f * (1 - c);
            m = Multiply(m, new float[,] { { c, 0, 0, 0, 0 }, { 0, c, 0, 0, 0 }, { 0, 0, c, 0, 0 }, { 0, 0, 0, 1, 0 }, { t, t, t, 0, 1 } });
        }
        if (s.Brightness != 0)
        {
            var b = s.Brightness / 100f * 0.5f;
            m = Multiply(m, new float[,] { { 1, 0, 0, 0, 0 }, { 0, 1, 0, 0, 0 }, { 0, 0, 1, 0, 0 }, { 0, 0, 0, 1, 0 }, { b, b, b, 0, 1 } });
        }
        if (s.InkSaving > 0)
        {
            // Blend toward white: the darker the ink, the more it is lightened, so black text stays readable.
            var k = Math.Clamp(s.InkSaving, 0, 80) / 100f;
            var keep = 1 - k;
            m = Multiply(m, new float[,] { { keep, 0, 0, 0, 0 }, { 0, keep, 0, 0, 0 }, { 0, 0, keep, 0, 0 }, { 0, 0, 0, 1, 0 }, { k, k, k, 0, 1 } });
        }
        if (alpha < 0.999)
            m = Multiply(m, new float[,] { { 1, 0, 0, 0, 0 }, { 0, 1, 0, 0, 0 }, { 0, 0, 1, 0, 0 }, { 0, 0, 0, (float)alpha, 0 }, { 0, 0, 0, 0, 1 } });
        return m;
    }

    /// <summary>Applies the effects to a single colour (for sampling and tests).</summary>
    public static Color Apply(PrintSettings s, Color c)
    {
        var m = Matrix(s);
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
        float R = r * m[0, 0] + g * m[1, 0] + b * m[2, 0] + m[4, 0];
        float G = r * m[0, 1] + g * m[1, 1] + b * m[2, 1] + m[4, 1];
        float B = r * m[0, 2] + g * m[1, 2] + b * m[2, 2] + m[4, 2];
        static int Byte(float v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return Color.FromArgb(c.A, Byte(R), Byte(G), Byte(B));
    }

    private static float[][] ToJagged(float[,] m)
    {
        var rows = new float[5][];
        for (var r = 0; r < 5; r++)
        {
            rows[r] = new float[5];
            for (var c = 0; c < 5; c++) rows[r][c] = m[r, c];
        }
        return rows;
    }

    /// <summary>Row-vector convention (as GDI+ uses): the result applies <paramref name="a"/> first, then <paramref name="b"/>.</summary>
    private static float[,] Multiply(float[,] a, float[,] b)
    {
        var result = new float[5, 5];
        for (var i = 0; i < 5; i++)
        for (var j = 0; j < 5; j++)
        {
            float sum = 0;
            for (var k = 0; k < 5; k++) sum += a[i, k] * b[k, j];
            result[i, j] = sum;
        }
        return result;
    }
}
