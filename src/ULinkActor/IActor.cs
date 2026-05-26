namespace ULinkActor;

internal interface IActor
{
    ValueTask OnMessage(ActorContextCore ctx, object message);

    ValueTask OnStarted(ActorContextCore ctx);

    ValueTask OnStopping(ActorContextCore ctx);
}

public interface IActor<TMessage>
{
    ValueTask OnMessage(ActorContext<TMessage> ctx, TMessage message);
}

public interface IActorStarted<TMessage>
{
    ValueTask OnStarted(ActorContext<TMessage> ctx);
}

public interface IActorStopping<TMessage>
{
    ValueTask OnStopping(ActorContext<TMessage> ctx);
}
