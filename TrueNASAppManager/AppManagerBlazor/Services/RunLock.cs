namespace TrueNasAppManager.Services;

public sealed class RunLock
{
    private readonly SemaphoreSlim semaphore = new(1, 1);

    public bool IsHeld => semaphore.CurrentCount == 0;

    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        if (!await semaphore.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return null;
        }

        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private int released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                semaphore.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
