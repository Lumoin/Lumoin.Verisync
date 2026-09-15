using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// A synchronization context that counts the continuations posted to it and sent through it separately,
/// and runs each one on the thread pool, so a count above zero is a continuation that came back to the
/// context a loop or a write was started on.
/// </summary>
internal sealed class PostCountingSynchronizationContext: SynchronizationContext
{
    private int posts;
    private int sends;


    /// <summary>The number of continuations posted to this context so far.</summary>
    public int Posts => Volatile.Read(ref posts);


    /// <summary>The number of continuations sent to this context so far.</summary>
    public int Sends => Volatile.Read(ref sends);


    /// <inheritdoc/>
    public override void Post(SendOrPostCallback d, object? state)
    {
        _ = Interlocked.Increment(ref posts);
        base.Post(d, state);
    }


    /// <inheritdoc/>
    public override void Send(SendOrPostCallback d, object? state)
    {
        _ = Interlocked.Increment(ref sends);
        d(state);
    }


    /// <summary>
    /// Starts <paramref name="start"/> with this context current and restores the caller's context before
    /// returning, so a continuation reaches this context only by having captured it while the call ran
    /// synchronously.
    /// </summary>
    /// <typeparam name="TTask">The task type <paramref name="start"/> returns.</typeparam>
    /// <param name="start">The call to start with this context current.</param>
    /// <returns>The task <paramref name="start"/> returned.</returns>
    public TTask Start<TTask>(StartUnderContextDelegate<TTask> start) where TTask : Task
    {
        SynchronizationContext? caller = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(this);
        try
        {
            return start();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(caller);
        }
    }
}
