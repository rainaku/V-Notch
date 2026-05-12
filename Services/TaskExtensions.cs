namespace VNotch.Services;

/// <summary>
/// Extension methods for safe fire-and-forget task execution.
/// Ensures unobserved exceptions are logged rather than silently swallowed.
/// </summary>
public static class TaskExtensions
{
    /// <summary>
    /// Safely fire-and-forget a task. Any unhandled exception is logged via RuntimeLog.
    /// Use this instead of <c>_ = SomeAsync()</c> to make intent explicit.
    /// </summary>
    /// <param name="task">The task to execute.</param>
    /// <param name="category">Log category for any exceptions (defaults to "FIRE-FORGET").</param>
    public static async void SafeFireAndForget(this Task task, string category = "FIRE-FORGET")
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown — don't log
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(category, $"Unhandled exception in fire-and-forget task: {ex}");
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[{category}] Fire-and-forget exception: {ex.Message}");
#endif
        }
    }

    /// <summary>
    /// Safely fire-and-forget a task with a custom error handler.
    /// </summary>
    public static async void SafeFireAndForget(this Task task, Action<Exception> onError)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            onError(ex);
        }
    }
}
