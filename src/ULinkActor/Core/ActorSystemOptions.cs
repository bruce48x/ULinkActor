namespace ULinkActor;

public sealed class ActorSystemOptions
{
    public int MailboxCapacity { get; init; } = 1024;

    public TimeSpan? SlowMessageThreshold { get; init; }
}
