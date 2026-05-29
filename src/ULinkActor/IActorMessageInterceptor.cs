namespace ULinkActor;

public interface IActorMessageInterceptor
{
    ValueTask OnBeforeMessage(ActorId actorId, object message, CancellationToken cancellationToken);

    ValueTask OnAfterMessage(ActorId actorId, object message, Exception? error, CancellationToken cancellationToken);
}
