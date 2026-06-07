using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace VNotch.Services;
internal static class FileIconProvider
{
    private sealed class CacheEntry
    {
        public ImageSource? Icon;
        public long LastAccess;
    }

    private static readonly ConcurrentDictionary<string, CacheEntry> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCacheSize = 200;
    // Monotonic clock used to order entries by recency of access.
    private static long _accessCounter;
    // Serializes eviction so multiple threads don't trim concurrently.
    private static readonly object _evictLock = new();

    public static void Invalidate(string filePath) => _iconCache.TryRemove(filePath, out _);

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
        {
            cached.LastAccess = Interlocked.Increment(ref _accessCounter);
            return cached.Icon;
        }

        try
        {
            if (!File.Exists(filePath)) return null;

            // Keep the cache bounded by evicting the least-recently-used entries
            // instead of clearing everything (avoids CPU spikes and icon flicker).
            if (_iconCache.Count >= MaxCacheSize)
                EvictOldest();

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

            _iconCache[filePath] = new CacheEntry
            {
                Icon = result,
                LastAccess = Interlocked.Increment(ref _accessCounter)
            };
            return result;
        }
        catch
        {
            return null;
        }
    }

    // Removes roughly the oldest 25% of entries by last-access time.
    private static void EvictOldest()
    {
        lock (_evictLock)
        {
            // Another thread may have already evicted while we waited on the lock.
            if (_iconCache.Count < MaxCacheSize) return;

            int removeCount = Math.Max(1, MaxCacheSize / 4);
            var oldest = _iconCache
                .OrderBy(kvp => kvp.Value.LastAccess)
                .Take(removeCount)
                .Select(kvp => kvp.Key)
                .ToArray();

            foreach (var key in oldest)
                _iconCache.TryRemove(key, out _);
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
