using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Spotlight.Interop;

/// <summary>Loads the same icons Explorer shows, for files, folders and shell:AppsFolder entries.</summary>
internal static class ShellIcons
{
    /// <summary>Must be called on an STA thread.</summary>
    public static ImageSource? Load(string parsingName, int size)
    {
        int hr = Native.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, typeof(Native.IShellItemImageFactory).GUID, out var factory);
        if (hr != 0 || factory == null) return null;
        try
        {
            var requested = new Native.SIZE { cx = size, cy = size };
            hr = factory.GetImage(requested, Native.SIIGBF_ICONONLY | Native.SIIGBF_BIGGERSIZEOK, out IntPtr hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero) return null;
            try
            {
                return ToBitmapSource(hBitmap);
            }
            finally
            {
                Native.DeleteObject(hBitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    // Imaging.CreateBitmapSourceFromHBitmap drops the alpha channel, so copy the pixels ourselves.
    private static BitmapSource? ToBitmapSource(IntPtr hBitmap)
    {
        if (Native.GetObject(hBitmap, Marshal.SizeOf<Native.BITMAP>(), out var bm) == 0) return null;
        int width = bm.bmWidth, height = Math.Abs(bm.bmHeight);
        if (width <= 0 || height <= 0) return null;

        var info = new Native.BITMAPINFO
        {
            biSize = 40,
            biWidth = width,
            biHeight = -height, // top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        IntPtr hdc = Native.GetDC(IntPtr.Zero);
        try
        {
            if (Native.GetDIBits(hdc, hBitmap, 0, (uint)height, pixels, ref info, 0) == 0) return null;
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, hdc);
        }

        // Some images come back with no alpha at all; treat those as opaque.
        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) { hasAlpha = true; break; }
        }
        if (!hasAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        source.Freeze();
        return source;
    }
}
