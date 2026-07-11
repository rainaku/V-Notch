using System.IO;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

public sealed class FileShelfControllerHistoryTests : IDisposable
{
    private readonly string _filePath = Path.Combine(Path.GetTempPath(), $"vnotch-history-{Guid.NewGuid():N}.txt");

    public FileShelfControllerHistoryTests()
    {
        File.WriteAllText(_filePath, "test");
    }

    [Fact]
    public void AddThenUndoThenRedo_RestoresShelfState()
    {
        using var controller = CreateController();
        controller.EnqueueFiles(new[] { _filePath });

        Assert.Single(controller.Files);
        Assert.True(controller.UndoLastOperation());
        Assert.Empty(controller.Files);
        Assert.True(controller.RedoLastOperation());
        Assert.Single(controller.Files);
    }

    [Fact]
    public void RemovePinnedFile_DoesNotCreateRemovalOperation()
    {
        using var controller = CreateController();
        controller.EnqueueFiles(new[] { _filePath });
        Assert.True(controller.TogglePin(_filePath));

        Assert.False(controller.RemoveFile(_filePath));
        Assert.True(controller.UndoLastOperation());

        Assert.False(controller.IsPinned(_filePath));
        Assert.Single(controller.Files);
    }

    [Fact]
    public void FailedOperation_DoesNotDisplacePreviousHistoryEntry()
    {
        using var controller = CreateController();
        controller.EnqueueFiles(new[] { _filePath });

        Assert.False(controller.RemoveFile(_filePath + ".missing"));
        Assert.True(controller.UndoLastOperation());
        Assert.Empty(controller.Files);
    }

    public void Dispose()
    {
        if (File.Exists(_filePath)) File.Delete(_filePath);
    }

    private static FileShelfController CreateController() =>
        new(new NotchSettings { IsShelfUploadLimitUnlocked = true }, new FakeSettingsService());
}
