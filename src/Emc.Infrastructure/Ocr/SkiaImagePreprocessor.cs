using Emc.Application.Ocr;
using SkiaSharp;

namespace Emc.Infrastructure.Ocr;

/// <summary>
/// Page conditioning before recognition, with SkiaSharp (already in the bundle for rendering):
///
///   1. rotate by the orientation the caller decided (0/90/180/270, from OSD or voting);
///   2. estimate small skew (±5°) by projection profile on a downscaled binarized copy - the
///      angle at which row sums have the highest variance is the angle at which text lines are
///      horizontal - and rotate by its negative;
///   3. scale to the engine's target DPI;
///   4. grayscale, then a linear contrast stretch between the 1st and 99th percentile of
///      luminance, which lifts a faint photocopy without inventing detail.
///
/// Deterministic for a given input and version; no randomness, no learned model.
/// </summary>
#pragma warning disable CA1416 // SkiaSharp is supported on the deployment and test platforms; see PdfiumRasterizer.
public sealed class SkiaImagePreprocessor : IImagePreprocessor
{
    public const string CurrentVersion = "skia-prep/1: rotate, deskew(projection ±5° step 0.25°), scale, gray, stretch(p1-p99)";

    private readonly int _targetDpi;

    public SkiaImagePreprocessor(int targetDpi = 300)
    {
        _targetDpi = targetDpi <= 0 ? 300 : targetDpi;
    }

    public string Version => CurrentVersion;

    public PreprocessedImage Preprocess(byte[] png, int sourceDpi, int rotateClockwiseDegrees, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        using var source = SKBitmap.Decode(png) ?? throw new OcrEngineException(Emc.Domain.Ocr.OcrFailureCategory.InvalidImage);
        ct.ThrowIfCancellationRequested();

        var rotation = ((rotateClockwiseDegrees % 360) + 360) % 360;
        if (rotation % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rotateClockwiseDegrees), "Orientation is a multiple of 90 degrees.");
        }

        using var upright = Rotate(source, rotation);
        ct.ThrowIfCancellationRequested();

        var skew = EstimateSkewDegrees(upright);
        ct.ThrowIfCancellationRequested();

        var scale = sourceDpi > 0 ? (float)_targetDpi / sourceDpi : 1f;
        var width = Math.Max(1, (int)Math.Round(upright.Width * scale));
        var height = Math.Max(1, (int)Math.Round(upright.Height * scale));

        using var output = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(output))
        {
            canvas.Clear(SKColors.White);
            canvas.Translate(width / 2f, height / 2f);
            canvas.RotateDegrees((float)-skew);
            canvas.Scale(scale);
            canvas.Translate(-upright.Width / 2f, -upright.Height / 2f);
            using var paint = new SKPaint { ColorFilter = Grayscale() };
            canvas.DrawBitmap(upright, 0, 0, SKSamplingOptions.Default, paint);
        }

        ct.ThrowIfCancellationRequested();
        StretchContrast(output);

        using var image = SKImage.FromBitmap(output);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new PreprocessedImage(data.ToArray(), width, height, rotation, skew, _targetDpi);
    }

    private static SKBitmap Rotate(SKBitmap source, int degrees)
    {
        if (degrees == 0)
        {
            return source.Copy();
        }

        var swap = degrees is 90 or 270;
        var rotated = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(rotated);
        canvas.Clear(SKColors.White);
        canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
        return rotated;
    }

    private static SKColorFilter Grayscale()
        => SKColorFilter.CreateColorMatrix(
        [
            0.2126f, 0.7152f, 0.0722f, 0, 0,
            0.2126f, 0.7152f, 0.0722f, 0, 0,
            0.2126f, 0.7152f, 0.0722f, 0, 0,
            0, 0, 0, 1, 0
        ]);

    /// <summary>
    /// Projection-profile skew estimate on a copy no wider than 800 px. For each candidate
    /// angle the dark pixels are projected onto rows along that angle; horizontal text gives the
    /// peakiest profile (highest variance). Returns the angle in degrees, positive = text runs
    /// clockwise-down, so the caller rotates by its negative.
    /// </summary>
    internal static double EstimateSkewDegrees(SKBitmap bitmap)
    {
        var factor = Math.Max(1, (int)Math.Ceiling(bitmap.Width / 800.0));
        var w = bitmap.Width / factor;
        var h = bitmap.Height / factor;
        if (w < 50 || h < 50)
        {
            return 0;
        }

        using var small = bitmap.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), SKSamplingOptions.Default);
        if (small is null)
        {
            return 0;
        }

        var pixels = small.Pixels;
        var dark = new List<(int X, int Y)>();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var p = pixels[y * w + x];
                var lum = 0.2126 * p.Red + 0.7152 * p.Green + 0.0722 * p.Blue;
                if (lum < 128)
                {
                    dark.Add((x, y));
                }
            }
        }

        if (dark.Count < 200 || dark.Count > w * h * 0.6)
        {
            return 0;
        }

        var bestAngle = 0.0;
        var bestScore = double.MinValue;
        var bins = new double[h + 2 * (int)Math.Ceiling(w * Math.Tan(5 * Math.PI / 180)) + 4];
        var offset = bins.Length / 2 - h / 2;
        for (var angle = -5.0; angle <= 5.0 + 1e-9; angle += 0.25)
        {
            Array.Clear(bins);
            var tan = Math.Tan(angle * Math.PI / 180);
            foreach (var (x, y) in dark)
            {
                var row = (int)Math.Round(y - x * tan) + offset;
                if (row >= 0 && row < bins.Length)
                {
                    bins[row]++;
                }
            }

            var mean = bins.Average();
            var variance = bins.Sum(b => (b - mean) * (b - mean)) / bins.Length;
            if (variance > bestScore + 1e-9)
            {
                bestScore = variance;
                bestAngle = angle;
            }
        }

        return Math.Abs(bestAngle) < 0.2 ? 0 : bestAngle;
    }

    private static void StretchContrast(SKBitmap bitmap)
    {
        var span = bitmap.GetPixelSpan();
        var histogram = new int[256];
        var count = span.Length / 4;
        for (var i = 0; i < span.Length; i += 4)
        {
            histogram[span[i]]++; // gray: R == G == B
        }

        var low = Percentile(histogram, count, 0.01);
        var high = Percentile(histogram, count, 0.99);
        if (high - low < 32)
        {
            return; // nearly flat: a blank page, or already a hard binary scan
        }

        var lut = new byte[256];
        for (var v = 0; v < 256; v++)
        {
            lut[v] = (byte)Math.Clamp((int)Math.Round((v - low) * 255.0 / (high - low)), 0, 255);
        }

        var pixels = bitmap.GetPixels();
        unsafe
        {
            var p = (byte*)pixels.ToPointer();
            for (var i = 0; i < span.Length; i += 4)
            {
                var g = lut[p[i]];
                p[i] = g; p[i + 1] = g; p[i + 2] = g;
            }
        }
    }

    private static int Percentile(int[] histogram, int count, double fraction)
    {
        var target = (long)Math.Round(count * fraction);
        long seen = 0;
        for (var v = 0; v < 256; v++)
        {
            seen += histogram[v];
            if (seen >= target)
            {
                return v;
            }
        }

        return 255;
    }
}
#pragma warning restore CA1416
