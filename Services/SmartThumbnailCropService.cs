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
public sealed class SmartThumbnailCropService : IDisposable
{
    private readonly object _lock = new();

    // Use 640 for model (YOLOv8n is trained at 640)
    private const int ModelInputSize = 640;
    private const float ConfidenceThreshold = 0.40f;
    private const float NmsThreshold = 0.45f;

    private bool _disposed;
    private bool _modelExists;
    private bool _modelExistsChecked;

    // COCO classes that are typically "main subjects" in thumbnails
    private static readonly HashSet<int> _priorityClasses = new()
    {
        0,  // person
    };
    public bool TryInitialize()
    {
        if (_disposed) return false;
        if (_modelExistsChecked) return _modelExists;

        _modelExists = File.Exists(GetModelPath());
        _modelExistsChecked = true;

        if (!_modelExists)
            System.Diagnostics.Debug.WriteLine($"[SmartCrop] Model not found at: {GetModelPath()}");

        return _modelExists;
    }
    public bool IsLoaded => _modelExists;
    public void Unload() { }
    public Int32Rect? GetSmartCropRect(BitmapImage source, int targetSquareSize)
    {
        if (_disposed) return null;
        if (!_modelExists && !TryInitialize()) return null;
        if (!_modelExists) return null;

        lock (_lock)
        {
            InferenceSession? session = null;
            float[]? tensorBuffer = null;

            try
            {
                int imgWidth = source.PixelWidth;
                int imgHeight = source.PixelHeight;

                // Skip if image is already square or very small
                if (Math.Abs(imgWidth - imgHeight) < 10 || imgWidth < 64 || imgHeight < 64)
                    return null;

                // ─── Load model ───
                var options = new SessionOptions();
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                options.InterOpNumThreads = 1;
                options.IntraOpNumThreads = 2;
                options.EnableMemoryPattern = true;

                session = new InferenceSession(GetModelPath(), options);
                tensorBuffer = new float[1 * 3 * ModelInputSize * ModelInputSize];

                System.Diagnostics.Debug.WriteLine("[SmartCrop] Model loaded for inference.");

                // ─── Preprocess ───
                var (scaleX, scaleY, padX, padY) = PreprocessImageFast(source, tensorBuffer);

                // ─── Run inference ───
                var tensor = new DenseTensor<float>(tensorBuffer, new[] { 1, 3, ModelInputSize, ModelInputSize });
                var inputName = session.InputNames[0];
                var inputs = new List<NamedOnnxValue>(1)
                {
                    NamedOnnxValue.CreateFromTensor(inputName, tensor)
                };

                using var results = session.Run(inputs);
                var output = results.First().AsTensor<float>();

                // ─── Parse detections ───
                var detections = ParseYolov8Output(output, imgWidth, imgHeight, scaleX, padX, padY);

                if (detections.Count == 0)
                {
                    // No YOLO detections — use saliency-based crop
                    return GetSaliencyCropRect(source, imgWidth, imgHeight, targetSquareSize);
                }

                // Hybrid crop logic
                return GetHybridCropRect(detections, source, imgWidth, imgHeight, targetSquareSize);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SmartCrop] Inference failed: {ex.Message}");
                return null;
            }
            finally
            {
                // ─── Immediately unload ───
                session?.Dispose();
                tensorBuffer = null;
                System.Diagnostics.Debug.WriteLine("[SmartCrop] Model unloaded after inference.");
            }
        }
    }

    private (float scale, float scaleY, float padX, float padY) PreprocessImageFast(BitmapImage source, float[] tensorBuffer)
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
            int planeSize = ModelInputSize * ModelInputSize;
            const float grayVal = 114f / 255f;
            const float inv255 = 1f / 255f;

            // Fill entire buffer with gray first (fast span fill)
            tensorBuffer.AsSpan().Fill(grayVal);

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
                    tensorBuffer[tensorIdx] = pixels[pixelIdx + 2] * inv255;                  // R plane
                    tensorBuffer[planeSize + tensorIdx] = pixels[pixelIdx + 1] * inv255;      // G plane
                    tensorBuffer[2 * planeSize + tensorIdx] = pixels[pixelIdx] * inv255;      // B plane
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

    /// <summary>
    /// Hybrid crop: combines ONNX detections with heuristic rules.
    /// - Single person → crop centered on person (with center bias)
    /// - Multiple persons → crop center encompassing all
    /// - Non-person detections → use as secondary hint
    /// </summary>
    private Int32Rect GetHybridCropRect(List<Detection> detections, BitmapImage source, int imgWidth, int imgHeight, int targetSize)
    {
        float imgArea = imgWidth * imgHeight;

        // Separate persons from other detections
        var persons = new List<Detection>();
        Detection? largestNonPerson = null;
        float largestNonPersonArea = 0;

        foreach (var d in detections)
        {
            float areaRatio = d.Area / imgArea;
            if (areaRatio < 0.02f) continue; // Skip tiny detections

            if (_priorityClasses.Contains(d.ClassId))
            {
                persons.Add(d);
            }
            else if (d.Area > largestNonPersonArea && areaRatio > 0.05f)
            {
                largestNonPersonArea = d.Area;
                largestNonPerson = d;
            }
        }

        int maxCropSize = Math.Min(imgWidth, imgHeight);
        int cropSize = Math.Min(targetSize, maxCropSize);
        float imgCenterX = imgWidth / 2f;

        if (persons.Count == 1)
        {
            // Single person → crop directly centered on person
            var p = persons[0];
            float personCenterX = (p.X1 + p.X2) / 2f;
            return BuildCropRect(personCenterX, imgWidth, imgHeight, cropSize);
        }
        else if (persons.Count >= 2)
        {
            // Multiple persons → center on the group
            float minX = float.MaxValue, maxX = float.MinValue;
            foreach (var p in persons)
            {
                if (p.X1 < minX) minX = p.X1;
                if (p.X2 > maxX) maxX = p.X2;
            }
            float groupCenterX = (minX + maxX) / 2f;
            return BuildCropRect(groupCenterX, imgWidth, imgHeight, cropSize);
        }
        else if (largestNonPerson != null)
        {
            // No persons but have other significant detection
            float objCenterX = (largestNonPerson.Value.X1 + largestNonPerson.Value.X2) / 2f;
            return BuildCropRect(objCenterX, imgWidth, imgHeight, cropSize);
        }

        // Fallback: saliency-based
        return GetSaliencyCropRect(source, imgWidth, imgHeight, targetSize)
               ?? BuildCropRect(imgCenterX, imgWidth, imgHeight, cropSize);
    }

    /// <summary>
    /// Builds a crop rect centered on a given X position, with Y centered in image.
    /// </summary>
    private static Int32Rect BuildCropRect(float centerX, int imgWidth, int imgHeight, int cropSize)
    {
        int cropX = (int)(centerX - cropSize / 2f);
        int cropY = (imgHeight - cropSize) / 2;

        // Clamp to image bounds
        cropX = Math.Max(0, Math.Min(cropX, imgWidth - cropSize));
        cropY = Math.Max(0, Math.Min(cropY, imgHeight - cropSize));

        return new Int32Rect(cropX, cropY, cropSize, cropSize);
    }

    /// <summary>
    /// Saliency-based crop: analyzes pixel contrast and brightness to find the most
    /// visually interesting region. Also detects text-heavy areas (high edge density).
    /// Used when YOLO detects nothing (common for graphic/text-heavy thumbnails).
    /// </summary>
    private Int32Rect? GetSaliencyCropRect(BitmapImage source, int imgWidth, int imgHeight, int targetSize)
    {
        try
        {
            int maxCropSize = Math.Min(imgWidth, imgHeight);
            int cropSize = Math.Min(targetSize, maxCropSize);

            // Downsample for fast analysis
            const int analysisSize = 64;
            double scaleX = (double)analysisSize / imgWidth;
            double scaleY = (double)analysisSize / imgHeight;
            var scaled = new TransformedBitmap(source, new ScaleTransform(scaleX, scaleY));
            scaled.Freeze();

            int w = scaled.PixelWidth;
            int h = scaled.PixelHeight;
            if (w < 4 || h < 4) return null;

            int stride = w * 4;
            byte[] pixels = new byte[h * stride];

            BitmapSource scaledSource = scaled;
            if (scaled.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                scaledSource = converted;
            }
            scaledSource.CopyPixels(pixels, stride, 0);

            // Divide image into vertical strips and score each
            const int numStrips = 8;
            int stripWidth = w / numStrips;
            double[] stripScores = new double[numStrips];

            for (int strip = 0; strip < numStrips; strip++)
            {
                int startX = strip * stripWidth;
                int endX = Math.Min(startX + stripWidth, w);
                double totalContrast = 0;
                double totalBrightness = 0;
                double edgeDensity = 0;
                int pixelCount = 0;

                for (int y = 1; y < h - 1; y++)
                {
                    for (int x = startX; x < endX && x < w - 1; x++)
                    {
                        int i = (y * stride) + (x * 4);
                        int iRight = i + 4;
                        int iBelow = ((y + 1) * stride) + (x * 4);

                        // Current pixel luminance
                        double lum = 0.299 * pixels[i + 2] + 0.587 * pixels[i + 1] + 0.114 * pixels[i];

                        // Horizontal gradient (edge detection)
                        double lumRight = 0.299 * pixels[iRight + 2] + 0.587 * pixels[iRight + 1] + 0.114 * pixels[iRight];
                        double lumBelow = 0.299 * pixels[iBelow + 2] + 0.587 * pixels[iBelow + 1] + 0.114 * pixels[iBelow];

                        double gradH = Math.Abs(lum - lumRight);
                        double gradV = Math.Abs(lum - lumBelow);
                        double gradient = gradH + gradV;

                        // High gradient = edges/text
                        edgeDensity += gradient > 30 ? 1 : 0;
                        totalContrast += gradient;
                        totalBrightness += lum;
                        pixelCount++;
                    }
                }

                if (pixelCount == 0) continue;

                double avgContrast = totalContrast / pixelCount;
                double avgBrightness = totalBrightness / pixelCount;
                double edgeRatio = edgeDensity / pixelCount;

                // Score: prefer high contrast + moderate brightness + high edge density (text)
                // Penalize very dark strips (likely black bars) and very bright (likely white bg)
                double brightnessPenalty = (avgBrightness < 20 || avgBrightness > 240) ? 0.3 : 1.0;
                double score = (avgContrast * 0.4 + edgeRatio * 80.0 * 0.6) * brightnessPenalty;

                // Slight center bias — thumbnails usually have main content in center
                double centerBias = 1.0 - Math.Abs((strip + 0.5) / numStrips - 0.5) * 0.3;
                stripScores[strip] = score * centerBias;
            }

            // Find the best strip region (allow 2 adjacent strips for wider coverage)
            double bestScore = -1;
            int bestStripCenter = numStrips / 2;

            for (int i = 0; i < numStrips - 1; i++)
            {
                double pairScore = stripScores[i] + stripScores[i + 1];
                if (pairScore > bestScore)
                {
                    bestScore = pairScore;
                    bestStripCenter = i;
                }
            }

            // Convert strip position back to image coordinates
            float saliencyCenterX = (bestStripCenter + 1.0f) / numStrips * imgWidth;

            return BuildCropRect(saliencyCenterX, imgWidth, imgHeight, cropSize);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SmartCrop] Saliency analysis failed: {ex.Message}");
            return null;
        }
    }

    private static string GetModelPath()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(appDir, "Models", "yolov8n.onnx");
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private struct Detection
    {
        public float X1, Y1, X2, Y2;
        public float Confidence;
        public int ClassId;
        public float Area => (X2 - X1) * (Y2 - Y1);
    }
}
