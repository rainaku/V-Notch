using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace VNotch.Services;

/// <summary>
/// Obtains a thumbnail / icon for an arbitrary file path. Uses the Shell
/// <c>IShellItemImageFactory</c> API first (high-quality thumbnails for
/// images / videos / documents), then falls back to
/// <see cref="System.Drawing.Icon.ExtractAssociatedIcon"/>.
///
/// Extracted from <c>MainWindow.Secondary.cs</c>. The <see cref="MainWindow"/>
/// used to carry these native P/Invokes and COM interop directly; now it just
/// calls <see cref="GetFileIcon"/>.
/// </summary>
internal static class FileIconProvider
{
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

    /// <summary>
    /// Returns a frozen <see cref="ImageSource"/> suitable for binding to a UI
    /// element, or <c>null</c> if no icon could be obtained.
    /// </summary>
    public static ImageSource? GetFileIcon(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            bool isImage = ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif";
            bool isVideo = ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm";

            if (isImage || isVideo)
            {
                if (isVideo)
                {
                    var videoThumb = TryGetVideoThumbnail(filePath);
                    if (videoThumb != null) return videoThumb;
                }

                var shellThumb = TryGetShellThumbnail(filePath, 128);
                if (shellThumb != null) return shellThumb;

                if (isImage)
                {
                    var directImage = TryGetDirectImage(filePath);
                    if (directImage != null) return directImage;
                }
            }

            return TryGetAssociatedIcon(filePath);
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
