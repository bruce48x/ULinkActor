namespace ULinkActor;

public sealed class ActorRef
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

    public ActorRef Untyped => inner;

    public ValueTask Send(TMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        return inner.Send(message, cancellationToken);
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

    public MailboxMetrics GetMailboxMetrics()
    {
        return inner.GetMailboxMetrics();
    }

    public override string ToString() => inner.ToString();
}
