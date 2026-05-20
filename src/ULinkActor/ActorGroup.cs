namespace ULinkActor;

internal sealed class ActorGroup
{
    private readonly ActorRef[] members;

    internal ActorGroup(IEnumerable<ActorRef> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        this.members = members
            .DistinctBy(actorRef => actorRef.Id)
            .ToArray();
    }

    internal int Count => members.Length;

    internal IReadOnlyList<ActorRef> Members => members;

    public async ValueTask Send(object message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        Task[] sends = members
            .Select(actorRef => actorRef.Send(message, cancellationToken).AsTask())
            .ToArray();

        await Task.WhenAll(sends).ConfigureAwait(false);
    }

    public async ValueTask Stop()
    {
        Task[] stops = members
            .Select(actorRef => actorRef.Stop().AsTask())
            .ToArray();

        await Task.WhenAll(stops).ConfigureAwait(false);
    }

    public IReadOnlyList<MailboxMetrics> GetMailboxMetrics()
    {
        return members
            .Select(actorRef => actorRef.GetMailboxMetrics())
            .ToArray();
    }
}

public sealed class ActorGroup<TMessage>
{
    private readonly ActorRef<TMessage>[] members;

    internal ActorGroup(IEnumerable<ActorRef<TMessage>> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        this.members = members
            .DistinctBy(actorRef => actorRef.Id)
            .ToArray();
    }

    public int Count => members.Length;

    public IReadOnlyList<ActorRef<TMessage>> Members => members;

    public async ValueTask Send(TMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        Task[] sends = members
            .Select(actorRef => actorRef.Send(message, cancellationToken).AsTask())
            .ToArray();

        await Task.WhenAll(sends).ConfigureAwait(false);
    }

    public async ValueTask Stop()
    {
        Task[] stops = members
            .Select(actorRef => actorRef.Stop().AsTask())
            .ToArray();

        await Task.WhenAll(stops).ConfigureAwait(false);
    }

    public IReadOnlyList<MailboxMetrics> GetMailboxMetrics()
    {
        return members
            .Select(actorRef => actorRef.GetMailboxMetrics())
            .ToArray();
    }
}
