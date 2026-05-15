using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;

namespace VNotch.Services;

public interface IMediaArtworkService
{
    Task<BitmapImage?> DownloadImageAsync(string url, CancellationToken ct = default);
    BitmapImage? CropToSquare(BitmapImage source, string mediaSource);
    Task<BitmapImage?> ConvertToWpfBitmapAsync(IRandomAccessStreamWithContentType stream, CancellationToken ct = default);
}

public sealed class MediaArtworkService : IMediaArtworkService
{
    private static readonly HttpClient _httpClient = new();

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
            
            // YouTube thumbs are usually 16:9; crop from the left side to show the main content.
            // Other sources crop from center.
            bool isYouTube = string.Equals(mediaSource, "YouTube", StringComparison.OrdinalIgnoreCase) && aspect > 1.25;
            int offsetX = isYouTube ? 0 : (width - squareSize) / 2;
            int offsetY = (height - squareSize) / 2;

            var rect = new System.Windows.Int32Rect(offsetX, offsetY, squareSize, squareSize);
            var cropped = new CroppedBitmap(source, rect);

            using var ms = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            encoder.Save(ms);
            ms.Position = 0;

            var result = new BitmapImage();
            result.BeginInit();
            result.StreamSource = ms;
            result.CacheOption = BitmapCacheOption.OnLoad;
            result.EndInit();
            result.Freeze();
            return result;
        }
        catch
        {
            return null;
        }
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
