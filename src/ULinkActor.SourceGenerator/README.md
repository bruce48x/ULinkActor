# ULinkActor.SourceGenerator

`ULinkActor.SourceGenerator` generates typed `ActorSystem` spawn extension methods for public ULinkActor typed actors and actor client proxies for `[ActorClient]` interfaces.

It is intended to reduce repeated `Spawn<TMessage>(...)` calls when your project defines actors that implement `IActor<TMessage>`.

The generator is a compile-time convenience layer. It must emit ordinary C# that calls the public ULinkActor runtime APIs, and it must not require runtime reflection, dynamic proxies, or dynamic method invocation.

This project is an internal compile-time project for the main `ULinkActor` package. It is not independently packable and should not be published as a standalone NuGet package.

## Install

Install the runtime package:

```xml
<ItemGroup>
  <PackageReference Include="ULinkActor" Version="0.1.8" />
</ItemGroup>
```

`ULinkActor` packages the generator as an analyzer asset, so applications do not need a separate source generator package reference and do not get extra runtime dependencies.

## Usage

Define a public typed actor:

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

The generator creates a typed spawn extension method:

```csharp
using ULinkActor;

using ActorSystem system = new();

ActorRef<RoomMessage> room = system.SpawnRoomActor(new RoomActor());

await room.Send(new JoinRoom(10001));
```

You can also register the actor by name:

```csharp
ActorRef<RoomMessage> room = system.SpawnRoomActor("room-1", new RoomActor());
```

## Generated Method Shape

For a public actor named `RoomActor` that implements `IActor<RoomMessage>`, the generator emits:

```csharp
ActorRef<RoomMessage> SpawnRoomActor(this ActorSystem system, RoomActor actor, ActorSpawnOptions? options = null);

ActorRef<RoomMessage> SpawnRoomActor(this ActorSystem system, string name, RoomActor actor, ActorSpawnOptions? options = null);
```

## Generation Rules

- The actor class must be `public`.
- The actor class must implement `ULinkActor.IActor<TMessage>`.
- Generic actor classes are ignored.
- The generated extension methods are emitted into the `ULinkActor` namespace.

## Actor Client Proxy

Mark a public interface with `[ActorClient]` to generate a small client wrapper:

```csharp
using ULinkActor;

[ActorClient]
public interface IRoomActorClient
{
    ValueTask Join(long playerId);

    ValueTask<int> GetPlayerCount();
}
```

The generator emits a public client message interface, public request records that implement it, and an `ActorRef<TMessage>` extension method:

```csharp
ActorRef<RoomActorClientMessage> roomActor = system.Spawn<RoomActorClientMessage>(new RoomActor());
IRoomActorClient room = roomActor.AsRoomActorClient(TimeSpan.FromSeconds(1));

await room.Join(10001);
int count = await room.GetPlayerCount();
```

The generated wrapper lowers:

- `ValueTask` methods into `ActorRef<TMessage>.Send(...)`.
- `ValueTask<T>` methods into `ActorRef<TMessage>.Call<T>(..., callTimeout)`.

The actor handles the generated request record types:

```csharp
internal sealed class RoomActor : IActor<RoomActorClientMessage>
{
    private readonly HashSet<long> players = new();

    public ValueTask OnMessage(ActorContext<RoomActorClientMessage> ctx, RoomActorClientMessage message)
    {
        switch (message)
        {
            case RoomActorClientJoinRequest join:
                players.Add(join.PlayerId);
                break;
            case RoomActorClientGetPlayerCountRequest:
                ctx.Respond(players.Count);
                break;
        }

        return ValueTask.CompletedTask;
    }
}
```

First-version limitations:

- The interface must be `public`.
- Generic interfaces and generic methods are not supported.
- Method overloads are not supported.
- `ref`, `out`, and `in` parameters are not supported.
- Methods should return `ValueTask` or `ValueTask<T>`.

Unsupported shapes are reported as compile-time diagnostics instead of falling back to runtime reflection or dynamic dispatch:

| Rule | Severity | Description |
| --- | --- | --- |
| `ULA101` | Error | Reports non-public `[ActorClient]` interfaces. |
| `ULA102` | Error | Reports generic `[ActorClient]` interfaces. |
| `ULA103` | Error | Reports overloaded actor client methods. |
| `ULA104` | Error | Reports actor client methods that do not return `ValueTask` or `ValueTask<T>`. |
| `ULA105` | Error | Reports generic actor client methods. |
| `ULA106` | Error | Reports `ref`, `out`, or `in` parameters on actor client methods. |

## Diagnostics

The same analyzer asset also reports common unsafe actor usage:

| Rule | Severity | Description |
| --- | --- | --- |
| `ULA001` | Warning | Reports `ctx.Self.Call(...)` inside an actor because calling the current actor through its own mailbox can deadlock. |
| `ULA002` | Warning | Reports `.Wait()` and `.Result` on task-like values inside actor types because blocking can stall the mailbox. |
| `ULA003` | Warning | Reports discarded `ActorRef<TMessage>.Call<T>` results because request/response calls should be awaited, returned, or stored. |

## Design Direction

Source generation is the preferred path for making ULinkActor easier to use without increasing runtime cost. Future generated APIs should keep the same boundary:

- Generate static, IDE-visible C# source.
- Lower higher-level APIs into `ActorSystem`, `ActorRef<TMessage>`, `Send`, and `Call<T>`.
- Keep Roslyn references inside the generator project.
- Do not add runtime reflection to actor discovery, dispatch, generated proxy calls, or response binding.

If the generator becomes part of the default `ULinkActor` package experience, it should still be distributed as a compile-time analyzer asset rather than as a runtime dependency.

## License

MIT
