namespace ULinkActor;

public interface IActor
{
    ValueTask OnMessage(ActorContext ctx, object message);
}

public interface IActor<in TMessage>
{
    ValueTask OnMessage(ActorContext ctx, TMessage message);
}
