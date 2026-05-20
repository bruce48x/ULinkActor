# ULinkActor

ULinkActor 是一个面向 .NET 游戏服务端的轻量级 Actor / Mailbox Runtime，设计灵感来自 skynet。

它不是 Orleans、Akka.NET、Proto.Actor 这类完整分布式 Actor 平台，而是专注于游戏服务端长期需要的基础运行时能力：

- mailbox
- 顺序执行
- 无锁状态
- 异步消息驱动
- timer
- request / response
- backpressure

目标是提供一个：

```text
小
稳定
游戏友好
skynet 风格
的 .NET service runtime
```

推荐分层：

```text
ULinkActor
    轻量 mailbox runtime

ULinkRPC
    网络与 RPC

ULinkGame.Core
    游戏服务端基础设施

ULinkGame.Room
    房间模型

ULinkGame.MMO
    MMO 模板（可选）

具体游戏业务
```

---

# 当前状态

v0.1 已开发完成。

- ActorSystem
- ActorRef
- ActorId
- IActor
- ActorContext
- Send
- Call<T>
- Mailbox
- Timer
- Sequential Execution
- Bounded Mailbox Backpressure
- Graceful Shutdown
- Dead Letter
- Mailbox Metrics
- Slow Message Detection
- 更细粒度的 Configurable Capacity
- Typed Actor Wrapper
- Diagnostics
- Tracing
- Source Generator
- Named Actor
- Local Registry
- Actor Group
- Unit Test
- .NET 10 / .slnx 项目结构

## 已覆盖测试

- Send 派发消息
- Call<T> 返回响应
- Call<T> 超时
- mailbox 按发送顺序执行
- 同一个 actor 不并发执行
- timer 消息通过 mailbox 串行执行
- bounded mailbox 产生 backpressure
- Stop drain 已入队消息
- stop 后发送消息进入 dead letter
- per-actor mailbox capacity 覆盖
- mailbox metrics 快照
- slow message detection
- typed actor wrapper
- ActivitySource tracing
- named actor / local registry
- actor group
- source generator typed spawn extension

---

# 设计目标

ULinkActor 的核心思想是：

```text
message-driven service runtime
```

而不是：

```text
enterprise distributed actor platform
```

设计约束：

- 核心极小
- 单进程优先
- 一个 actor 一个 mailbox
- actor 内部串行执行
- 天然无锁状态
- 支持 Send
- 支持 Call<T>
- 支持 Timer
- 支持 Backpressure
- 基于 TPL Dataflow
- 不引入 MMO 业务概念
- 不依赖 Unity
- 不绑定网络协议

---

# 核心模型

ULinkActor 的核心理念来自 skynet：

```text
service
    mailbox
    state
    message handler
```

每个 actor：

- 拥有自己的状态
- 拥有自己的 mailbox
- 只能通过消息通信
- 内部顺序执行

因此同一个 actor 内：

```text
不需要 lock
不需要 ConcurrentDictionary
不需要 CAS
不会发生并发状态修改
```

这是游戏服务端最重要的价值之一。

---

# 核心概念

## ActorSystem

ActorSystem 管理所有 actor。

```csharp
public sealed class ActorSystem
{
    public ActorRef Spawn(IActor actor);

    public ActorRef Spawn(string name, IActor actor);

    public ActorGroup CreateGroup(params ActorRef[] actorRefs);

    public ValueTask Send(ActorId target, object message);

    public ValueTask<TResponse> Call<TResponse>(
        ActorId target,
        object request,
        TimeSpan timeout);

    public ValueTask Stop(ActorId target);

    public MailboxMetrics GetMailboxMetrics(ActorId target);

    public ActorRef GetActor(string name);
}
```

职责：

- 创建 actor
- 调度 mailbox
- 消息派发
- timer 调度
- actor 生命周期

## Actor

Actor 是一个状态对象，只通过消息驱动。

```csharp
public interface IActor
{
    ValueTask OnMessage(ActorContext ctx, object message);
}
```

Actor 内部可以持有状态，但不允许外部直接修改状态。

也可以使用强类型 actor wrapper：

```csharp
public interface IActor<in TMessage>
{
    ValueTask OnMessage(ActorContext ctx, TMessage message);
}
```

## ActorRef

ActorRef 是 actor 的通信句柄。

