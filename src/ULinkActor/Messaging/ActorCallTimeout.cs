namespace ULinkActor;

public enum ActorCallTimeoutReason
{
    ResponseTimeout = 0,
    QueueTimeout = 1
}

public sealed record ActorCallTimeout(
    ActorId? Caller,
    ActorId Target,
    object Request,
    TimeSpan Timeout,
    ActorCallTimeoutReason Reason,
    IReadOnlyList<ActorId> CallChain);
