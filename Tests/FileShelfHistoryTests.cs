using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class FileShelfHistoryTests
{
    [Fact]
    public void RecordOperation_When51OperationsRecorded_UndoStartsWithNewestOperation()
    {
        var history = new FileShelfHistory();
        for (int i = 1; i <= 51; i++)
            history.RecordOperation(new TestOperation(i));

        var operation = Assert.IsType<TestOperation>(history.Undo());

        Assert.Equal(51, operation.Id);
        Assert.Equal(49, history.UndoCount);
    }

    [Fact]
    public void RecordOperation_AfterUndo_ClearsRedoStack()
    {
        var history = new FileShelfHistory();
        history.RecordOperation(new TestOperation(1));
        history.Undo();

        history.RecordOperation(new TestOperation(2));

        Assert.False(history.CanRedo);
        Assert.Equal(0, history.RedoCount);
    }

    private sealed class TestOperation(int id) : IFileShelfOperation
    {
        public int Id { get; } = id;
        public string Description => Id.ToString();
        public bool Undo(VNotch.Controllers.FileShelfController controller) => true;
        public bool Redo(VNotch.Controllers.FileShelfController controller) => true;
    }
}