```csharp
public sealed class ActorRef
{
    public ActorId Id { get; }

    public ValueTask Send(object message);

    public ValueTask<TResponse> Call<TResponse>(
        object request,
        TimeSpan timeout);

    public ValueTask Stop();

    public MailboxMetrics GetMailboxMetrics();
}
```

业务代码只能通过 ActorRef 与 actor 通信。

强类型 actor 会返回 `ActorRef<TMessage>`：

```csharp
ActorRef<RoomMessage> room = system.Spawn<RoomMessage>(new RoomActor());

await room.Send(new JoinRoom(10001));
```

## Mailbox

每个 actor 拥有一个 mailbox。

当前实现基于 TPL Dataflow `ActionBlock<T>`：

```csharp
new ActionBlock<Envelope>(
    async envelope => await Dispatch(envelope),
    new ExecutionDataflowBlockOptions
    {
        MaxDegreeOfParallelism = 1,
        EnsureOrdered = true,
        BoundedCapacity = 1024
    });
```

关键点：

```text
MaxDegreeOfParallelism = 1
```

这意味着同一个 actor 永远串行执行。

## Metrics

Mailbox metrics 是轻量快照，不启动后台采样。

```csharp
MailboxMetrics metrics = actor.GetMailboxMetrics();
```

当前包含：

- Capacity
- QueuedCount
- EnqueuedCount
- ProcessedCount
- IsCompleted

## Slow Message Detection

Slow message detection 默认关闭。

通过 `ActorSystemOptions.SlowMessageThreshold` 设置阈值后，超过阈值的消息会触发事件：

```csharp
ActorSystem system = new(new ActorSystemOptions
{
    SlowMessageThreshold = TimeSpan.FromMilliseconds(50)
});

system.SlowMessageDetected += slow =>
{
    Console.WriteLine($"{slow.ActorId} {slow.Elapsed}");
};
```

## Diagnostics / Tracing

ULinkActor 使用 .NET 标准 `ActivitySource` 暴露消息派发追踪，不绑定具体日志或 APM 平台。

```csharp
using ActivityListener listener = new()
{
    ShouldListenTo = source => source.Name == ULinkActorDiagnostics.ActivitySourceName,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
        ActivitySamplingResult.AllDataAndRecorded,
    ActivityStopped = activity =>
    {
        Console.WriteLine(activity.OperationName);
    }
};

ActivitySource.AddActivityListener(listener);
```

每条消息派发会产生：

```text
ULinkActor.Actor.Dispatch
```

当前 tags：

- ulinkactor.actor.id
- ulinkactor.message.type
- ulinkactor.message.kind

## Source Generator

`ULinkActor.SourceGenerator` 会扫描 public `IActor<TMessage>` 实现，并生成 `ActorSystem` 扩展方法。

例如：

```csharp
public sealed class RoomActor : IActor<RoomMessage>
{
    public ValueTask OnMessage(ActorContext ctx, RoomMessage message)
    {
        return ValueTask.CompletedTask;
    }
}
```

会生成：

```csharp
ActorRef<RoomMessage> room = system.SpawnRoomActor(new RoomActor());

ActorRef<RoomMessage> namedRoom = system.SpawnRoomActor(
    "room-10001",
    new RoomActor());
```

---

# 消息模型

ULinkActor 不要求消息继承基类。

推荐使用：

```csharp
record
record struct
```

示例：

```csharp
public readonly record struct JoinRoom(long PlayerId);

public readonly record struct LeaveRoom(long PlayerId);

public readonly record struct PlayerInput(
    long PlayerId,
    float X,
    float Y);

public readonly record struct RoomTick;
```

---

# Send / Call / Timer

## Send

Fire-and-forget，不等待返回值。

```csharp
await actor.Send(new PlayerInput(playerId, x, y));
```

适合：

- 输入
- 通知
- 广播
- Tick

## Call<T>

Request / Response。

```csharp
PlayerInfo info = await actor.Call<PlayerInfo>(
    new QueryPlayerInfo(playerId),
    TimeSpan.FromSeconds(3));
```

内部通过 message envelope + TaskCompletionSource 实现。

## Timer

Timer 本质也是消息。

```csharp
ctx.ScheduleRepeated(
    message: new RoomTick(),
    dueTime: TimeSpan.Zero,
    period: TimeSpan.FromMilliseconds(50));
```

