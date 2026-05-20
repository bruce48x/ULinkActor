namespace ULinkActor;

public sealed class ActorContext
{
    private readonly ActorCell cell;
    private readonly Envelope envelope;

    internal ActorContext(ActorSystem system, ActorRef self, ActorCell cell, Envelope envelope)
    {
        System = system;
        Self = self;
        this.cell = cell;
        this.envelope = envelope;
    }

    public ActorSystem System { get; }

    public ActorRef Self { get; }

    public bool HasPendingResponse => envelope.Response is not null;

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

        ActorTimer timer = new(Self, message, dueTime, period);
        cell.AddTimer(timer);
        return timer;
    }
}
