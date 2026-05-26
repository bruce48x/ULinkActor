namespace ULinkActor;

public sealed record DeadLetter(ActorId Target, object Message, string Reason);
