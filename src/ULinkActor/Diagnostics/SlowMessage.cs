namespace ULinkActor;

public sealed record SlowMessage(ActorId ActorId, object Message, TimeSpan Elapsed);
