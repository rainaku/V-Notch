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
    private const float ConfidenceThreshold = 0.50f;       // ≥ 0.5 to filter noise/false positives
    private const float PersonConfidenceThreshold = 0.25f; // Lower for person class (priority)
    private const float NmsThreshold = 0.45f;              // IoU ≤ 0.45 to merge overlapping bboxes
    private const float MinAreaRatio = 0.05f;              // ≥ 5% of frame area for non-person objects
    private const float MinPersonAreaRatio = 0.01f;        // Lower threshold for persons

    // ─── Person/Face Detection Weights ───
    private const float FaceWeight = 2.0f;                 // Face bbox importance multiplier
    private const float BodyWeight = 1.4f;                 // Body bbox fallback weight
    private const float HeadRoomPadding = 0.15f;           // +15% padding above head
    private const int MinFaceSize = 20;                    // Min 20x20px to filter false positives

    // ─── Text Detection Weights ───
    private const float TextWeight = 1.6f;                 // Text region importance multiplier
    private const float TextSafeZoneRatio = 0.80f;         // Crop must contain text if within 80%

    // ─── Portrait Detection ───
    private const float PortraitThreshold = 0.60f;         // Person > 60% frame = portrait mode

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
        if (_personClasses.Contains(classId)) return 3.0f;
        if (_productClasses.Contains(classId)) return 2.0f;
        if (_animalClasses.Contains(classId)) return 1.5f;
        return 1.0f;
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

    public Int32Rect? GetSmartCropRect(BitmapImage source, int targetSquareSize)
    {
        if (_disposed) return null;
        if (!_modelExists && !TryInitialize()) return null;
        if (!_modelExists) return null;

        lock (_lock)
        {
            float[]? tensorBuffer = null;

            try
            {
                int imgWidth = source.PixelWidth;
                int imgHeight = source.PixelHeight;

                // Skip if image is already square or very small
                if (Math.Abs(imgWidth - imgHeight) < 10 || imgWidth < 64 || imgHeight < 64)
                    return null;

                // For small images (< 400px wide), ONNX is overkill — use saliency directly
                if (imgWidth < 400 && imgHeight < 400)
                {
                    int maxCrop = Math.Min(imgWidth, imgHeight);
                    int cropSz = Math.Min(targetSquareSize, maxCrop);
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

                tensorBuffer = ArrayPool<float>.Shared.Rent(1 * 3 * ModelInputSize * ModelInputSize);

                // ─── Preprocess ───
                var (scaleX, scaleY, padX, padY) = PreprocessImageFast(source, tensorBuffer);

                // ─── Run inference ───
                var tensor = new DenseTensor<float>(tensorBuffer, new[] { 1, 3, ModelInputSize, ModelInputSize });
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
                    // No YOLO detections — use saliency/attention fallback
                    return GetSaliencyCropRect(source, imgWidth, imgHeight, targetSquareSize);
                }

                // ─── Multi-signal hybrid crop ───
                return GetHybridCropRect(detections, source, imgWidth, imgHeight, targetSquareSize);
            }
            catch (Exception ex)
            {
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
        var detections = new List<Detection>(16);
        var dims = output.Dimensions;
        int numPredictions = dims[2];
        float imgArea = imgWidth * imgHeight;

        for (int i = 0; i < numPredictions; i++)
        {
            float maxScore = PersonConfidenceThreshold;
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

            // Rule: Non-person classes must meet ≥ 0.5 confidence threshold
            if (!_personClasses.Contains(maxClassId) && maxScore < ConfidenceThreshold)
                continue;

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

            // Rule: Min area ≥ 5% for non-person, ≥ 1% for person
            if (_personClasses.Contains(maxClassId))
            {
                if (areaRatio < MinPersonAreaRatio) continue;
            }
            else
            {
                if (areaRatio < MinAreaRatio) continue;
            }

            if (x2 - x1 < 5 || y2 - y1 < 5) continue;

            detections.Add(new Detection
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Confidence = maxScore,
                ClassId = maxClassId
            });
        }

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
            byte[] pixels = new byte[h * stride];

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
        int cropSize = Math.Min(targetSize, maxCropSize);

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

        // ─── Person always wins — focus on people, ignore text ───
        if (persons.Count >= 1)
        {
            var largestPerson = persons.OrderByDescending(p => p.Area).First();
            float personAreaRatio = largestPerson.Area / imgArea;

            if (personAreaRatio >= PortraitThreshold)
                return GetPortraitCropRect(largestPerson, imgWidth, imgHeight, cropSize);

            return GetPersonCropRect(persons, imgWidth, imgHeight, cropSize);
        }

        // ─── No persons: focus on objects (not text) ───
        if (objects.Count > 0)
        {
            var bestObj = objects
                .Select(d => new
                {
                    Detection = d,
                    Score = d.Confidence * GetCenterWeight(d, imgWidth, imgHeight) * GetClassPriority(d.ClassId)
                })
                .OrderByDescending(x => x.Score)
                .First()
                .Detection;

            float objCenterX = (bestObj.X1 + bestObj.X2) / 2f;
            float objCenterY = (bestObj.Y1 + bestObj.Y2) / 2f;

            return BuildCropRect(objCenterX, objCenterY, imgWidth, imgHeight, cropSize);
        }

        // ─── Fallback: saliency (subject-focused, not text-focused) ───
        return GetSaliencyCropRect(source, imgWidth, imgHeight, targetSize)
               ?? BuildCropRect(imgWidth / 2f, imgHeight / 2f, imgWidth, imgHeight, cropSize);
    }

    private Int32Rect GetPortraitCropRect(Detection person, int imgWidth, int imgHeight, int cropSize)
    {
        float personHeight = person.Y2 - person.Y1;
        float personWidth = person.X2 - person.X1;

        // Estimate face region: upper 30% of person bbox
        float faceY1 = person.Y1;
        float faceY2 = person.Y1 + personHeight * 0.30f;
        float faceHeight = faceY2 - faceY1;

        // Rule: Head-room padding +15% above
        float headRoom = faceHeight * HeadRoomPadding;
        float targetCenterY = faceY1 - headRoom + (faceY2 - faceY1 + headRoom) / 2f;

        // Center X on person
        float targetCenterX = (person.X1 + person.X2) / 2f;

        return BuildCropRect(targetCenterX, targetCenterY, imgWidth, imgHeight, cropSize);
    }

    private Int32Rect GetPersonCropRect(List<Detection> persons, int imgWidth, int imgHeight, int cropSize)
    {
        if (persons.Count == 1)
        {
            var p = persons[0];
            float personHeight = p.Y2 - p.Y1;
            float personWidth = p.X2 - p.X1;

            // Check if face-sized (min 20×20px rule)
            bool hasFace = personWidth >= MinFaceSize && personHeight >= MinFaceSize;
            float weight = hasFace ? FaceWeight : BodyWeight;

            // Face bias: upper 35% of person bbox
            float faceBiasY = p.Y1 + personHeight * 0.35f;

            // Rule: Head-room padding +15% above
            float headRoom = personHeight * 0.30f * HeadRoomPadding;
            float targetY = faceBiasY - headRoom;

            float targetX = (p.X1 + p.X2) / 2f;

            return BuildCropRect(targetX, targetY, imgWidth, imgHeight, cropSize);
        }
        else
        {
            // Multiple persons: union bbox (don't miss anyone)
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;

            foreach (var p in persons)
            {
                if (p.X1 < minX) minX = p.X1;
                if (p.X2 > maxX) maxX = p.X2;
                if (p.Y1 < minY) minY = p.Y1;
                if (p.Y2 > maxY) maxY = p.Y2;
            }

            // Center on union bbox, bias toward top (faces)
            float unionCenterX = (minX + maxX) / 2f;
            float unionHeight = maxY - minY;

            // Head-room +15% above the top-most person
            float headRoom = unionHeight * HeadRoomPadding;
            float targetY = minY - headRoom + unionHeight * 0.35f;

            return BuildCropRect(unionCenterX, targetY, imgWidth, imgHeight, cropSize);
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
            byte[] pixels = new byte[h * stride];

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

                    // Penalize very high contrast (text regions have sharp edges)
                    // Prefer moderate contrast (subjects) over extreme contrast (text)
                    double contrastFactor = avgContrast < 40 ? avgContrast / 40.0 : 1.0 - (avgContrast - 40) / 200.0;
                    contrastFactor = Math.Clamp(contrastFactor, 0.2, 1.0);

                    // Center bias for saliency
                    float cx = (gx + 0.5f) / gridSize;
                    float cy = (gy + 0.5f) / gridSize;
                    float centerDist = MathF.Sqrt((cx - 0.5f) * (cx - 0.5f) + (cy - 0.5f) * (cy - 0.5f));
                    float centerBias = 1.0f - centerDist * 0.6f;

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
