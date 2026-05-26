namespace ULinkActor;

internal sealed record ActorCallContext(ActorId ActorId, IReadOnlyList<ActorId> CallChain);
