namespace ULinkActor;

internal interface IActor
{
    ValueTask OnMessage(ActorContextCore ctx, object message);
}

public interface IActor<TMessage>
{
    ValueTask OnMessage(ActorContext<TMessage> ctx, TMessage message);
}
