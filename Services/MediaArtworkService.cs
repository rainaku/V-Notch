using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;

namespace VNotch.Services;

public interface IMediaArtworkService
{
    Task<BitmapImage?> DownloadImageAsync(string url, CancellationToken ct = default);
    BitmapImage? CropToSquare(BitmapImage source, string mediaSource);
    Task<BitmapImage?> ConvertToWpfBitmapAsync(IRandomAccessStreamWithContentType stream, CancellationToken ct = default);
    void ConfigureSmartCrop(bool enabled);
}

public sealed class MediaArtworkService : IMediaArtworkService
{
    private static readonly HttpClient _httpClient = new();
    private readonly SmartThumbnailCropService _smartCrop;
    private bool _smartCropAvailable;

    public MediaArtworkService()
    {
        _smartCrop = new SmartThumbnailCropService();
        _smartCropAvailable = false;
    }
    public bool EnableSmartCrop { get; set; } = false;
    public void InitializeSmartCrop()
    {
        if (!_smartCropAvailable)
        {
            _smartCropAvailable = _smartCrop.TryInitialize();
        }
    }
    public void ConfigureSmartCrop(bool enabled)
    {
        EnableSmartCrop = enabled;
        if (enabled && !_smartCropAvailable)
        {
            // Just check if model file exists — no model loading here
            InitializeSmartCrop();
        }
    }

    static MediaArtworkService()
    {
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
    }

    public async Task<BitmapImage?> DownloadImageAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(1800));
            var bytes = await _httpClient.GetByteArrayAsync(url, timeoutCts.Token);

            BitmapImage? bitmap = null;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = new MemoryStream(bytes);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                });
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public BitmapImage? CropToSquare(BitmapImage source, string mediaSource)
    {
        try
        {
            int width = source.PixelWidth;
            int height = source.PixelHeight;

            double aspect = (double)width / height;

            // No zoom — use full height as crop size for all sources
            double zoom = 0.97;
            int squareSize = (int)(Math.Min(width, height) * zoom);

            Int32Rect rect;

            // Try smart crop first if enabled and available
            if (EnableSmartCrop && _smartCropAvailable && aspect > 1.4)
            {
                var smartRect = _smartCrop.GetSmartCropRect(source, squareSize);
                if (smartRect.HasValue)
                {
                    rect = smartRect.Value;
                }
                else
                {
                    // Fall back to heuristic crop
                    rect = GetFallbackCropRect(width, height, squareSize, mediaSource, aspect);
                }
            }
            else
            {
                rect = GetFallbackCropRect(width, height, squareSize, mediaSource, aspect);
            }

            // Fast crop: CroppedBitmap → WriteableBitmap (no encode/decode round-trip)
            var cropped = new CroppedBitmap(source, rect);
            cropped.Freeze();

            int cropW = cropped.PixelWidth;
            int cropH = cropped.PixelHeight;
            int stride = cropW * 4;

            // Ensure Bgra32 format for consistent pixel copy
            BitmapSource cropSource = cropped;
            if (cropped.Format != System.Windows.Media.PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap(cropped, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                cropSource = converted;
            }

            // Create a WriteableBitmap directly — avoids BMP encode/decode overhead
            var wb = new System.Windows.Media.Imaging.WriteableBitmap(cropW, cropH, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            wb.Lock();
            try
            {
                cropSource.CopyPixels(new Int32Rect(0, 0, cropW, cropH), wb.BackBuffer, wb.BackBufferStride * cropH, wb.BackBufferStride);
                wb.AddDirtyRect(new Int32Rect(0, 0, cropW, cropH));
            }
            finally
            {
                wb.Unlock();
            }
            wb.Freeze();

            // Wrap in BitmapImage via BMP encoding (fastest — no compression overhead)
            using var ms = new MemoryStream();
            var encoder = new BmpBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(wb));
            encoder.Save(ms);
            ms.Position = 0;

            var bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.StreamSource = ms;
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.EndInit();
            bitmapImage.Freeze();
            return bitmapImage;
        }
        catch
        {
            return null;
        }
    }

    private static Int32Rect GetFallbackCropRect(int width, int height, int squareSize, string mediaSource, double aspect)
    {
        // YouTube thumbs are usually 16:9; crop from the left side to show the main content.
        // Only apply left-crop for clearly wide images (aspect > 1.4).
        // Near-square images (album art, Topic channels) always center crop.
        bool isWideYouTube = string.Equals(mediaSource, "YouTube", StringComparison.OrdinalIgnoreCase) && aspect > 1.4;
        int offsetX = isWideYouTube ? 0 : (width - squareSize) / 2;
        int offsetY = (height - squareSize) / 2;
        return new Int32Rect(offsetX, offsetY, squareSize, squareSize);
    }

    public async Task<BitmapImage?> ConvertToWpfBitmapAsync(IRandomAccessStreamWithContentType stream, CancellationToken ct = default)
    {
        try
        {
            using var memoryStream = new MemoryStream();
            using (var reader = new DataReader(stream))
            {
                await reader.LoadAsync((uint)stream.Size).AsTask(ct);
                var bytes = new byte[stream.Size];
                reader.ReadBytes(bytes);
                memoryStream.Write(bytes, 0, bytes.Length);
            }

            memoryStream.Position = 0;

            BitmapImage? bitmap = null;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return null;
            }

            await dispatcher.InvokeAsync(() =>
            {
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = memoryStream;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
            });

            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
