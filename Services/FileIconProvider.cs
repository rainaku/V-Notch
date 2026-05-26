using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace VNotch.Services;
internal static class FileIconProvider
{
    // ─── Icon Cache ───
    // Key: full file path (case-insensitive). Frozen ImageSource values are thread-safe.
    private static readonly ConcurrentDictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCacheSize = 200;

    /// <summary>Evicts a single path from the cache (e.g. after rename/delete).</summary>
    public static void Invalidate(string filePath) => _iconCache.TryRemove(filePath, out _);

    /// <summary>Clears the entire icon cache.</summary>
    public static void ClearCache() => _iconCache.Clear();

    #region Native interop

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        [In] IntPtr pbc,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8746c1f01a3b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage([In, MarshalAs(UnmanagedType.Struct)] SIZE size, [In] int flags, [Out] out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    private static readonly Guid _shellItemImageFactoryIid = new Guid("bcc18b79-ba16-442f-80c4-8746c1f01a3b");

    #endregion
public static ImageSource? GetFileIcon(string filePath)
    {
        // Fast path: return cached icon without any I/O
        if (_iconCache.TryGetValue(filePath, out var cached))
            return cached;

        try
        {
            if (!File.Exists(filePath)) return null;

            // Prevent unbounded growth
            if (_iconCache.Count >= MaxCacheSize)
                _iconCache.Clear();

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            bool isImage = ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif";
            bool isVideo = ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm";

            ImageSource? result = null;

            if (isImage || isVideo)
            {
                if (isVideo)
                {
                    result = TryGetVideoThumbnail(filePath);
                }

                result ??= TryGetShellThumbnail(filePath, 128);

                if (result == null && isImage)
                {
                    result = TryGetDirectImage(filePath);
                }
            }

            result ??= TryGetAssociatedIcon(filePath);

            _iconCache[filePath] = result;
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryGetVideoThumbnail(string filePath)
    {
        try
        {
            var task = Task.Run(async () =>
            {
                var file = await StorageFile.GetFileFromPathAsync(filePath);
                var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 128);
                if (thumbnail == null) return null;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = thumbnail.AsStream();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                return (ImageSource?)bitmap;
            });
            return task.Result;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryGetShellThumbnail(string filePath, int size)
    {
        try
        {
            SHCreateItemFromParsingName(filePath, IntPtr.Zero, _shellItemImageFactoryIid, out var factory);
            int hr = factory.GetImage(new SIZE { cx = size, cy = size }, 0x01, out IntPtr hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero) return null;

            try
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryGetDirectImage(string filePath)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(filePath);
            bitmap.DecodePixelWidth = 128;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryGetAssociatedIcon(string filePath)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(filePath);
            if (icon == null) return null;

            using var bmp = icon.ToBitmap();
            var hBitmap = bmp.GetHbitmap();
            try
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
    }
}
