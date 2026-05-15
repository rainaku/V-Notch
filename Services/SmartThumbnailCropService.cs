using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VNotch.Services;

/// <summary>
/// Offline smart thumbnail cropping using YOLOv8n ONNX model.
/// Detects the main subject/object in a thumbnail and crops around it
/// for better visual framing in the music widget.
/// 
/// Optimized for speed:
/// - Reduced input size (320x320) for thumbnail-sized images
/// - Pooled buffers to avoid GC pressure
/// - Direct pixel copy without intermediate encoding
/// - Pre-allocated tensor with gray fill optimization
/// </summary>
public sealed class SmartThumbnailCropService : IDisposable
{
    private InferenceSession? _session;
    private bool _isModelLoaded;
    private readonly object _lock = new();

    // Use 640 for model (YOLOv8n is trained at 640) but downscale input first
    private const int ModelInputSize = 640;
    private const float ConfidenceThreshold = 0.40f;
    private const float NmsThreshold = 0.45f;

    // Pre-allocated reusable buffer for tensor data (avoids GC pressure)
    private float[]? _tensorBuffer;
    private readonly object _inferLock = new();

    // COCO classes that are typically "main subjects" in thumbnails
    private static readonly HashSet<int> _priorityClasses = new()
    {
        0,  // person
    };

    /// <summary>
    /// Attempts to load the YOLOv8n ONNX model from the application directory.
    /// Returns false if model file is not found (graceful degradation).
    /// </summary>
    public bool TryInitialize()
    {
        if (_isModelLoaded) return true;

        lock (_lock)
        {
            if (_isModelLoaded) return true;

            try
            {
                string modelPath = GetModelPath();
                if (!File.Exists(modelPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[SmartCrop] Model not found at: {modelPath}");
                    return false;
                }

                var options = new SessionOptions();
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                options.InterOpNumThreads = 1;
                options.IntraOpNumThreads = 2;
                // Enable memory pattern optimization
                options.EnableMemoryPattern = true;

                _session = new InferenceSession(modelPath, options);
                _isModelLoaded = true;

                // Pre-allocate tensor buffer (1 * 3 * 640 * 640 = 1,228,800 floats)
                _tensorBuffer = new float[1 * 3 * ModelInputSize * ModelInputSize];

                System.Diagnostics.Debug.WriteLine("[SmartCrop] YOLOv8n model loaded successfully.");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SmartCrop] Failed to load model: {ex.Message}");
                _isModelLoaded = false;
                return false;
            }
        }
    }

