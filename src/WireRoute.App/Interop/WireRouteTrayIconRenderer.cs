using System.Runtime.InteropServices;

namespace WireRoute.App.Interop;

internal static class WireRouteTrayIconRenderer
{
    private const int SamplesPerAxis = 4;

    public static nint Create(string style, int size, bool active, bool transitioning)
    {
        size = Math.Max(16, size);
        var pixels = Render(style, size, active, transitioning);
        var bitmapInfo = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = size,
                Height = -size,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
                SizeImage = (uint)(size * size * 4),
            },
        };
        var colorBitmap = CreateDIBSection(
            0,
            ref bitmapInfo,
            0,
            out var bits,
            0,
            0);
        if (colorBitmap == 0 || bits == 0)
        {
            throw new InvalidOperationException("WireRoute could not create a tray icon bitmap.");
        }

        var maskBitmap = CreateBitmap(size, size, 1, 1, 0);
        if (maskBitmap == 0)
        {
            _ = DeleteObject(colorBitmap);
            throw new InvalidOperationException("WireRoute could not create a tray icon mask.");
        }

        try
        {
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var iconInfo = new IconInfo
            {
                IsIcon = true,
                ColorBitmap = colorBitmap,
                MaskBitmap = maskBitmap,
            };
            var icon = CreateIconIndirect(ref iconInfo);
            return icon != 0
                ? icon
                : throw new InvalidOperationException("WireRoute could not create a tray icon.");
        }
        finally
        {
            _ = DeleteObject(maskBitmap);
            _ = DeleteObject(colorBitmap);
        }
    }

    private static int[] Render(string style, int size, bool active, bool transitioning)
    {
        var normalizedStyle = style.Trim().ToUpperInvariant();
        var clear = normalizedStyle == "CLEAR OUTLINE";
        var legacy = normalizedStyle == "LEGACY WIREGUARD";
        var (primary, accent) = normalizedStyle switch
        {
            "DARK" => (new Rgb(0, 0, 0), new Rgb(0, 0, 0)),
            "LIGHT" => (new Rgb(255, 255, 255), new Rgb(255, 255, 255)),
            "CLEAR OUTLINE" => (new Rgb(245, 248, 252), new Rgb(245, 248, 252)),
            "WIRE ROUTE COLOR" or "WIREROUTE COLOR" =>
                (new Rgb(20, 126, 255), new Rgb(0, 214, 255)),
            "LEGACY WIREGUARD" => (new Rgb(225, 225, 225), new Rgb(202, 202, 202)),
            _ => (new Rgb(245, 248, 252), new Rgb(0, 204, 238)),
        };
        var stateAlpha = active ? 1d : transitioning ? 0.76d : 0.52d;
        var output = new int[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var primarySamples = 0;
                var accentSamples = 0;
                for (var sy = 0; sy < SamplesPerAxis; sy++)
                {
                    for (var sx = 0; sx < SamplesPerAxis; sx++)
                    {
                        var px = ((x + (sx + 0.5) / SamplesPerAxis) / size) * 20;
                        var py = ((y + (sy + 0.5) / SamplesPerAxis) / size) * 20;
                        var sample = Sample(px, py, clear, legacy, transitioning);
                        if (sample == SampleColor.Primary)
                        {
                            primarySamples++;
                        }
                        else if (sample == SampleColor.Accent)
                        {
                            accentSamples++;
                        }
                    }
                }

                var total = SamplesPerAxis * SamplesPerAxis;
                if (primarySamples + accentSamples == 0)
                {
                    continue;
                }

                var primaryWeight = (double)primarySamples / total;
                var accentWeight = (double)accentSamples / total;
                var coverage = Math.Min(1, primaryWeight + accentWeight);
                var alpha = coverage * stateAlpha;
                var red = (primary.R * primaryWeight + accent.R * accentWeight) / coverage;
                var green = (primary.G * primaryWeight + accent.G * accentWeight) / coverage;
                var blue = (primary.B * primaryWeight + accent.B * accentWeight) / coverage;
                output[y * size + x] = PackPremultiplied(red, green, blue, alpha);
            }
        }
        return output;
    }

    private static SampleColor Sample(
        double x,
        double y,
        bool clear,
        bool legacy,
        bool transitioning)
    {
        var primary = OnSegment(x, y, 4.2, 4.2, 10, 16.4, 1.03)
            || OnSegment(x, y, 10, 16.4, 15.8, 4.2, 1.03)
            || OnCircle(x, y, 4.2, 4.2, 1.75, 0.73)
            || OnCircle(x, y, 10, 16.4, 1.75, 0.73)
            || OnCircle(x, y, 15.8, 4.2, 1.75, 0.73);
        var halo = OnEllipse(x, y, 10, 10.25, 5.8, 2.55, 0.72);
        var shield = new[]
        {
            (10d, 12.8d),
            (12.15d, 13.65d),
            (11.8d, 15.75d),
            (10d, 17.3d),
            (8.2d, 15.75d),
            (7.85d, 13.65d),
        };
        var shieldOutline = OnPolygon(x, y, shield, 0.58);
        var shieldFill = !clear && InsidePolygon(x, y, shield);
        var pulse = transitioning && OnCircle(x, y, 10, 16.4, 3.1, 0.45);

        if (shieldOutline || shieldFill || primary)
        {
            return SampleColor.Primary;
        }
        if (halo || pulse)
        {
            return SampleColor.Accent;
        }
        if (legacy && OnCircle(x, y, 10, 10, 8.6, 0.7))
        {
            return SampleColor.Accent;
        }
        return SampleColor.Transparent;
    }

    private static bool OnSegment(
        double x,
        double y,
        double x1,
        double y1,
        double x2,
        double y2,
        double halfWidth)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0
            ? 0
            : Math.Clamp(((x - x1) * dx + (y - y1) * dy) / lengthSquared, 0, 1);
        var closestX = x1 + t * dx;
        var closestY = y1 + t * dy;
        var distanceX = x - closestX;
        var distanceY = y - closestY;
        return Math.Sqrt(distanceX * distanceX + distanceY * distanceY) <= halfWidth;
    }

    private static bool OnCircle(
        double x,
        double y,
        double centerX,
        double centerY,
        double radius,
        double halfWidth) =>
        Math.Abs(Math.Sqrt(
            (x - centerX) * (x - centerX)
            + (y - centerY) * (y - centerY)) - radius) <= halfWidth;

    private static bool OnEllipse(
        double x,
        double y,
        double centerX,
        double centerY,
        double radiusX,
        double radiusY,
        double halfWidth)
    {
        var normalized = Math.Sqrt(
            Math.Pow((x - centerX) / radiusX, 2)
            + Math.Pow((y - centerY) / radiusY, 2));
        return Math.Abs(normalized - 1) <= halfWidth / Math.Min(radiusX, radiusY);
    }

    private static bool OnPolygon(
        double x,
        double y,
        IReadOnlyList<(double X, double Y)> points,
        double halfWidth)
    {
        for (var index = 0; index < points.Count; index++)
        {
            var first = points[index];
            var second = points[(index + 1) % points.Count];
            if (OnSegment(x, y, first.X, first.Y, second.X, second.Y, halfWidth))
            {
                return true;
            }
        }
        return false;
    }

    private static bool InsidePolygon(
        double x,
        double y,
        IReadOnlyList<(double X, double Y)> points)
    {
        var inside = false;
        for (int current = 0, previous = points.Count - 1;
             current < points.Count;
             previous = current++)
        {
            var a = points[current];
            var b = points[previous];
            if ((a.Y > y) != (b.Y > y)
                && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static int PackPremultiplied(double red, double green, double blue, double alpha)
    {
        var a = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        var r = (int)Math.Round(red * alpha);
        var g = (int)Math.Round(green * alpha);
        var b = (int)Math.Round(blue * alpha);
        return a << 24 | r << 16 | g << 8 | b;
    }

    private enum SampleColor
    {
        Transparent,
        Primary,
        Accent,
    }

    private readonly record struct Rgb(double R, double G, double B);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool IsIcon;
        public uint XHotspot;
        public uint YHotspot;
        public nint MaskBitmap;
        public nint ColorBitmap;
    }

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(
        nint deviceContext,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out nint bits,
        nint section,
        uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint CreateBitmap(
        int width,
        int height,
        uint planes,
        uint bitsPerPixel,
        nint bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);

    [DllImport("user32.dll")]
    private static extern nint CreateIconIndirect(ref IconInfo iconInfo);
}
