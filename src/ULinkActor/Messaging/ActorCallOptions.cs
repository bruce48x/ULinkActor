namespace ULinkActor;

public sealed record ActorCallOptions(TimeSpan QueueTimeout, TimeSpan ResponseTimeout);
