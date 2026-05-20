# ULinkActor

ULinkActor is a lightweight actor/mailbox runtime for .NET game servers, inspired by skynet.

It is not a full distributed actor platform such as Orleans, Akka.NET, or Proto.Actor. It focuses on single-process service runtime capabilities that are common in game servers:

- One actor owns one mailbox.
- Each actor processes messages sequentially.
- Actor state can usually be written without locks.
- Messaging is asynchronous.
- Timers are supported.
- Request/response workflows are supported.
- Bounded mailboxes provide backpressure.
- Diagnostics and tracing are exposed through standard .NET APIs.

v0.1 is complete. The current runtime package version is `0.1.2`.

---

# Use Cases

ULinkActor is suitable for long-lived stateful services in game servers, such as:

- RoomActor
- BattleActor
- MatchActor
- ChatActor
- GuildActor
- SessionActor
- WorldActor

The main benefit is that naturally sequential state can be kept inside actors. The mailbox guarantees ordered execution, so business code can avoid most shared-state concurrency concerns.

Recommended layering:

```text
ULinkActor        Lightweight mailbox runtime
ULinkRPC          Networking and RPC
ULinkGame.Core    Game server infrastructure
ULinkGame.Room    Room model
ULinkGame.MMO     Optional MMO template
Game-specific business logic
```

Related projects:

- [bruce48x/ULinkRPC](https://github.com/bruce48x/ULinkRPC)
- [bruce48x/ULinkGame](https://github.com/bruce48x/ULinkGame)

---

# Quick Start

Define a message and an actor:

```csharp
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

Start the actor system and send a message:

```csharp
ActorSystem system = new();
ActorRef room = system.Spawn(new RoomActor());

await room.Send(new JoinRoom(10001));
```

Use `Call<T>` when a response is required. Use `ActorContext` to schedule timers from inside an actor. Timer messages enter the same mailbox as normal messages, so they do not bypass the actor's sequential execution rule.

---

# Core Features

## Send

Fire-and-forget messaging. Useful for inputs, notifications, broadcasts, ticks, and similar workflows.

## Call<T>

Request/response messaging. Useful for querying actor state, requesting computed results, or waiting for actor-side work to complete.

## Timer

Timers are delivered as mailbox messages and are processed sequentially with normal actor messages.

## Typed Actor

In addition to `IActor`, ULinkActor supports `IActor<TMessage>` and `ActorRef<TMessage>` to reduce message casts in business code.

## Named Actor / Local Registry

Actors can be registered by name inside a local `ActorSystem` and resolved later by name.

## Actor Group

Multiple actors can be grouped locally for broadcast messaging or batch stop operations.

## Backpressure

Mailboxes support bounded capacity. When the queue reaches its capacity limit, senders observe backpressure.

## Metrics / Slow Message Detection

Mailbox metric snapshots can be read, and a slow message threshold can be configured to detect messages that take too long to process.

## Diagnostics / Tracing

ULinkActor exposes message dispatch tracing through standard .NET `ActivitySource`. It does not bind the runtime to a specific logger or APM platform.

## Source Generator

`ULinkActor.SourceGenerator` generates typed spawn extension methods for public `IActor<TMessage>` implementations, reducing repetitive boilerplate code.

---

# Non-Goals

The following are not part of ULinkActor Core:

- Cluster
- Remote Actor
- Virtual Actor
- Actor Persistence
- Event Sourcing
- Supervisor Tree
- MMO templates
- Gate / Realm / Map / AOI
- Unity integration
- Database abstractions
- ORM
- Network protocols
- Transport
- RPC

These concerns should be handled by [ULinkGame](https://github.com/bruce48x/ULinkGame), [ULinkRPC](https://github.com/bruce48x/ULinkRPC), or application code.

---

# Contributing

Project structure, design constraints, test coverage, and development conventions for framework contributors are documented in [CONTRIBUTING.md](./CONTRIBUTING.md).

---

# License

MIT
