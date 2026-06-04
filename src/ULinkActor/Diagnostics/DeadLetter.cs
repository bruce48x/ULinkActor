namespace ULinkActor;

public sealed record DeadLetter(ActorId Target, string MessageType, string Reason);
