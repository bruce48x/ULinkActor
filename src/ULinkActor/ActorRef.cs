using System.Diagnostics;

namespace ULinkActor;

internal sealed class ActorRef
{
    private readonly ActorSystem system;

    internal ActorRef(ActorSystem system, ActorId id)
    {
        this.system = system;
        Id = id;
    }

    public ActorId Id { get; }

    public ValueTask Send(object message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        return system.Send(Id, message, cancellationToken);
    }

    internal ValueTask Send(
        object message,
        ActivityContext parentActivityContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        return system.Send(Id, message, parentActivityContext, cancellationToken);
    }

    public ActorSendResult TrySend(object message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return system.TrySend(Id, message);
    }

    public ValueTask<TResponse> Call<TResponse>(
        object request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return system.Call<TResponse>(Id, request, timeout, cancellationToken);
    }

    public ValueTask Stop()
    {
        return system.Stop(Id);
    }

    public ValueTask<ActorStopResult> Stop(TimeSpan drainTimeout)
    {
        return system.Stop(Id, drainTimeout);
    }

    public MailboxMetrics GetMailboxMetrics()
    {
        return system.GetMailboxMetrics(Id);
    }

    public override string ToString() => Id.ToString();
}

public sealed class ActorRef<TMessage>
{
    private readonly ActorRef inner;

    internal ActorRef(ActorRef inner)
    {
        this.inner = inner;
    }

    public ActorId Id => inner.Id;

    public ValueTask Send(TMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        return inner.Send(message, cancellationToken);
    }

    public ActorSendResult TrySend(TMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return inner.TrySend(message);
    }

    public ValueTask<TResponse> Call<TResponse>(
        TMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return inner.Call<TResponse>(request, timeout, cancellationToken);
    }

    public ValueTask Stop()
    {
        return inner.Stop();
    }

    public ValueTask<ActorStopResult> Stop(TimeSpan drainTimeout)
    {
        return inner.Stop(drainTimeout);
    }

    public MailboxMetrics GetMailboxMetrics()
    {
        return inner.GetMailboxMetrics();
    }

    public override string ToString() => inner.ToString();
}
