namespace Hexor.Updates;

// Covers the entire check/prompt/install lifetime, including ShowDialog's nested dispatcher.
internal sealed class UpdateSession
{
    internal static readonly UpdateSession Current = new();
    readonly object sync = new();
    Task? active;
    bool startupAttempted;

    internal Task Run(bool startup, Func<Task> operation)
    {
        TaskCompletionSource completion;
        lock (sync)
        {
            if (active != null) return active;
            if (startup && startupAttempted) return Task.CompletedTask;
            startupAttempted = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            active = completion.Task;
        }
        _ = Execute();
        return completion.Task;

        async Task Execute()
        {
            Exception? failure = null;
            try { await operation(); }
            catch (Exception error) { failure = error; }
            finally
            {
                lock (sync)
                {
                    active = null;
                    if (failure == null) completion.TrySetResult();
                    else completion.TrySetException(failure);
                }
            }
        }
    }
}
