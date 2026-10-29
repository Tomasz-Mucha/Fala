using Microsoft.UI.Dispatching;

namespace Fala.Helpers;

public static class UiThread
{
    public static Task RunAsync(DispatcherQueue queue, Action action)
    {
        if (queue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }))
        {
            tcs.SetException(new InvalidOperationException("Nie udało się wrócić na wątek UI."));
        }

        return tcs.Task;
    }
}
