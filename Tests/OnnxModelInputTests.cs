using System;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace VNotch.Tests;

public sealed class OnnxModelInputTests
{
    [Fact]
    public void SmartCropModel_UsesOptimized416InputAndExpectedOutput()
    {
        string modelPath = Path.Combine(AppContext.BaseDirectory, "Models", "yolov8n.onnx");
        Assert.True(File.Exists(modelPath), $"Model was not copied to {modelPath}");

        using var session = new InferenceSession(modelPath);
        int[] inputDimensions = session.InputMetadata.Single().Value.Dimensions;
        int[] outputDimensions = session.OutputMetadata.Single().Value.Dimensions;

        Assert.Equal(new[] { 1, 3, 416, 416 }, inputDimensions);
        Assert.Equal(new[] { 1, 84, 3549 }, outputDimensions);
    }
}
