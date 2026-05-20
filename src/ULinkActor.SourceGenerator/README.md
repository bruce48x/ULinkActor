# ULinkActor.SourceGenerator

`ULinkActor.SourceGenerator` generates typed `ActorSystem` spawn extension methods for public ULinkActor typed actors.

It is intended to reduce repeated `Spawn<TMessage>(...)` calls when your project defines actors that implement `IActor<TMessage>`.

## Install

Install the runtime package and the source generator package:

```xml
<ItemGroup>
  <PackageReference Include="ULinkActor" Version="0.1.2" />
  <PackageReference Include="ULinkActor.SourceGenerator" Version="0.1.1" PrivateAssets="all" />
</ItemGroup>
```

## Usage

Define a public typed actor:

```csharp
using ULinkActor;

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

## License

MIT
