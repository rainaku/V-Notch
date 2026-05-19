using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
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

            // Detect and trim letterbox (black bars top/bottom or left/right)
            var contentRect = DetectContentBounds(source, width, height);
            BitmapSource workingSource = source;

            if (contentRect.Width < width * 0.95 || contentRect.Height < height * 0.95)
            {
                // Significant letterbox detected — crop it out first
                var trimmed = new CroppedBitmap(source, contentRect);
                trimmed.Freeze();
                workingSource = trimmed;
                width = trimmed.PixelWidth;
                height = trimmed.PixelHeight;
            }

            double aspect = (double)width / height;

            double zoom = 0.97;
            int squareSize = (int)(Math.Min(width, height) * zoom);

            Int32Rect rect;

            // Try smart crop first if enabled and available
            if (EnableSmartCrop && _smartCropAvailable && aspect > 1.4)
            {
                BitmapImage? workingBitmap = workingSource as BitmapImage;
                if (workingBitmap == null)
                {
                    // Convert CroppedBitmap to BitmapImage for ONNX
                    workingBitmap = ConvertToBitmapImage(workingSource);
                }

                if (workingBitmap != null)
                {
                    var smartRect = _smartCrop.GetSmartCropRect(workingBitmap, squareSize);
                    if (smartRect.HasValue)
                        rect = smartRect.Value;
                    else
                        rect = GetFallbackCropRect(width, height, squareSize, mediaSource, aspect);
                }
                else
                {
                    rect = GetFallbackCropRect(width, height, squareSize, mediaSource, aspect);
                }
            }
            else
            {
                rect = GetFallbackCropRect(width, height, squareSize, mediaSource, aspect);
            }

            // Fast path: CroppedBitmap → direct pixel copy → BitmapImage
            var cropped = new CroppedBitmap(workingSource, rect);
            cropped.Freeze();

            int cropW = cropped.PixelWidth;
            int cropH = cropped.PixelHeight;
            int stride = cropW * 4;

            BitmapSource cropSource = cropped;
            if (cropped.Format != System.Windows.Media.PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap(cropped, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                cropSource = converted;
            }

            // Direct WriteableBitmap — skip BMP encode/decode entirely
            var wb = new WriteableBitmap(cropW, cropH, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
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

            // Convert WriteableBitmap to BitmapImage via PNG in-memory (fast, lossless)
            using var ms = new MemoryStream();
            var encoder = new PngBitmapEncoder();
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

    private static Int32Rect DetectContentBounds(BitmapSource source, int width, int height)
    {
        // Detect black letterbox bars by scanning rows/columns for near-black pixels
        const int sampleSize = 64;
        double scaleX = (double)sampleSize / width;
        double scaleY = (double)sampleSize / height;
        var small = new TransformedBitmap(source, new ScaleTransform(scaleX, scaleY));
        small.Freeze();

        int sw = small.PixelWidth;
        int sh = small.PixelHeight;
        if (sw < 4 || sh < 4) return new Int32Rect(0, 0, width, height);

        int stride = sw * 4;
        byte[] pixels = new byte[sh * stride];

        BitmapSource src = small;
        if (small.Format != System.Windows.Media.PixelFormats.Bgra32)
        {
            var conv = new FormatConvertedBitmap(small, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            conv.Freeze();
            src = conv;
        }
        src.CopyPixels(pixels, stride, 0);

        const int blackThreshold = 25; // RGB values below this = "black"

        // Scan from top
        int topBar = 0;
        for (int y = 0; y < sh / 3; y++)
        {
            int darkPixels = 0, total = 0;
            for (int x = sw / 4; x < sw * 3 / 4; x++) // sample center 50%
            {
                int i = y * stride + x * 4;
                if (pixels[i] < blackThreshold && pixels[i + 1] < blackThreshold && pixels[i + 2] < blackThreshold)
                    darkPixels++;
                total++;
            }
            if (total > 0 && (double)darkPixels / total > 0.85)
                topBar = y + 1;
            else
                break;
        }

        // Scan from bottom
        int bottomBar = 0;
        for (int y = sh - 1; y >= sh * 2 / 3; y--)
        {
            int darkPixels = 0, total = 0;
            for (int x = sw / 4; x < sw * 3 / 4; x++)
            {
                int i = y * stride + x * 4;
                if (pixels[i] < blackThreshold && pixels[i + 1] < blackThreshold && pixels[i + 2] < blackThreshold)
                    darkPixels++;
                total++;
            }
            if (total > 0 && (double)darkPixels / total > 0.85)
                bottomBar++;
            else
                break;
        }

        // Scan from left
        int leftBar = 0;
        for (int x = 0; x < sw / 3; x++)
        {
            int darkPixels = 0, total = 0;
            for (int y = sh / 4; y < sh * 3 / 4; y++)
            {
                int i = y * stride + x * 4;
                if (pixels[i] < blackThreshold && pixels[i + 1] < blackThreshold && pixels[i + 2] < blackThreshold)
                    darkPixels++;
                total++;
            }
            if (total > 0 && (double)darkPixels / total > 0.85)
                leftBar = x + 1;
            else
                break;
        }

        // Scan from right
        int rightBar = 0;
        for (int x = sw - 1; x >= sw * 2 / 3; x--)
        {
            int darkPixels = 0, total = 0;
            for (int y = sh / 4; y < sh * 3 / 4; y++)
            {
                int i = y * stride + x * 4;
                if (pixels[i] < blackThreshold && pixels[i + 1] < blackThreshold && pixels[i + 2] < blackThreshold)
                    darkPixels++;
                total++;
            }
            if (total > 0 && (double)darkPixels / total > 0.85)
                rightBar++;
            else
                break;
        }

        // Convert back to original image coordinates
        int contentX = (int)(leftBar / scaleX);
        int contentY = (int)(topBar / scaleY);
        int contentW = width - contentX - (int)(rightBar / scaleX);
        int contentH = height - contentY - (int)(bottomBar / scaleY);

        // Sanity check
        if (contentW < width / 2 || contentH < height / 2)
            return new Int32Rect(0, 0, width, height);

        return new Int32Rect(contentX, contentY, contentW, contentH);
    }

    private static BitmapImage? ConvertToBitmapImage(BitmapSource source)
    {
        try
        {
            using var ms = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
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
}