    /// <summary>
    /// Detects the main subject in the image and returns a smart crop rectangle.
    /// Returns null if no significant subject is detected (caller should fall back to center crop).
    /// Thread-safe (serialized inference).
    /// </summary>
    public Int32Rect? GetSmartCropRect(BitmapImage source, int targetSquareSize)
    {
        if (!_isModelLoaded || _session == null || _tensorBuffer == null)
            return null;

        // Serialize inference calls (ONNX session is thread-safe but we reuse the buffer)
        lock (_inferLock)
        {
            try
            {
                int imgWidth = source.PixelWidth;
                int imgHeight = source.PixelHeight;

                // Skip if image is already square or very small
                if (Math.Abs(imgWidth - imgHeight) < 10 || imgWidth < 64 || imgHeight < 64)
                    return null;

                // Preprocess: resize and fill tensor buffer directly
                var (scaleX, scaleY, padX, padY) = PreprocessImageFast(source);

                // Run inference with pre-allocated buffer
                var tensor = new DenseTensor<float>(_tensorBuffer, new[] { 1, 3, ModelInputSize, ModelInputSize });
                var inputName = _session.InputNames[0];
                var inputs = new List<NamedOnnxValue>(1)
                {
                    NamedOnnxValue.CreateFromTensor(inputName, tensor)
                };

                using var results = _session.Run(inputs);
                var output = results.First().AsTensor<float>();

                // Parse detections
                var detections = ParseYolov8Output(output, imgWidth, imgHeight, scaleX, padX, padY);

                if (detections.Count == 0)
                    return null;

                // Find the best detection
                var bestDetection = SelectBestDetection(detections, imgWidth, imgHeight);
                if (bestDetection == null)
                    return null;

                return CalculateCropRect(bestDetection.Value, imgWidth, imgHeight, targetSquareSize);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SmartCrop] Inference failed: {ex.Message}");
                return null;
            }
        }
    }

    private (float scale, float scaleY, float padX, float padY) PreprocessImageFast(BitmapImage source)
    {
        int imgWidth = source.PixelWidth;
        int imgHeight = source.PixelHeight;

        // Calculate letterbox scaling
        float scale = Math.Min((float)ModelInputSize / imgWidth, (float)ModelInputSize / imgHeight);
        int newWidth = (int)(imgWidth * scale);
        int newHeight = (int)(imgHeight * scale);
        float padX = (ModelInputSize - newWidth) / 2f;
        float padY = (ModelInputSize - newHeight) / 2f;
        int padXi = (int)padX;
        int padYi = (int)padY;

        // Fast: scale source down using WPF (hardware-accelerated)
        var scaled = new TransformedBitmap(source, new ScaleTransform(
            (double)newWidth / imgWidth,
            (double)newHeight / imgHeight));
        scaled.Freeze();

        int scaledW = scaled.PixelWidth;
        int scaledH = scaled.PixelHeight;
        int stride = scaledW * 4; // Bgra32

        // Rent a buffer from the pool to avoid allocation
        byte[] pixels = ArrayPool<byte>.Shared.Rent(scaledH * stride);
        try
        {
            // Convert to Bgra32 if needed and copy pixels
            if (scaled.Format != PixelFormats.Bgra32)
            {
                var formatted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                formatted.Freeze();
                formatted.CopyPixels(pixels, stride, 0);
            }
            else
            {
                scaled.CopyPixels(pixels, stride, 0);
            }

            // Fill tensor buffer: gray padding + image data in one pass
            var buf = _tensorBuffer!;
            int planeSize = ModelInputSize * ModelInputSize;
            const float grayVal = 114f / 255f;
            const float inv255 = 1f / 255f;

            // Fill entire buffer with gray first (fast span fill)
            buf.AsSpan().Fill(grayVal);

            // Copy image pixels into the correct positions (R, G, B planes)
            for (int y = 0; y < scaledH && (y + padYi) < ModelInputSize; y++)
            {
                int tensorY = y + padYi;
                int rowOffset = y * stride;
                int tensorRowBase = tensorY * ModelInputSize;

                for (int x = 0; x < scaledW && (x + padXi) < ModelInputSize; x++)
                {
                    int pixelIdx = rowOffset + x * 4;
                    int tensorIdx = tensorRowBase + (x + padXi);

                    // BGRA -> RGB planes
                    buf[tensorIdx] = pixels[pixelIdx + 2] * inv255;                  // R plane
                    buf[planeSize + tensorIdx] = pixels[pixelIdx + 1] * inv255;      // G plane
                    buf[2 * planeSize + tensorIdx] = pixels[pixelIdx] * inv255;      // B plane
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }

        return (scale, scale, padX, padY);
    }

    private List<Detection> ParseYolov8Output(Tensor<float> output, int imgWidth, int imgHeight, float scale, float padX, float padY)
    {
        // YOLOv8 output shape: [1, 84, 8400]
        // 84 = 4 (bbox) + 80 (class scores)
        // 8400 = number of predictions

        var detections = new List<Detection>(16); // Pre-size for typical count
        var dims = output.Dimensions;

        int numClasses = dims[1] - 4;
        int numPredictions = dims[2];

        for (int i = 0; i < numPredictions; i++)
        {
            // Find max class score — early exit optimization
            float maxScore = ConfidenceThreshold;
            int maxClassId = -1;

            for (int c = 4; c < dims[1]; c++)
            {
                float score = output[0, c, i];
                if (score > maxScore)
                {
                    maxScore = score;
                    maxClassId = c - 4;
                }
            }

            if (maxClassId < 0)
                continue;

            // Get bbox (center_x, center_y, width, height) in model coordinates
            float cx = output[0, 0, i];
            float cy = output[0, 1, i];
            float w = output[0, 2, i];
            float h = output[0, 3, i];

            // Convert from model space to original image space
            float x1 = (cx - w / 2f - padX) / scale;
            float y1 = (cy - h / 2f - padY) / scale;
            float x2 = (cx + w / 2f - padX) / scale;
            float y2 = (cy + h / 2f - padY) / scale;

            // Clamp to image bounds
            x1 = Math.Clamp(x1, 0, imgWidth);
            y1 = Math.Clamp(y1, 0, imgHeight);
            x2 = Math.Clamp(x2, 0, imgWidth);
            y2 = Math.Clamp(y2, 0, imgHeight);

            if (x2 - x1 < 5 || y2 - y1 < 5)
                continue;

            detections.Add(new Detection
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Confidence = maxScore,
                ClassId = maxClassId
            });
        }

        // Apply NMS only if we have multiple detections
        return detections.Count > 1 ? ApplyNms(detections) : detections;
    }

    private List<Detection> ApplyNms(List<Detection> detections)
    {
        var sorted = detections.OrderByDescending(d => d.Confidence).ToList();
        var result = new List<Detection>(sorted.Count);

        Span<bool> suppressed = stackalloc bool[sorted.Count];
        for (int i = 0; i < sorted.Count; i++)
        {
            if (suppressed[i]) continue;
            result.Add(sorted[i]);

            for (int j = i + 1; j < sorted.Count; j++)
            {
                if (suppressed[j]) continue;
                if (IoU(sorted[i], sorted[j]) > NmsThreshold)
                    suppressed[j] = true;
            }
        }

        return result;
    }

    private static float IoU(Detection a, Detection b)
    {
        float interX1 = Math.Max(a.X1, b.X1);
        float interY1 = Math.Max(a.Y1, b.Y1);
        float interX2 = Math.Min(a.X2, b.X2);
        float interY2 = Math.Min(a.Y2, b.Y2);

        float interArea = Math.Max(0, interX2 - interX1) * Math.Max(0, interY2 - interY1);
        float areaA = (a.X2 - a.X1) * (a.Y2 - a.Y1);
        float areaB = (b.X2 - b.X1) * (b.Y2 - b.Y1);

        return interArea / (areaA + areaB - interArea + 1e-6f);
    }

    private Detection? SelectBestDetection(List<Detection> detections, int imgWidth, int imgHeight)
    {
        float imgArea = imgWidth * imgHeight;

        // Prefer priority classes (person) with decent size
        Detection? bestPriority = null;
        float bestPriorityScore = 0;

        Detection? bestLargest = null;
        float bestLargestArea = 0;

        foreach (var d in detections)
        {
            float areaRatio = d.Area / imgArea;

            if (_priorityClasses.Contains(d.ClassId) && areaRatio > 0.02f)
            {
                float score = d.Confidence * areaRatio;
                if (score > bestPriorityScore)
                {
                    bestPriorityScore = score;
                    bestPriority = d;
                }
            }

            if (areaRatio > 0.05f && d.Area > bestLargestArea)
            {
                bestLargestArea = d.Area;
                bestLargest = d;
            }
        }

        return bestPriority ?? bestLargest;
    }

    private Int32Rect CalculateCropRect(Detection detection, int imgWidth, int imgHeight, int targetSize)
    {
        // Center of the detected object
        float centerX = (detection.X1 + detection.X2) / 2f;
        float centerY = (detection.Y1 + detection.Y2) / 2f;

        // Crop size: use the same size as the fallback system (based on image height for 16:9)
        // This ensures no black borders — we never crop larger than the shortest dimension.
        int maxCropSize = Math.Min(imgWidth, imgHeight);
        int cropSize = Math.Min(targetSize, maxCropSize);

        // Position crop centered on the detected subject's X position
        // but keep Y centered in the image (avoids grabbing letterbox black bars)
        int cropX = (int)(centerX - cropSize / 2f);
        int cropY = (imgHeight - cropSize) / 2;

        // Clamp to image bounds
        cropX = Math.Max(0, Math.Min(cropX, imgWidth - cropSize));
        cropY = Math.Max(0, Math.Min(cropY, imgHeight - cropSize));

        return new Int32Rect(cropX, cropY, cropSize, cropSize);
    }

    private static string GetModelPath()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(appDir, "Models", "yolov8n.onnx");
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _tensorBuffer = null;
        _isModelLoaded = false;
    }

    private struct Detection
    {
        public float X1, Y1, X2, Y2;
        public float Confidence;
        public int ClassId;
        public float Area => (X2 - X1) * (Y2 - Y1);
    }
}
