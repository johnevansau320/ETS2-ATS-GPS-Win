namespace Ets2Gps;

/// <summary>Takes turns with the GPS app on the shared named semaphore, so the two never talk over each other.</summary>
public sealed class SpeakingTurn : IDisposable
{
    public const string Name = @"Local\ETS2ATS-Speaking";

    readonly Semaphore _sem = new(1, 1, Name);
    readonly object _gate = new();
    bool _held;

    /// <summary>Waits on a thread-pool thread; false after the wait (the caller speaks anyway and must not Release).</summary>
    public Task<bool> AcquireAsync(TimeSpan wait, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
            // poll in short slices so cancellation is honoured without leaving a half-taken semaphore
            while (!ct.IsCancellationRequested)
            {
                var left = Math.Max(0, deadline - Environment.TickCount64);
                bool got;
                try { got = _sem.WaitOne((int)Math.Min(left, 100)); }
                catch (AbandonedMutexException) { got = true; }
                if (got)
                {
                    lock (_gate) _held = true;
                    return true;
                }
                if (left <= 0) return false;
            }
            return false;
        });
    }

    /// <summary>Safe at any time: only releases after a successful acquire, and only once.</summary>
    public void Release()
    {
        lock (_gate)
        {
            if (!_held) return;
            _held = false;
            try { _sem.Release(); } catch (SemaphoreFullException) { }
        }
    }

    public void Dispose()
    {
        Release();
        _sem.Dispose();
    }
}
