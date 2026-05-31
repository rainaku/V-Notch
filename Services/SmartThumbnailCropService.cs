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

    // ─── Model constants ───
    private const int ModelInputSize = 640;

    // ─── Object Detection Rules ───
    private const float ConfidenceThreshold = 0.35f;       // ≥ 0.35 to catch more objects (tuned down from 0.5)
    private const float PersonConfidenceThreshold = 0.10f; // Lower for person class (priority)
    private const float NmsThreshold = 0.50f;              // IoU ≤ 0.50 to merge overlapping bboxes (relaxed)
    private const float MinAreaRatio = 0.02f;              // ≥ 2% of frame area for non-person objects (tuned down)
    private const float MinPersonAreaRatio = 0.005f;       // Lower threshold for persons

    // ─── Person/Face Detection Weights ───
    private const float FaceWeight = 2.5f;                 // Face bbox importance multiplier (boosted)
    private const float BodyWeight = 1.8f;                 // Body bbox fallback weight (boosted)
    private const float HeadRoomPadding = 0.15f;           // +15% padding above head
    private const int MinFaceSize = 20;                    // Min 20x20px to filter false positives

    // ─── Text Detection Weights (deprioritized — person/object always wins) ───
    private const float TextWeight = 0.6f;                 // Text region importance (LOW — never override person)
    private const float TextSafeZoneRatio = 0.80f;         // Crop must contain text if within 80%

    // ─── Portrait Detection ───
    private const float PortraitThreshold = 0.50f;         // Person > 50% frame = portrait mode (lowered to catch more)

    // ─── Composition Rules (R1–R12) ───
    private const float SubjectMarginRatio = 0.15f;        // R1: breathing room on each side of subject (15%)
    private const float MinCropRatio = 0.45f;              // R1: crop never smaller than 45% of min(w,h) → avoid over-zoom on tiny detections
    private const float FaceVerticalFraction = 0.38f;      // R2: face sits at 38% from top of crop (upper-third framing)
    private const float PortraitFaceFraction = 0.42f;      // R2: face fraction for tight portrait crops
    private const float ObjectVerticalFraction = 0.50f;    // R3: objects centred vertically by default
    private const float StabilizeCenterThreshold = 0.03f;  // R6: ignore focal shifts < 3% of min(w,h)
    private const float StabilizeSizeThreshold = 0.04f;    // R6: ignore crop-size changes < 4% of min(w,h)

    // R7: Rule of Thirds — power points at intersections of 1/3 lines
    private const float ThirdLineLeft = 1f / 3f;
    private const float ThirdLineRight = 2f / 3f;
    private const float ThirdLineTop = 1f / 3f;
    private const float ThirdLineBottom = 2f / 3f;
    private const float ThirdsSnapStrength = 0.25f;        // R7: how strongly to pull focal toward nearest power point (0=none, 1=full)

    // R8: Golden Ratio — φ ≈ 0.618 for natural visual harmony
    private const float GoldenRatio = 0.618f;
    private const float GoldenVerticalFraction = 0.382f;   // R8: 1 - φ = upper golden line (face placement)
    private const float GoldenHorizontalBias = 0.15f;      // R8: horizontal golden bias strength

    // R9: Aspect-Aware Framing — handle panoramic vs tall sources
    private const float PanoramicThreshold = 2.0f;         // R9: width/height > 2.0 = panoramic
    private const float TallThreshold = 0.5f;              // R9: width/height < 0.5 = tall/vertical
    private const float PanoramicCenterBias = 0.6f;        // R9: panoramic images bias crop toward horizontal center
    private const float TallVerticalBias = 0.35f;          // R9: tall images bias crop toward upper portion

    // R10: Edge Avoidance — minimum distance from crop edge to subject center
    private const float EdgeAvoidanceRatio = 0.20f;        // R10: subject center must be ≥ 20% from any crop edge
    private const float EdgeSoftClampRatio = 0.12f;        // R10: soft clamp starts at 12% from edge

    // R11: Visual Balance — counterbalance subject with negative space
    private const float BalanceWeight = 0.20f;             // R11: how much to shift crop for visual balance (subtle)
    private const float BalanceMaxShift = 0.08f;           // R11: max shift as fraction of crop size

    // R12: Headroom & Lookroom — directional awareness for persons
    private const float HeadroomRatio = 0.12f;             // R12: minimum headroom above face (12% of crop)
    private const float LookroomRatio = 0.10f;             // R12: extra space in the direction subject faces

    // R6: last emitted crop, used to suppress micro-jitter between recomputes of the same artwork.
    private Int32Rect _lastCropRect;
    private int _lastCropImgWidth;
    private int _lastCropImgHeight;
    private bool _hasLastCrop;

    private bool _disposed;
    private bool _modelExists;
    private bool _modelExistsChecked;
    private InferenceSession? _cachedSession;

    // COCO class IDs for multi-class priority: person > product > animal > background
    private static readonly HashSet<int> _personClasses = new() { 0 }; // person
    private static readonly HashSet<int> _animalClasses = new()
    {
        14, 15, 16, 17, 18, 19, 20, 21, 22, 23 // bird, cat, dog, horse, sheep, cow, elephant, bear, zebra, giraffe
    };
    private static readonly HashSet<int> _productClasses = new()
    {
        39, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59,
        60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79
    };

    private static float GetClassPriority(int classId)
    {
        if (_personClasses.Contains(classId)) return 5.0f;  // Person ALWAYS highest priority
        if (_animalClasses.Contains(classId)) return 2.5f;  // Animals second
        if (_productClasses.Contains(classId)) return 2.0f; // Products third
        return 1.0f;                                         // Everything else (text, background)
    }

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

    public void Unload()
    {
        lock (_lock)
        {
            _cachedSession?.Dispose();
            _cachedSession = null;
        }
    }

    public SubjectBounds? GetDominantSubjectBounds(BitmapImage source)
    {
        if (_disposed) return null;
        if (!_modelExists && !TryInitialize()) return null;
        if (!_modelExists) return null;
        if (source == null) return null;

        int imgWidth = source.PixelWidth;
        int imgHeight = source.PixelHeight;
        if (imgWidth < 64 || imgHeight < 64) return null;

        lock (_lock)
        {
            float[]? tensorBuffer = null;
            try
            {
                if (_cachedSession == null)
                {
                    var options = new SessionOptions();
                    options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                    options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    options.InterOpNumThreads = 1;
                    options.IntraOpNumThreads = 4;
                    options.EnableMemoryPattern = true;
                    options.EnableCpuMemArena = true;
                    _cachedSession = new InferenceSession(GetModelPath(), options);
                }

                int requiredLength = 1 * 3 * ModelInputSize * ModelInputSize;
                tensorBuffer = ArrayPool<float>.Shared.Rent(requiredLength);

                var (scale, _, padX, padY) = PreprocessImageFast(source, tensorBuffer);

                var tensor = new DenseTensor<float>(
                    new Memory<float>(tensorBuffer, 0, requiredLength),
                    new[] { 1, 3, ModelInputSize, ModelInputSize });
                var inputName = _cachedSession.InputNames[0];
                var inputs = new List<NamedOnnxValue>(1)
                {
                    NamedOnnxValue.CreateFromTensor(inputName, tensor)
                };

                using var results = _cachedSession.Run(inputs);
                var output = results.First().AsTensor<float>();

                var detections = ParseYolov8Output(output, imgWidth, imgHeight, scale, padX, padY);
                if (detections.Count == 0) return null;

                // Pick best by class priority × confidence × area share × center weight.
                Detection? best = null;
                float bestScore = float.MinValue;
                float imgArea = imgWidth * imgHeight;

                foreach (var d in detections)
                {
                    float area = (d.X2 - d.X1) * (d.Y2 - d.Y1);
                    if (area <= 0) continue;
                    float areaShare = area / imgArea;
                    float score = d.Confidence * GetClassPriority(d.ClassId) * MathF.Sqrt(areaShare) * GetCenterWeight(d, imgWidth, imgHeight);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = d;
                    }
                }

                if (best is not Detection b) return null;

                float cx = (b.X1 + b.X2) / 2f / imgWidth;
                float cy = (b.Y1 + b.Y2) / 2f / imgHeight;
                float w = (b.X2 - b.X1) / imgWidth;
                float h = (b.Y2 - b.Y1) / imgHeight;

                // Persons: bias subject center upward toward the face for nicer "spotlight".
                if (_personClasses.Contains(b.ClassId))
                {
                    cy = (b.Y1 + (b.Y2 - b.Y1) * 0.12f) / imgHeight;
                }

                return new SubjectBounds(cx, cy, w, h, b.Confidence, b.ClassId);
            }
            catch (Exception ex)
            {
                VNotch.Services.RuntimeLog.Error("SUBJECT-BOUNDS", $"Inference failed: {ex.GetType().Name}: {ex.Message}");
                _cachedSession?.Dispose();
                _cachedSession = null;
                return null;
            }
            finally
            {
                if (tensorBuffer != null)
                    ArrayPool<float>.Shared.Return(tensorBuffer);
            }
        }
    }

    public Int32Rect? GetSmartCropRect(BitmapImage source, int targetSquareSize)
    {
        var rect = ComputeSmartCropRectCore(source, targetSquareSize);
        if (rect.HasValue && source != null)
        {
            // R6: suppress micro-jitter between recomputes of the same artwork.
            return Stabilize(rect.Value, source.PixelWidth, source.PixelHeight);
        }
        return rect;
    }

    private Int32Rect? ComputeSmartCropRectCore(BitmapImage source, int targetSquareSize)
    {
        if (_disposed) return null;
        if (!_modelExists && !TryInitialize())
        {
            VNotch.Services.RuntimeLog.Log("SMART-CROP", $"GetSmartCropRect: model init failed");
            return null;
        }
        if (!_modelExists)
        {
            VNotch.Services.RuntimeLog.Log("SMART-CROP", $"GetSmartCropRect: model not exist");
            return null;
        }

        lock (_lock)
        {
            float[]? tensorBuffer = null;

            try
            {
                int imgWidth = source.PixelWidth;
                int imgHeight = source.PixelHeight;

                // Skip if image is already square or very small
                if (Math.Abs(imgWidth - imgHeight) < 10 || imgWidth < 64 || imgHeight < 64)
                {
                    VNotch.Services.RuntimeLog.Log("SMART-CROP", $"GetSmartCropRect: skip (square/small) {imgWidth}x{imgHeight}");
                    return null;
                }

                // For small images (< 400px wide), ONNX is overkill — use saliency directly
                if (imgWidth < 400 && imgHeight < 400)
                {
                    int maxCrop = Math.Min(imgWidth, imgHeight);
                    int cropSz = Math.Min(targetSquareSize, maxCrop);
                    VNotch.Services.RuntimeLog.Log("SMART-CROP", $"GetSmartCropRect: small image {imgWidth}x{imgHeight} -> saliency only");
                    return GetSaliencyCropRect(source, imgWidth, imgHeight, cropSz);
                }

                // ─── Reuse cached session or create new one ───
                if (_cachedSession == null)
                {
                    var options = new SessionOptions();
                    options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                    options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    options.InterOpNumThreads = 1;
                    options.IntraOpNumThreads = 4;
                    options.EnableMemoryPattern = true;
                    options.EnableCpuMemArena = true;

                    _cachedSession = new InferenceSession(GetModelPath(), options);
                    System.Diagnostics.Debug.WriteLine("[SmartCrop] Model loaded (cached session).");
                }

                int requiredLength = 1 * 3 * ModelInputSize * ModelInputSize;
                tensorBuffer = ArrayPool<float>.Shared.Rent(requiredLength);

                // ─── Preprocess ───
                var (scaleX, scaleY, padX, padY) = PreprocessImageFast(source, tensorBuffer);

                // ─── Run inference ─── ArrayPool
                var tensor = new DenseTensor<float>(
                    new Memory<float>(tensorBuffer, 0, requiredLength),
                    new[] { 1, 3, ModelInputSize, ModelInputSize });
                var inputName = _cachedSession.InputNames[0];
                var inputs = new List<NamedOnnxValue>(1)
                {
                    NamedOnnxValue.CreateFromTensor(inputName, tensor)
                };

                using var results = _cachedSession.Run(inputs);
                var output = results.First().AsTensor<float>();

                // ─── Parse detections ───
                var detections = ParseYolov8Output(output, imgWidth, imgHeight, scaleX, padX, padY);

                if (detections.Count == 0)
                {
                    VNotch.Services.RuntimeLog.Log("SMART-CROP", "ONNX produced 0 detections -> saliency fallback");
                    // No YOLO detections — use saliency/attention fallback
                    return GetSaliencyCropRect(source, imgWidth, imgHeight, targetSquareSize);
                }

                // ─── Multi-signal hybrid crop ───
                VNotch.Services.RuntimeLog.Log("SMART-CROP", $"raw detections count={detections.Count}");
                return GetHybridCropRect(detections, source, imgWidth, imgHeight, targetSquareSize);
            }
            catch (Exception ex)
            {
                VNotch.Services.RuntimeLog.Error("SMART-CROP", $"Inference failed: {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[SmartCrop] Inference failed: {ex.Message}");
                _cachedSession?.Dispose();
                _cachedSession = null;
                return null;
            }
            finally
            {
                if (tensorBuffer != null)
                    ArrayPool<float>.Shared.Return(tensorBuffer);
            }
        }
    }

    private (float scale, float scaleY, float padX, float padY) PreprocessImageFast(BitmapImage source, float[] tensorBuffer)
    {
        int imgWidth = source.PixelWidth;
        int imgHeight = source.PixelHeight;

        float scale = Math.Min((float)ModelInputSize / imgWidth, (float)ModelInputSize / imgHeight);
        int newWidth = (int)(imgWidth * scale);
        int newHeight = (int)(imgHeight * scale);
        float padX = (ModelInputSize - newWidth) / 2f;
        float padY = (ModelInputSize - newHeight) / 2f;
        int padXi = (int)padX;
        int padYi = (int)padY;

        var scaled = new TransformedBitmap(source, new ScaleTransform(
            (double)newWidth / imgWidth,
            (double)newHeight / imgHeight));
        scaled.Freeze();

        int scaledW = scaled.PixelWidth;
        int scaledH = scaled.PixelHeight;
        int stride = scaledW * 4;

        byte[] pixels = ArrayPool<byte>.Shared.Rent(scaledH * stride);
        try
        {
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

            int planeSize = ModelInputSize * ModelInputSize;
            const float grayVal = 114f / 255f;
            const float inv255 = 1f / 255f;

            tensorBuffer.AsSpan().Fill(grayVal);

            for (int y = 0; y < scaledH && (y + padYi) < ModelInputSize; y++)
            {
                int tensorY = y + padYi;
                int rowOffset = y * stride;
                int tensorRowBase = tensorY * ModelInputSize;

                for (int x = 0; x < scaledW && (x + padXi) < ModelInputSize; x++)
                {
                    int pixelIdx = rowOffset + x * 4;
                    int tensorIdx = tensorRowBase + (x + padXi);

                    tensorBuffer[tensorIdx] = pixels[pixelIdx + 2] * inv255;
                    tensorBuffer[planeSize + tensorIdx] = pixels[pixelIdx + 1] * inv255;
                    tensorBuffer[2 * planeSize + tensorIdx] = pixels[pixelIdx] * inv255;
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
        var detections = new List<Detection>(32);
        var dims = output.Dimensions;
        int numPredictions = dims[2];
        int numClasses = dims[1] - 4; // 80 classes for COCO
        float imgArea = imgWidth * imgHeight;

        for (int i = 0; i < numPredictions; i++)
        {
            // Find the class with highest confidence
            float maxScore = 0f;
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

            if (maxClassId < 0) continue;

            // Apply class-specific confidence thresholds
            bool isPerson = _personClasses.Contains(maxClassId);
            float threshold = isPerson ? PersonConfidenceThreshold : ConfidenceThreshold;
            if (maxScore < threshold) continue;

            float cx = output[0, 0, i];
            float cy = output[0, 1, i];
            float w = output[0, 2, i];
            float h = output[0, 3, i];

            // Convert from model space to original image space
            float x1 = (cx - w / 2f - padX) / scale;
            float y1 = (cy - h / 2f - padY) / scale;
            float x2 = (cx + w / 2f - padX) / scale;
            float y2 = (cy + h / 2f - padY) / scale;

            x1 = Math.Clamp(x1, 0, imgWidth);
            y1 = Math.Clamp(y1, 0, imgHeight);
            x2 = Math.Clamp(x2, 0, imgWidth);
            y2 = Math.Clamp(y2, 0, imgHeight);

            float bboxArea = (x2 - x1) * (y2 - y1);
            float areaRatio = bboxArea / imgArea;

            // Rule: Min area filter (class-specific)
            if (isPerson)
            {
                if (areaRatio < MinPersonAreaRatio) continue;
            }
            else
            {
                if (areaRatio < MinAreaRatio) continue;
            }

            // Skip tiny detections (likely noise)
            if (x2 - x1 < 5 || y2 - y1 < 5) continue;

            detections.Add(new Detection
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Confidence = maxScore,
                ClassId = maxClassId
            });
        }

        VNotch.Services.RuntimeLog.Log("SMART-CROP",
            $"ParseYolov8: raw={detections.Count} predictions scanned={numPredictions} classes={numClasses}");

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
                // Rule: NMS IoU ≤ 0.45
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

    private static float GetCenterWeight(Detection d, int imgWidth, int imgHeight)
    {
        float objCenterX = (d.X1 + d.X2) / 2f;
        float objCenterY = (d.Y1 + d.Y2) / 2f;
        float imgCenterX = imgWidth / 2f;
        float imgCenterY = imgHeight / 2f;

        // Normalized distance from center (0 = center, 1 = corner)
        float dx = (objCenterX - imgCenterX) / imgCenterX;
        float dy = (objCenterY - imgCenterY) / imgCenterY;
        float dist = MathF.Sqrt(dx * dx + dy * dy) / MathF.Sqrt(2f);

        // Weight: 1.0 at center, 0.5 at corners
        return 1.0f - dist * 0.5f;
    }

    private List<TextRegion> DetectTextRegions(BitmapImage source, int imgWidth, int imgHeight)
    {
        var regions = new List<TextRegion>();

        try
        {
            const int gridSize = 8;
            int cellW = imgWidth / gridSize;
            int cellH = imgHeight / gridSize;

            // Downsample for analysis
            const int analysisSize = 128;
            double scaleX = (double)analysisSize / imgWidth;
            double scaleY = (double)analysisSize / imgHeight;
            var scaled = new TransformedBitmap(source, new ScaleTransform(scaleX, scaleY));
            scaled.Freeze();

            int w = scaled.PixelWidth;
            int h = scaled.PixelHeight;
            if (w < 8 || h < 8) return regions;

            int stride = w * 4;
            byte[] pixels = ArrayPool<byte>.Shared.Rent(h * stride);
            try
            {

            BitmapSource scaledSource = scaled;
            if (scaled.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                scaledSource = converted;
            }
            scaledSource.CopyPixels(pixels, stride, 0);

            int aCellW = w / gridSize;
            int aCellH = h / gridSize;

            for (int gy = 0; gy < gridSize; gy++)
            {
                for (int gx = 0; gx < gridSize; gx++)
                {
                    int startX = gx * aCellW;
                    int startY = gy * aCellH;
                    int endX = Math.Min(startX + aCellW, w - 1);
                    int endY = Math.Min(startY + aCellH, h - 1);

                    double edgeCount = 0;
                    int pixelCount = 0;

                    for (int y = startY; y < endY; y++)
                    {
                        for (int x = startX; x < endX; x++)
                        {
                            int i = y * stride + x * 4;
                            int iRight = i + 4;
                            int iBelow = (y + 1) * stride + x * 4;

                            if (x + 1 >= w || y + 1 >= h) continue;

                            double lum = 0.299 * pixels[i + 2] + 0.587 * pixels[i + 1] + 0.114 * pixels[i];
                            double lumR = 0.299 * pixels[iRight + 2] + 0.587 * pixels[iRight + 1] + 0.114 * pixels[iRight];
                            double lumB = 0.299 * pixels[iBelow + 2] + 0.587 * pixels[iBelow + 1] + 0.114 * pixels[iBelow];

                            double grad = Math.Abs(lum - lumR) + Math.Abs(lum - lumB);
                            if (grad > 40) edgeCount++;
                            pixelCount++;
                        }
                    }

                    if (pixelCount == 0) continue;

                    double edgeRatio = edgeCount / pixelCount;

                    // High edge density (> 0.3) indicates text-like content
                    if (edgeRatio > 0.30)
                    {
                        float regionHeight = cellH;
                        // Rule: font_factor = sqrt(text_height)
                        float fontFactor = MathF.Sqrt(regionHeight);

                        regions.Add(new TextRegion
                        {
                            X1 = gx * cellW,
                            Y1 = gy * cellH,
                            X2 = (gx + 1) * cellW,
                            Y2 = (gy + 1) * cellH,
                            EdgeDensity = (float)edgeRatio,
                            FontFactor = fontFactor
                        });
                    }
                }
            }

            } // end try
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SmartCrop] Text detection failed: {ex.Message}");
        }

        return regions;
    }

    private Int32Rect GetHybridCropRect(List<Detection> detections, BitmapImage source, int imgWidth, int imgHeight, int targetSize)
    {
        float imgArea = imgWidth * imgHeight;
        int maxCropSize = Math.Min(imgWidth, imgHeight);
        int cropSize = Math.Min(targetSize, maxCropSize); // used only by text/saliency paths

        // ─── Classify detections ───
        var persons = new List<Detection>();
        var objects = new List<Detection>();

        foreach (var d in detections)
        {
            if (_personClasses.Contains(d.ClassId))
                persons.Add(d);
            else
                objects.Add(d);
        }

        VNotch.Services.RuntimeLog.Log("SMART-CROP",
            $"img={imgWidth}x{imgHeight} detections total={detections.Count} persons={persons.Count} objects={objects.Count}");

        if (persons.Count >= 1)
        {
            // Multiple persons = group photo → frame the whole group (R4)
            if (persons.Count >= 2)
            {
                VNotch.Services.RuntimeLog.Log("SMART-CROP",
                    $"GROUP detected: {persons.Count} persons → framing group union");
                return GetPersonCropRect(persons, imgWidth, imgHeight, targetSize);
            }

            // Single person
            var largestPerson = persons[0];
            float personAreaRatio = largestPerson.Area / imgArea;

            VNotch.Services.RuntimeLog.Log("SMART-CROP",
                $"person path: largest=({largestPerson.X1:F0},{largestPerson.Y1:F0})-({largestPerson.X2:F0},{largestPerson.Y2:F0}) areaRatio={personAreaRatio:F3} portraitThr={PortraitThreshold}");

            if (personAreaRatio >= PortraitThreshold)
                return GetPortraitCropRect(largestPerson, imgWidth, imgHeight, targetSize);

            return GetPersonCropRect(persons, imgWidth, imgHeight, targetSize);
        }

        var textRegions = DetectTextRegions(source, imgWidth, imgHeight);
        bool hasText = textRegions.Count >= 2; // Need at least 2 text cells to be meaningful

        if (hasText && objects.Count > 0)
        {
            // Both text and objects present (no person) → text wins
            VNotch.Services.RuntimeLog.Log("SMART-CROP",
                $"text+object: textRegions={textRegions.Count} objects={objects.Count} → prioritizing text");
            var textCrop = GetTextFirstCropRect(textRegions, imgWidth, imgHeight, cropSize);
            // Try to expand crop to include nearby text
            return ExpandCropForText(textCrop, textRegions, imgWidth, imgHeight, cropSize);
        }

        if (hasText && objects.Count == 0)
        {
            // Only text, no objects → focus on text
            VNotch.Services.RuntimeLog.Log("SMART-CROP",
                $"text-only: textRegions={textRegions.Count} → text crop");
            var textCrop = GetTextFirstCropRect(textRegions, imgWidth, imgHeight, cropSize);
            return ExpandCropForText(textCrop, textRegions, imgWidth, imgHeight, cropSize);
        }

        if (objects.Count > 0)
        {
            // Only objects, no text → focus on best object
            var bestObj = objects
                .Select(d => new
                {
                    Detection = d,
                    Score = d.Confidence * GetCenterWeight(d, imgWidth, imgHeight) * GetClassPriority(d.ClassId)
                })
                .OrderByDescending(x => x.Score)
                .First()
                .Detection;

            // Compute exact center of the detected object
            float objCenterX = (bestObj.X1 + bestObj.X2) / 2f;
            float objCenterY = (bestObj.Y1 + bestObj.Y2) / 2f;
            float objWidth = bestObj.X2 - bestObj.X1;
            float objHeight = bestObj.Y2 - bestObj.Y1;

            VNotch.Services.RuntimeLog.Log("SMART-CROP",
                $"object path: classId={bestObj.ClassId} center=({objCenterX:F0},{objCenterY:F0}) size=({objWidth:F0}x{objHeight:F0})");

            // R1 + R3: adaptive square that contains the object with margin, centred on it.
            int objCrop = ComputeAdaptiveCropSize(objWidth, objHeight, imgWidth, imgHeight, targetSize);
            var objRect = BuildComposedCropRect(objCenterX, objCenterY, ObjectVerticalFraction, imgWidth, imgHeight, objCrop);

            // R7–R12: Apply composition rules for objects (no person-specific lookroom)
            objRect = ApplyCompositionRules(objRect, objCenterX, objCenterY, imgWidth, imgHeight, isPerson: false, subjectDetection: bestObj);
            return objRect;
        }

        // ─── No persons, no objects, no text: saliency fallback ───
        VNotch.Services.RuntimeLog.Log("SMART-CROP", "no person/object/text -> saliency fallback");
        var saliencyRect = GetSaliencyCropRect(source, imgWidth, imgHeight, targetSize);
        if (saliencyRect.HasValue)
        {
            VNotch.Services.RuntimeLog.Log("SMART-CROP",
                $"saliency rect=({saliencyRect.Value.X},{saliencyRect.Value.Y},{saliencyRect.Value.Width}x{saliencyRect.Value.Height})");
            return saliencyRect.Value;
        }

        VNotch.Services.RuntimeLog.Log("SMART-CROP", "saliency null -> exact center");
        return BuildCropRect(imgWidth / 2f, imgHeight / 2f, imgWidth, imgHeight, cropSize);
    }

    private Int32Rect GetPortraitCropRect(Detection person, int imgWidth, int imgHeight, int targetSize)
    {
        float personWidth = person.X2 - person.X1;
        float personHeight = person.Y2 - person.Y1;

        // Estimate face center X: head is narrower than body and centered on the
        // upper portion of the bbox. Blend body center with image center (faces in
        // thumbnails tend to be near image center more than body center).
        float bodyCenterX = (person.X1 + person.X2) / 2f;
        float imgCenterX = imgWidth / 2f;
        float faceCenterX = bodyCenterX * 0.7f + imgCenterX * 0.3f;

        // Face center Y: ~12% down from the top of the person bbox (eye-level).
        float faceCenterY = person.Y1 + personHeight * 0.12f;

        float subjectExtent = Math.Max(personWidth, personHeight * 0.6f);
        int cropSize = ComputeAdaptiveCropSize(subjectExtent, subjectExtent, imgWidth, imgHeight, targetSize);

        // Center crop on estimated face position.
        var crop = BuildComposedCropRect(faceCenterX, faceCenterY, 0.5f, imgWidth, imgHeight, cropSize);

        VNotch.Services.RuntimeLog.Log("SMART-CROP",
            $"portrait: bodyCX={bodyCenterX:F0} faceCX={faceCenterX:F0} faceCY={faceCenterY:F0} cropSize={cropSize} rect=({crop.X},{crop.Y},{crop.Width})");

        return crop;
    }

    private Int32Rect GetPersonCropRect(List<Detection> persons, int imgWidth, int imgHeight, int targetSize)
    {
        if (persons.Count == 1)
        {
            var p = persons[0];
            float personHeight = p.Y2 - p.Y1;
            float personWidth = p.X2 - p.X1;

            // Estimate face center: blend body center X with image center X.
            float bodyCenterX = (p.X1 + p.X2) / 2f;
            float imgCenterX = imgWidth / 2f;
            float faceCenterX = bodyCenterX * 0.7f + imgCenterX * 0.3f;

            // Face center Y: ~12% from top of person bbox (eye-level).
            float faceCenterY = p.Y1 + personHeight * 0.12f;

            // Adaptive size containing the person with breathing room.
            int cropSize = ComputeAdaptiveCropSize(personWidth, personHeight, imgWidth, imgHeight, targetSize);

            // Center crop on estimated face position.
            var crop = BuildComposedCropRect(faceCenterX, faceCenterY, 0.5f, imgWidth, imgHeight, cropSize);

            VNotch.Services.RuntimeLog.Log("SMART-CROP",
                $"single person: bbox=({p.X1:F0},{p.Y1:F0})-({p.X2:F0},{p.Y2:F0}) faceCX={faceCenterX:F0} faceCY={faceCenterY:F0} cropSize={cropSize} rect=({crop.X},{crop.Y},{crop.Width})");

            return crop;
        }
        else
        {
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            float topFaceY = float.MaxValue;

            foreach (var p in persons)
            {
                if (p.X1 < minX) minX = p.X1;
                if (p.Y1 < minY) minY = p.Y1;
                if (p.X2 > maxX) maxX = p.X2;
                if (p.Y2 > maxY) maxY = p.Y2;

                float faceY = p.Y1 + (p.Y2 - p.Y1) * 0.12f;
                if (faceY < topFaceY) topFaceY = faceY;
            }

            float groupW = maxX - minX;
            float groupH = maxY - minY;
            float groupCenterX = (minX + maxX) / 2f;

            // Focal Y: center between topmost face and group center.
            float groupCenterY = (minY + maxY) / 2f;
            float focalY = (topFaceY + groupCenterY) / 2f;

            // Adaptive size that contains the whole group.
            int cropSize = ComputeAdaptiveCropSize(groupW, groupH, imgWidth, imgHeight, targetSize);

            // Center crop on focal point.
            var crop = BuildComposedCropRect(groupCenterX, focalY, 0.5f, imgWidth, imgHeight, cropSize);

            // R1 containment: keep the topmost head in frame.
            crop = EnsureTopNotClipped(crop, minY - groupH * 0.03f, imgWidth, imgHeight);

            // R7–R12: Apply composition rules (no single subject for lookroom)
            crop = ApplyCompositionRules(crop, groupCenterX, focalY, imgWidth, imgHeight, isPerson: true, subjectDetection: null);

            VNotch.Services.RuntimeLog.Log("SMART-CROP",
                $"group ({persons.Count} persons): union=({minX:F0},{minY:F0})-({maxX:F0},{maxY:F0}) cropSize={cropSize} rect=({crop.X},{crop.Y},{crop.Width})");

            return crop;
        }
    }

    private Int32Rect GetTextFirstCropRect(List<TextRegion> textRegions, int imgWidth, int imgHeight, int cropSize)
    {
        // Compute convex hull of all text regions
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;

        foreach (var t in textRegions)
        {
            if (t.X1 < minX) minX = t.X1;
            if (t.X2 > maxX) maxX = t.X2;
            if (t.Y1 < minY) minY = t.Y1;
            if (t.Y2 > maxY) maxY = t.Y2;
        }

        // Center crop on text hull
        float textCenterX = (minX + maxX) / 2f;
        float textCenterY = (minY + maxY) / 2f;

        return BuildCropRect(textCenterX, textCenterY, imgWidth, imgHeight, cropSize);
    }

    private Int32Rect ExpandCropForText(Int32Rect currentCrop, List<TextRegion> textRegions, int imgWidth, int imgHeight, int cropSize)
    {
        float cropX1 = currentCrop.X;
        float cropY1 = currentCrop.Y;
        float cropX2 = currentCrop.X + currentCrop.Width;
        float cropY2 = currentCrop.Y + currentCrop.Height;

        float imgArea = imgWidth * imgHeight;
        float maxTextArea = imgArea * TextSafeZoneRatio;

        // Check which text regions are partially outside current crop
        float expandMinX = cropX1, expandMaxX = cropX2;
        float expandMinY = cropY1, expandMaxY = cropY2;

        foreach (var t in textRegions)
        {
            float textArea = (t.X2 - t.X1) * (t.Y2 - t.Y1);
            if (textArea > maxTextArea) continue; // Skip if text region too large

            // Only expand if text is close to current crop (within 20% margin)
            float margin = cropSize * 0.2f;
            bool isNearby = t.X1 < cropX2 + margin && t.X2 > cropX1 - margin &&
                           t.Y1 < cropY2 + margin && t.Y2 > cropY1 - margin;

            if (isNearby)
            {
                expandMinX = Math.Min(expandMinX, t.X1);
                expandMaxX = Math.Max(expandMaxX, t.X2);
                expandMinY = Math.Min(expandMinY, t.Y1);
                expandMaxY = Math.Max(expandMaxY, t.Y2);
            }
        }

        // If expansion needed, recalculate center but keep crop size fixed
        float newCenterX = (expandMinX + expandMaxX) / 2f;
        float newCenterY = (expandMinY + expandMaxY) / 2f;

        // Only shift if the expansion is reasonable (doesn't move too far from original)
        float originalCenterX = currentCrop.X + currentCrop.Width / 2f;
        float originalCenterY = currentCrop.Y + currentCrop.Height / 2f;
        float maxShift = cropSize * 0.25f;

        float finalCenterX = Math.Clamp(newCenterX, originalCenterX - maxShift, originalCenterX + maxShift);
        float finalCenterY = Math.Clamp(newCenterY, originalCenterY - maxShift, originalCenterY + maxShift);

        return BuildCropRect(finalCenterX, finalCenterY, imgWidth, imgHeight, cropSize);
    }


    private static int ComputeAdaptiveCropSize(float subjectW, float subjectH, int imgWidth, int imgHeight, int targetSize)
    {
        int maxCrop = Math.Min(imgWidth, imgHeight);

        // Subject's largest extent + symmetric margin on both sides.
        float subjectExtent = Math.Max(subjectW, subjectH);
        float desired = subjectExtent * (1f + 2f * SubjectMarginRatio);

        float minCrop = maxCrop * MinCropRatio;
        float size = Math.Clamp(desired, minCrop, maxCrop);

        if (targetSize > 0)
            size = Math.Max(size, Math.Min(targetSize, maxCrop));

        return (int)MathF.Round(size);
    }

    private static Int32Rect BuildComposedCropRect(
        float focalX, float focalY, float verticalFraction,
        int imgWidth, int imgHeight, int cropSize)
    {
        cropSize = Math.Min(cropSize, Math.Min(imgWidth, imgHeight));

        // Simple: center the crop on the focal point (face center).
        float cropX = focalX - cropSize / 2f;
        float cropY = focalY - cropSize / 2f;

        // Clamp to image bounds.
        cropX = Math.Clamp(cropX, 0, imgWidth - cropSize);
        cropY = Math.Clamp(cropY, 0, imgHeight - cropSize);

        return new Int32Rect((int)MathF.Round(cropX), (int)MathF.Round(cropY), cropSize, cropSize);
    }

    /// <summary>
    /// R1 (containment guarantee) — Ensures the subject's head/top is not clipped.
    /// If the head would be above the crop's top edge, shift the crop up.
    /// Additionally enforces a minimum headroom (HeadroomRatio) above the subject top.
    /// </summary>
    private static Int32Rect EnsureTopNotClipped(Int32Rect crop, float subjectTop, int imgWidth, int imgHeight)
    {
        // Minimum headroom: at least HeadroomRatio of crop height above the subject top.
        float minHeadroom = crop.Height * HeadroomRatio;
        float desiredCropTop = subjectTop - minHeadroom;

        if (desiredCropTop < crop.Y)
        {
            int newY = Math.Max(0, (int)desiredCropTop);
            newY = Math.Min(newY, imgHeight - crop.Height);
            return new Int32Rect(crop.X, newY, crop.Width, crop.Height);
        }
        return crop;
    }

    private static (float x, float y) ApplyThirdsSnap(float focalX, float focalY, int cropSize)
    {
        // Determine which third-line intersection is closest
        float[] thirdXs = { cropSize * ThirdLineLeft, cropSize * 0.5f, cropSize * ThirdLineRight };
        float[] thirdYs = { cropSize * ThirdLineTop, cropSize * 0.5f, cropSize * ThirdLineBottom };

        float bestDist = float.MaxValue;
        float snapX = focalX, snapY = focalY;

        foreach (float tx in thirdXs)
        {
            foreach (float ty in thirdYs)
            {
                float dist = MathF.Sqrt((focalX - tx) * (focalX - tx) + (focalY - ty) * (focalY - ty));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    snapX = tx;
                    snapY = ty;
                }
            }
        }

        // Blend toward the nearest power point with controlled strength
        float resultX = focalX + (snapX - focalX) * ThirdsSnapStrength;
        float resultY = focalY + (snapY - focalY) * ThirdsSnapStrength;

        return (resultX, resultY);
    }

    private static float GetGoldenVerticalFraction(bool isPerson, bool isPortrait)
    {
        if (isPortrait) return GoldenVerticalFraction + 0.04f; // 0.422 — slightly lower for tight portraits
        if (isPerson) return GoldenVerticalFraction;            // 0.382 — golden line for faces
        return 0.5f;                                            // Objects: centered
    }

    private static (float focalX, float focalY) ApplyAspectAwareFraming(
        float focalX, float focalY, int imgWidth, int imgHeight)
    {
        float aspectRatio = (float)imgWidth / imgHeight;

        if (aspectRatio >= PanoramicThreshold)
        {
            // Panoramic: bias focal X toward image center to avoid cropping edges of wide scenes
            float imgCenterX = imgWidth / 2f;
            focalX = focalX + (imgCenterX - focalX) * PanoramicCenterBias;
        }
        else if (aspectRatio <= TallThreshold)
        {
            // Tall/vertical: bias focal Y toward upper portion (album art, posters often have subject on top)
            float upperTarget = imgHeight * TallVerticalBias;
            focalY = focalY + (upperTarget - focalY) * 0.3f;
        }

        return (focalX, focalY);
    }

    private static (float x, float y) ApplyEdgeAvoidance(float focalX, float focalY, int cropSize)
    {
        float minDist = cropSize * EdgeAvoidanceRatio;
        float softDist = cropSize * EdgeSoftClampRatio;

        // Soft-clamp: if focal is within the edge zone, push it inward
        if (focalX < minDist)
            focalX = minDist + (focalX - softDist) * 0.3f;
        else if (focalX > cropSize - minDist)
            focalX = (cropSize - minDist) + (focalX - (cropSize - softDist)) * 0.3f;

        if (focalY < minDist)
            focalY = minDist + (focalY - softDist) * 0.3f;
        else if (focalY > cropSize - minDist)
            focalY = (cropSize - minDist) + (focalY - (cropSize - softDist)) * 0.3f;

        // Hard clamp to ensure we never exceed bounds
        focalX = Math.Clamp(focalX, softDist, cropSize - softDist);
        focalY = Math.Clamp(focalY, softDist, cropSize - softDist);

        return (focalX, focalY);
    }

    private static (float cropX, float cropY) ApplyVisualBalance(
        float cropX, float cropY, float focalX, float focalY, int cropSize, int imgWidth, int imgHeight)
    {
        // Calculate how off-center the focal point is within the crop
        float cropCenterX = cropX + cropSize / 2f;
        float cropCenterY = cropY + cropSize / 2f;

        float offsetX = focalX - cropCenterX;
        float offsetY = focalY - cropCenterY;

        // Shift crop in the direction of the subject to create breathing space on the other side
        float maxShift = cropSize * BalanceMaxShift;
        float shiftX = Math.Clamp(offsetX * BalanceWeight, -maxShift, maxShift);
        float shiftY = Math.Clamp(offsetY * BalanceWeight, -maxShift, maxShift);

        cropX += shiftX;
        cropY += shiftY;

        // Clamp to image bounds
        cropX = Math.Clamp(cropX, 0, imgWidth - cropSize);
        cropY = Math.Clamp(cropY, 0, imgHeight - cropSize);

        return (cropX, cropY);
    }

    private static Int32Rect ApplyHeadroomAndLookroom(
        Int32Rect crop, Detection person, int imgWidth, int imgHeight)
    {
        int cropSize = crop.Width;
        float personCenterX = (person.X1 + person.X2) / 2f;
        float personTop = person.Y1;

        // Headroom: ensure at least HeadroomRatio of crop above the person's head
        float currentHeadroom = personTop - crop.Y;
        float minHeadroom = cropSize * HeadroomRatio;

        int newY = crop.Y;
        if (currentHeadroom < minHeadroom)
        {
            newY = (int)(personTop - minHeadroom);
            newY = Math.Clamp(newY, 0, imgHeight - cropSize);
        }

        float imgCenterX = imgWidth / 2f;
        float lookDirection = (personCenterX < imgCenterX) ? 1f : -1f; // +1 = facing right, -1 = facing left
        float lookShift = cropSize * LookroomRatio * lookDirection;

        int newX = crop.X - (int)(lookShift * 0.5f); // Shift crop opposite to look direction
        newX = Math.Clamp(newX, 0, imgWidth - cropSize);

        return new Int32Rect(newX, newY, cropSize, cropSize);
    }

    private static Int32Rect ApplyCompositionRules(
        Int32Rect initialCrop, float focalX, float focalY,
        int imgWidth, int imgHeight, bool isPerson, Detection? subjectDetection = null)
    {
        int cropSize = initialCrop.Width;

        // R9: Aspect-aware framing adjusts focal point based on source geometry
        var (adjustedFocalX, adjustedFocalY) = ApplyAspectAwareFraming(focalX, focalY, imgWidth, imgHeight);

        // R7: Rule of Thirds snap — nudge focal toward nearest power point (relative to crop)
        float relFocalX = adjustedFocalX - initialCrop.X;
        float relFocalY = adjustedFocalY - initialCrop.Y;
        var (snappedRelX, snappedRelY) = ApplyThirdsSnap(relFocalX, relFocalY, cropSize);

        // R10: Edge avoidance — ensure focal isn't too close to crop edges
        var (safeRelX, safeRelY) = ApplyEdgeAvoidance(snappedRelX, snappedRelY, cropSize);

        // Convert back to image coordinates and rebuild crop
        float newFocalX = initialCrop.X + safeRelX;
        float newFocalY = initialCrop.Y + safeRelY;

        // Rebuild crop centered on the refined focal point
        float cropX = newFocalX - cropSize / 2f;
        float cropY = newFocalY - cropSize / 2f;
        cropX = Math.Clamp(cropX, 0, imgWidth - cropSize);
        cropY = Math.Clamp(cropY, 0, imgHeight - cropSize);

        // R11: Visual balance — subtle shift for counterbalancing negative space
        var (balancedX, balancedY) = ApplyVisualBalance(cropX, cropY, adjustedFocalX, adjustedFocalY, cropSize, imgWidth, imgHeight);

        var result = new Int32Rect((int)MathF.Round(balancedX), (int)MathF.Round(balancedY), cropSize, cropSize);

        // R12: Headroom & Lookroom for persons
        if (isPerson && subjectDetection.HasValue)
        {
            result = ApplyHeadroomAndLookroom(result, subjectDetection.Value, imgWidth, imgHeight);
        }

        return result;
    }

    private Int32Rect Stabilize(Int32Rect candidate, int imgWidth, int imgHeight)
    {
        if (_hasLastCrop && _lastCropImgWidth == imgWidth && _lastCropImgHeight == imgHeight)
        {
            float minEdge = Math.Min(imgWidth, imgHeight);
            float centerThreshold = minEdge * StabilizeCenterThreshold;
            float sizeThreshold = minEdge * StabilizeSizeThreshold;

            float oldCx = _lastCropRect.X + _lastCropRect.Width / 2f;
            float oldCy = _lastCropRect.Y + _lastCropRect.Height / 2f;
            float newCx = candidate.X + candidate.Width / 2f;
            float newCy = candidate.Y + candidate.Height / 2f;

            float centerShift = MathF.Sqrt((newCx - oldCx) * (newCx - oldCx) + (newCy - oldCy) * (newCy - oldCy));
            float sizeShift = Math.Abs(candidate.Width - _lastCropRect.Width);

            if (centerShift < centerThreshold && sizeShift < sizeThreshold)
            {
                VNotch.Services.RuntimeLog.Log("SMART-CROP",
                    $"stabilize: reuse previous crop (centerShift={centerShift:F1}<{centerThreshold:F1}, sizeShift={sizeShift:F1}<{sizeThreshold:F1})");
                return _lastCropRect;
            }
        }

        _lastCropRect = candidate;
        _lastCropImgWidth = imgWidth;
        _lastCropImgHeight = imgHeight;
        _hasLastCrop = true;
        return candidate;
    }

    private static Int32Rect BuildCropRect(float centerX, float centerY, int imgWidth, int imgHeight, int cropSize)
    {
        int cropX = (int)(centerX - cropSize / 2f);
        int cropY = (int)(centerY - cropSize / 2f);

        cropX = Math.Max(0, Math.Min(cropX, imgWidth - cropSize));
        cropY = Math.Max(0, Math.Min(cropY, imgHeight - cropSize));

        return new Int32Rect(cropX, cropY, cropSize, cropSize);
    }

    private Int32Rect? GetSaliencyCropRect(BitmapImage source, int imgWidth, int imgHeight, int targetSize)
    {
        try
        {
            int maxCropSize = Math.Min(imgWidth, imgHeight);
            int cropSize = Math.Min(targetSize, maxCropSize);

            const int analysisSize = 64;
            double scaleX = (double)analysisSize / imgWidth;
            double scaleY = (double)analysisSize / imgHeight;
            var scaled = new TransformedBitmap(source, new ScaleTransform(scaleX, scaleY));
            scaled.Freeze();

            int w = scaled.PixelWidth;
            int h = scaled.PixelHeight;
            if (w < 4 || h < 4) return null;

            int stride = w * 4;
            byte[] pixels = ArrayPool<byte>.Shared.Rent(h * stride);
            try
            {

            BitmapSource scaledSource = scaled;
            if (scaled.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                scaledSource = converted;
            }
            scaledSource.CopyPixels(pixels, stride, 0);

            // Divide into grid and compute saliency per cell
            const int gridSize = 8;
            int cellW = w / gridSize;
            int cellH = h / gridSize;
            float[,] saliencyMap = new float[gridSize, gridSize];

            for (int gy = 0; gy < gridSize; gy++)
            {
                for (int gx = 0; gx < gridSize; gx++)
                {
                    int startX = gx * cellW;
                    int startY = gy * cellH;
                    int endX = Math.Min(startX + cellW, w - 1);
                    int endY = Math.Min(startY + cellH, h - 1);

                    double totalContrast = 0;
                    double totalBrightness = 0;
                    int pixelCount = 0;

                    for (int y = startY; y < endY; y++)
                    {
                        for (int x = startX; x < endX; x++)
                        {
                            int i = y * stride + x * 4;
                            if (x + 1 >= w || y + 1 >= h) continue;

                            int iRight = i + 4;
                            int iBelow = (y + 1) * stride + x * 4;

                            double lum = 0.299 * pixels[i + 2] + 0.587 * pixels[i + 1] + 0.114 * pixels[i];
                            double lumR = 0.299 * pixels[iRight + 2] + 0.587 * pixels[iRight + 1] + 0.114 * pixels[iRight];
                            double lumB = 0.299 * pixels[iBelow + 2] + 0.587 * pixels[iBelow + 1] + 0.114 * pixels[iBelow];

                            totalContrast += Math.Abs(lum - lumR) + Math.Abs(lum - lumB);
                            totalBrightness += lum;
                            pixelCount++;
                        }
                    }

                    if (pixelCount == 0) continue;

                    double avgContrast = totalContrast / pixelCount;
                    double avgBrightness = totalBrightness / pixelCount;

                    // Compute color saturation for this cell (subjects are colorful, text is not)
                    double totalSat = 0;
                    for (int y = startY; y < endY; y++)
                    {
                        for (int x = startX; x < endX; x++)
                        {
                            int idx = y * stride + x * 4;
                            double r = pixels[idx + 2] / 255.0;
                            double g = pixels[idx + 1] / 255.0;
                            double b = pixels[idx] / 255.0;
                            double maxC = Math.Max(r, Math.Max(g, b));
                            double minC = Math.Min(r, Math.Min(g, b));
                            totalSat += maxC > 0 ? (maxC - minC) / maxC : 0;
                        }
                    }
                    double avgSat = totalSat / Math.Max(1, pixelCount);

                    // Penalize very dark/bright regions (black bars, white bg)
                    double brightnessPenalty = (avgBrightness < 20 || avgBrightness > 240) ? 0.3 : 1.0;

                    double contrastFactor = avgContrast < 40 ? avgContrast / 40.0 : 1.0 - (avgContrast - 40) / 120.0;
                    contrastFactor = Math.Clamp(contrastFactor, 0.1, 1.0);

                    // Center bias for saliency — strong bias to keep main subject centered and avoid pulling crop toward off-center decorative elements (text, logos)
                    float cx = (gx + 0.5f) / gridSize;
                    float cy = (gy + 0.5f) / gridSize;
                    float centerDist = MathF.Sqrt((cx - 0.5f) * (cx - 0.5f) + (cy - 0.5f) * (cy - 0.5f));
                    float centerBias = MathF.Max(0.1f, 1.0f - centerDist * 1.6f);

                    // Score: saturation (subject) + moderate contrast - text penalty
                    saliencyMap[gy, gx] = (float)((avgSat * 60.0 + contrastFactor * 20.0) * brightnessPenalty * centerBias);
                }
            }

            // Find the 2×2 block with highest saliency
            float bestScore = -1;
            int bestGx = gridSize / 2, bestGy = gridSize / 2;

            for (int gy = 0; gy < gridSize - 1; gy++)
            {
                for (int gx = 0; gx < gridSize - 1; gx++)
                {
                    float blockScore = saliencyMap[gy, gx] + saliencyMap[gy, gx + 1]
                                     + saliencyMap[gy + 1, gx] + saliencyMap[gy + 1, gx + 1];
                    if (blockScore > bestScore)
                    {
                        bestScore = blockScore;
                        bestGx = gx;
                        bestGy = gy;
                    }
                }
            }

            // Convert grid position to image coordinates
            float saliencyCenterX = (bestGx + 1.0f) / gridSize * imgWidth;
            float saliencyCenterY = (bestGy + 1.0f) / gridSize * imgHeight;

            return BuildCropRect(saliencyCenterX, saliencyCenterY, imgWidth, imgHeight, cropSize);

            } // end pixel processing try
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
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
        lock (_lock)
        {
            _cachedSession?.Dispose();
            _cachedSession = null;
        }
    }

    private struct Detection
    {
        public float X1, Y1, X2, Y2;
        public float Confidence;
        public int ClassId;
        public float Area => (X2 - X1) * (Y2 - Y1);
    }

    private struct TextRegion
    {
        public float X1, Y1, X2, Y2;
        public float EdgeDensity;
        public float FontFactor;
        public float Area => (X2 - X1) * (Y2 - Y1);
    }
}

public readonly record struct SubjectBounds(
    float CenterX,
    float CenterY,
    float Width,
    float Height,
    float Confidence,
    int ClassId);
