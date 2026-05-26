namespace ULinkActor.Messaging;

internal sealed record ActorCallContext(ActorId ActorId, IReadOnlyList<ActorId> CallChain);
