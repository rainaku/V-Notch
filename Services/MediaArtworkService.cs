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

    /// <summary>
    /// Configures smart crop (ONNX/YOLOv8n object detection).
    /// Call after settings are loaded.
    /// </summary>
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
        // Lazy-init model on first use to avoid startup delay
        _smartCropAvailable = false;
    }

    /// <summary>
    /// Whether smart crop (ONNX/YOLOv8n) is available and enabled.
    /// Set by the caller based on user settings.
    /// </summary>
    public bool EnableSmartCrop { get; set; } = false;

    /// <summary>
    /// Initializes the ONNX model for smart cropping. Call once at startup or on first use.
    /// Safe to call multiple times.
    /// </summary>
    public void InitializeSmartCrop()
    {
        if (!_smartCropAvailable)
        {
            _smartCropAvailable = _smartCrop.TryInitialize();
        }
    }

    /// <summary>
    /// Configures smart crop based on user settings.
    /// If enabled, lazily initializes the ONNX model on a background thread.
    /// </summary>
    public void ConfigureSmartCrop(bool enabled)
    {
        EnableSmartCrop = enabled;
        if (enabled && !_smartCropAvailable)
        {
            // Initialize on background thread to avoid blocking UI
            Task.Run(() => InitializeSmartCrop());
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
            double zoom = string.Equals(mediaSource, "YouTube", StringComparison.OrdinalIgnoreCase) && aspect > 1.25
                ? 0.74
                : 0.97;
            int squareSize = (int)(Math.Min(width, height) * zoom);

            Int32Rect rect;

            // Try smart crop first if enabled and available
            if (EnableSmartCrop && _smartCropAvailable && aspect > 1.15)
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

            // Fast crop: use WriteableBitmap + CopyPixels instead of PNG encode/decode
            var cropped = new CroppedBitmap(source, rect);
            cropped.Freeze();

            int cropW = cropped.PixelWidth;
            int cropH = cropped.PixelHeight;
            int stride = cropW * 4;
            var pixelBuffer = new byte[cropH * stride];

            // Ensure Bgra32 format for consistent pixel copy
            BitmapSource cropSource = cropped;
            if (cropped.Format != System.Windows.Media.PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap(cropped, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                cropSource = converted;
            }
            cropSource.CopyPixels(pixelBuffer, stride, 0);

            // Create frozen BitmapSource directly from pixel data (no PNG round-trip)
            var result = BitmapSource.Create(
                cropW, cropH, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null,
                pixelBuffer, stride);
            result.Freeze();

            // Wrap in BitmapImage via minimal BMP encoding (much faster than PNG)
            using var ms = new MemoryStream();
            var encoder = new BmpBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(result));
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
        // Other sources crop from center.
        bool isYouTube = string.Equals(mediaSource, "YouTube", StringComparison.OrdinalIgnoreCase) && aspect > 1.25;
        int offsetX = isYouTube ? 0 : (width - squareSize) / 2;
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
