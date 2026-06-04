using System.Diagnostics;
using ULinkActor.Core;
using ULinkActor.Timers;

namespace ULinkActor.Messaging;

internal sealed class ActorContextCore
{
    private readonly ActorCell cell;
    private readonly ActorResponseSlot responseSlot;

    internal ActorContextCore(ActorRef self, ActorCell cell, Envelope envelope)
    {
        Self = self;
        this.cell = cell;
        responseSlot = new ActorResponseSlot(envelope.Response);
    }

    internal ActorRef Self { get; }

    internal bool HasPendingResponse => responseSlot.HasPendingResponse;

    public void Respond<TResponse>(TResponse response)
    {
        responseSlot.Respond(response);
    }

    public bool TryRespond<TResponse>(TResponse response)
    {
        return responseSlot.TryRespond(response);
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
        timer.Start();
        return timer;
    }
}
