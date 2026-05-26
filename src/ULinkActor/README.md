# ULinkActor

`ULinkActor` is a lightweight actor/mailbox runtime for .NET game servers.

It focuses on single-process service runtime scenarios where long-lived state objects should process messages sequentially without locks.

## Install

```xml
<ItemGroup>
  <PackageReference Include="ULinkActor" Version="0.2.0" />
</ItemGroup>
```

## Quick Start

Define a message family and an actor:

```csharp
using ULinkActor;

public abstract record RoomMessage;

public sealed record JoinRoom(long PlayerId) : RoomMessage;

public sealed class RoomActor : IActor<RoomMessage>
{
    private readonly HashSet<long> players = new();

    public ValueTask OnMessage(ActorContext<RoomMessage> ctx, RoomMessage message)
    {
        if (message is JoinRoom join)
        {
            players.Add(join.PlayerId);
        }

        return ValueTask.CompletedTask;
    }
}
```

Spawn the actor and send messages:

```csharp
using ULinkActor;

using ActorSystem system = new();

ActorRef<RoomMessage> room = system.Spawn<RoomMessage>(new RoomActor());

await room.Send(new JoinRoom(10001));
```

## Request / Response

Use `Call<T>` when the sender needs a response:

```csharp
public sealed record GetPlayerCount : RoomMessage;

public sealed class RoomActor : IActor<RoomMessage>
{
    private readonly HashSet<long> players = new();

    public ValueTask OnMessage(ActorContext<RoomMessage> ctx, RoomMessage message)
    {
        if (message is GetPlayerCount)
        {
            ctx.Respond(players.Count);
        }

        return ValueTask.CompletedTask;
    }
}

int count = await room.Call<int>(new GetPlayerCount(), TimeSpan.FromSeconds(1));
```

Generated typed spawn extension methods are included with the `ULinkActor` package as compile-time source generator output.

Interfaces marked with `[ActorClient]` can also get generated client proxies that lower `ValueTask` methods into `Send` and `ValueTask<T>` methods into `Call<T>`.

The package also includes compile-time analyzer warnings for actor self-calls, blocking waits inside actor types, common blocking APIs such as `Thread.Sleep`, and discarded `Call<T>` request results.

## Core Features

- One actor owns one mailbox.
- Messages are processed sequentially inside each actor.
- `Send` supports fire-and-forget messaging.
- `Call<T>` supports request/response workflows.
- Timer messages enter the actor mailbox and follow the same sequential execution rule.
- Optional `IActorStarted<TMessage>` and `IActorStopping<TMessage>` hooks support local startup and graceful stop work.
- Bounded mailbox capacity provides backpressure.
- Named actors can be registered and resolved inside an `ActorSystem`; lookup validates the expected message type.
- Mailbox metrics, slow message detection, dead letters, and .NET `ActivitySource` tracing are available for diagnostics.

## Non-Goals

`ULinkActor` is not a distributed actor platform. It does not provide cluster, remote actor, virtual actor, persistence, event sourcing, supervisor tree, networking, RPC, database, ORM, Unity integration, or MMO template features.

Those concerns should live in higher-level infrastructure or application code.

## License

MIT
