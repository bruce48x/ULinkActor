using ULinkActor.Messaging;

namespace ULinkActor.Abstractions;

internal interface IActor
{
    ValueTask OnMessage(ActorContextCore ctx, object message);

    ValueTask OnStarted(ActorContextCore ctx);

    ValueTask OnStopping(ActorContextCore ctx);
}
