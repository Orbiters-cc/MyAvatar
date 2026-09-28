using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orbiters.MyAvatar.Editor
{
    // Shared by all inspectors. Writes finish in click order; only the latest choice publishes a result.
    internal sealed class TextureAiPreferences
    {
        private readonly Func<string, bool, Func<CancellationToken, Task<bool>>> prepare;
        private Task tail = Task.CompletedTask;
        private string latestToken;
        private bool latestChoice;
        private int context;
        internal long Revision { get; private set; }
        internal event Action<string, bool> Changed;

        internal TextureAiPreferences(Func<string, bool, Func<CancellationToken, Task<bool>>> prepare) { this.prepare = prepare; }

        internal bool TryGetPendingChoice(string token, out bool enabled)
        {
            enabled = latestChoice;
            return !tail.IsCompleted && token == latestToken;
        }

        internal void InvalidateContext() { context++; Revision++; latestToken = null; }

        // Called on Unity's editor thread, including optimistic notifications and response handling.
        internal Task<bool> SetAsync(string token, bool enabled, CancellationToken cancellation = default)
        {
            long revision = ++Revision;
            latestToken = token; latestChoice = enabled;
            // Capture the account and endpoint now, before a queued write can outlive an environment switch.
            var write = prepare(token, enabled);
            Task previous = tail;
            var completion = new TaskCompletionSource<bool>();
            tail = completion.Task;
            Changed?.Invoke(token, enabled);
            _ = CompleteAsync(previous, completion, revision, context, token, write, cancellation);
            return completion.Task;
        }

        private async Task CompleteAsync(Task previous, TaskCompletionSource<bool> completion, long revision, int startedContext,
            string token, Func<CancellationToken, Task<bool>> write, CancellationToken cancellation)
        {
            try
            {
                try { await previous; } catch (Exception) { /* A failed write must not block later choices. */ }
                cancellation.ThrowIfCancellationRequested();
                if (startedContext != context) throw new OperationCanceledException();
                bool saved = await write(cancellation);
                if (revision == Revision) { latestChoice = saved; Changed?.Invoke(token, saved); }
                completion.TrySetResult(saved);
            }
            catch (Exception ex)
            {
                // Keep local AI paused when the account setting could not be saved.
                if (revision == Revision) { latestChoice = false; Changed?.Invoke(token, false); }
                completion.TrySetException(ex);
            }
        }
    }
}