Timer 不会绕过 mailbox，所以 timer 与普通消息仍然串行执行，不会出现并发 Tick。

---

# 使用示例

ULinkActor 适合实现：

- RoomActor
- BattleActor
- MatchActor
- ChatActor
- GuildActor
- SessionActor
- WorldActor

示例：

```csharp
public sealed class RoomActor : IActor
{
    private readonly Dictionary<long, Player> players = new();

    public ValueTask OnMessage(ActorContext ctx, object message)
    {
        switch (message)
        {
            case JoinRoom m:
                players[m.PlayerId] = new Player(m.PlayerId);
                break;

            case LeaveRoom m:
                players.Remove(m.PlayerId);
                break;

            case PlayerInput m:
                break;

            case RoomTick:
                break;
        }

        return ValueTask.CompletedTask;
    }
}
```

启动：

```csharp
ActorSystem system = new();

ActorRef room = system.Spawn(new RoomActor());

await room.Send(new JoinRoom(10001));

await room.Send(new PlayerInput(10001, 1, 0));
```

单个 actor 可以覆盖默认 mailbox capacity：

```csharp
ActorRef room = system.Spawn(
    new RoomActor(),
    new ActorSpawnOptions { MailboxCapacity = 4096 });
```

actor 也可以在本地系统内命名注册：

```csharp
ActorRef room = system.Spawn("room-10001", new RoomActor());

ActorRef resolved = system.GetActor("room-10001");
```

强类型 actor 同样支持命名注册：

```csharp
ActorRef<RoomMessage> room = system.Spawn<RoomMessage>(
    "room-10001",
    new RoomActor());
```

多个 actor 可以组成本地 group，用于广播消息或批量停止：

```csharp
ActorGroup group = system.CreateGroup(room1, room2, room3);

await group.Send(new RoomTick());
await group.Stop();
```

强类型 actor group：

```csharp
ActorGroup<RoomMessage> group = system.CreateGroup(room1, room2);

await group.Send(new RoomTick());
```

---

# 为什么使用 TPL Dataflow

TPL Dataflow 已经提供：

- async message processing
- bounded queue
- backpressure
- ordered execution
- scheduler
- completion propagation

ULinkActor 只是在其上构建更适合游戏服务端的 API：

```text
ActorRef
Mailbox
Send
Call
Timer
```

业务层不会暴露 Dataflow API。未来可以替换为：

```text
TPL Dataflow
→ Channel<T>
→ 自研 scheduler
```

---

# 工程信息

## 项目结构

```text
src/
  ULinkActor/
    ActorSystem.cs
    ActorGroup.cs
    ActorRef.cs
    ActorId.cs
    IActor.cs
    ActorContext.cs
    Envelope.cs
    Mailbox.cs
    ActorTimer.cs
    DeadLetter.cs
    MailboxMetrics.cs
    SlowMessage.cs
    ActorSpawnOptions.cs
    TypedActorAdapter.cs
    ULinkActorDiagnostics.cs

  ULinkActor.SourceGenerator/
    TypedActorSpawnGenerator.cs

tests/
  ULinkActor.Tests/
    ActorSystemTests.cs
```

## Target Framework

仅支持 .NET 10：

```xml
<TargetFramework>net10.0</TargetFramework>
```

## Version

当前包版本：

```xml
ULinkActor: 0.1.1
ULinkActor.SourceGenerator: 0.1.0
```

## Solution

使用 .NET 10 `.slnx`：

```text
ULinkActor.slnx
```

## Repository

[bruce48x/ULinkActor](https://github.com/bruce48x/ULinkActor)

## 依赖

`ULinkActor` runtime 仅面向 .NET 10，不额外声明 `System.Threading.Tasks.Dataflow` 包引用。

`ULinkActor.SourceGenerator` 使用 Roslyn：

```xml
<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.12.0" PrivateAssets="all" />
```

---

# 非目标

以下内容不属于 ULinkActor Core：

- Cluster
- Remote Actor
- Virtual Actor
- Actor Persistence
- Event Sourcing
- Supervisor Tree
- MMO 模板
- Gate / Realm / Map / AOI
- Unity 集成
- 数据库抽象
- ORM
- 网络协议
- Transport
- RPC

这些应由 ULinkGame、ULinkRPC 或业务层解决。

---

# License

MIT
