using System.Collections.Concurrent;

namespace ULinkActor.Core;

internal sealed class ActorTimerSet
{
    private readonly ConcurrentBag<IDisposable> timers = new();

    internal void Add(IDisposable timer)
    {
        timers.Add(timer);
    }

    internal void DisposeAll()
    {
        foreach (IDisposable timer in timers)
        {
            timer.Dispose();
        }
    }
}
