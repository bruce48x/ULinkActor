# ULinkActor

`ULinkActor` is a lightweight actor/mailbox runtime for .NET game servers.

It focuses on single-process service runtime scenarios where long-lived state objects should process messages sequentially without locks.

## Install

```xml
<ItemGroup>
  <PackageReference Include="ULinkActor" Version="0.1.2" />
</ItemGroup>
```

## Quick Start

Define a message and an actor:

```csharp
using ULinkActor;

public readonly record struct JoinRoom(long PlayerId);

public sealed class RoomActor : IActor
{
    private readonly HashSet<long> players = new();

    public ValueTask OnMessage(ActorContext ctx, object message)
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

ActorRef room = system.Spawn(new RoomActor());

await room.Send(new JoinRoom(10001));
```

## Request / Response

Use `Call<T>` when the sender needs a response:

```csharp
public readonly record struct GetPlayerCount;

public sealed class RoomActor : IActor
{
    private readonly HashSet<long> players = new();

    public ValueTask OnMessage(ActorContext ctx, object message)
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

## Typed Actors

Use `IActor<TMessage>` and `ActorRef<TMessage>` when you want message type checking at the call site:

```csharp
public abstract record RoomMessage;

public sealed record JoinRoom(long PlayerId) : RoomMessage;

public sealed class RoomActor : IActor<RoomMessage>
{
    private readonly HashSet<long> players = new();

    public ValueTask OnMessage(ActorContext ctx, RoomMessage message)
    {
        if (message is JoinRoom join)
        {
            players.Add(join.PlayerId);
        }

        return ValueTask.CompletedTask;
    }
}

ActorRef<RoomMessage> room = system.Spawn<RoomMessage>(new RoomActor());

await room.Send(new JoinRoom(10001));
```

For generated typed spawn extension methods, install `ULinkActor.SourceGenerator`.

## Core Features

- One actor owns one mailbox.
- Messages are processed sequentially inside each actor.
- `Send` supports fire-and-forget messaging.
- `Call<T>` supports request/response workflows.
- Timer messages enter the actor mailbox and follow the same sequential execution rule.
- Bounded mailbox capacity provides backpressure.
- Named actors can be registered and resolved inside an `ActorSystem`.
- Actor groups support local broadcast and batch stop operations.
- Mailbox metrics, slow message detection, dead letters, and .NET `ActivitySource` tracing are available for diagnostics.

## Non-Goals

`ULinkActor` is not a distributed actor platform. It does not provide cluster, remote actor, virtual actor, persistence, event sourcing, supervisor tree, networking, RPC, database, ORM, Unity integration, or MMO template features.

Those concerns should live in higher-level infrastructure or application code.

## License

MIT
