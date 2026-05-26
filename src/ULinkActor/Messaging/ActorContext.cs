using System.Diagnostics;

namespace ULinkActor;

internal sealed class ActorContextCore
{
    private readonly ActorCell cell;
    private readonly Envelope envelope;

    internal ActorContextCore(ActorSystem system, ActorRef self, ActorCell cell, Envelope envelope)
    {
        System = system;
        Self = self;
        this.cell = cell;
        this.envelope = envelope;
    }

    internal ActorSystem System { get; }

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

public sealed class ActorContext<TMessage>
{
    private readonly ActorContextCore inner;

    internal ActorContext(ActorContextCore inner)
    {
        this.inner = inner;
        Self = new ActorRef<TMessage>(inner.Self);
    }

    public ActorSystem System => inner.System;

    public ActorRef<TMessage> Self { get; }

    public bool HasPendingResponse => inner.HasPendingResponse;

    public void Respond<TResponse>(TResponse response)
    {
        inner.Respond(response);
    }

    public bool TryRespond<TResponse>(TResponse response)
    {
        return inner.TryRespond(response);
    }

    public IDisposable ScheduleOnce(TMessage message, TimeSpan dueTime)
    {
        ArgumentNullException.ThrowIfNull(message);

        return inner.ScheduleOnce(message, dueTime);
    }

    public IDisposable ScheduleRepeated(TMessage message, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(message);

        return inner.ScheduleRepeated(message, dueTime, period);
    }
}
