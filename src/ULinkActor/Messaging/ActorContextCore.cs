using System.Diagnostics;
using ULinkActor.Core;
using ULinkActor.Timers;

namespace ULinkActor.Messaging;

internal sealed class ActorContextCore
{
    private readonly ActorCell cell;
    private readonly Envelope envelope;

    internal ActorContextCore(ActorRef self, ActorCell cell, Envelope envelope)
    {
        Self = self;
        this.cell = cell;
        this.envelope = envelope;
    }

    internal ActorRef Self { get; }

    internal bool HasPendingResponse => envelope.Response is not null;

    public void Respond<TResponse>(TResponse response)
    {
        if (!TryRespond(response))
        {
            throw new InvalidOperationException("The current message does not have a pending response or was already completed.");
        }
    }

    public bool TryRespond<TResponse>(TResponse response)
    {
        return envelope.Response?.TrySetResult(response) == true;
    }

    public IDisposable ScheduleOnce(object message, TimeSpan dueTime)
    {
        ArgumentNullException.ThrowIfNull(message);

        return ScheduleRepeated(message, dueTime, Timeout.InfiniteTimeSpan);
    }

    public IDisposable ScheduleRepeated(object message, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(message);

        ActorTimer timer = new(Self, message, dueTime, period, Activity.Current?.Context ?? default);
        cell.AddTimer(timer);
        return timer;
    }
}
