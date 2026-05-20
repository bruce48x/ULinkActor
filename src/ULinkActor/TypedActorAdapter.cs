namespace ULinkActor;

internal sealed class TypedActorAdapter<TMessage> : IActor
{
    private readonly IActor<TMessage> actor;

    public TypedActorAdapter(IActor<TMessage> actor)
    {
        this.actor = actor;
    }

    public ValueTask OnMessage(ActorContextCore ctx, object message)
    {
        if (message is TMessage typedMessage)
        {
            return actor.OnMessage(new ActorContext<TMessage>(ctx), typedMessage);
        }

        throw new InvalidOperationException(
            $"Actor {ctx.Self.Id} expected message type {typeof(TMessage).FullName}, but received {message.GetType().FullName}.");
    }
}
