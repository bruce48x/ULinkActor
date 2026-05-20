# ULinkActor

ULinkActor 是一个面向 .NET 游戏服务端的轻量级 Actor / Mailbox Runtime，设计灵感来自 skynet。

它不是 Orleans、Akka.NET、Proto.Actor 这类完整分布式 Actor 平台，而是专注于游戏服务端常见的单进程 service runtime 能力：

- 一个 actor 一个 mailbox
- actor 内部顺序执行
- 无锁状态模型
- 异步消息驱动
- timer
- request / response
- backpressure
- diagnostics / tracing

v0.1 已开发完成，当前 runtime 包版本为 `0.1.1`。

---

# 适用场景

ULinkActor 适合实现游戏服务端中的长期状态服务，例如：

- RoomActor
- BattleActor
- MatchActor
- ChatActor
- GuildActor
- SessionActor
- WorldActor

核心收益是把天然串行的状态对象收敛到 actor 内部，通过 mailbox 保证顺序执行，让业务代码少写锁、少处理共享状态并发问题。

推荐分层：

```text
ULinkActor        轻量 mailbox runtime
ULinkRPC          网络与 RPC
ULinkGame.Core    游戏服务端基础设施
ULinkGame.Room    房间模型
ULinkGame.MMO     MMO 模板（可选）
具体游戏业务
```

相关项目：

- [bruce48x/ULinkRPC](https://github.com/bruce48x/ULinkRPC)
- [bruce48x/ULinkGame](https://github.com/bruce48x/ULinkGame)

---

# 快速开始

定义消息和 actor：

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

启动并发送消息：

```csharp
ActorSystem system = new();
ActorRef room = system.Spawn(new RoomActor());

await room.Send(new JoinRoom(10001));
```

需要返回值时使用 `Call<T>`；需要周期任务时在 actor 内通过 `ActorContext` 调度 timer。Timer 消息和普通消息一样进入 mailbox，因此不会绕过 actor 的顺序执行约束。

---

# 主要能力

## Send

Fire-and-forget，不等待返回值。适合输入、通知、广播、Tick 等场景。

## Call<T>

Request / Response。适合查询状态、请求计算结果、等待 actor 内部操作完成等场景。

## Timer

Timer 本质也是消息，会进入 actor mailbox，与普通消息串行执行。

## Typed Actor

除了 `IActor`，也支持 `IActor<TMessage>` 和 `ActorRef<TMessage>`，用于减少业务层的消息类型转换。

## Named Actor / Local Registry

actor 可以在本地 `ActorSystem` 内按名称注册，并通过名称解析。

## Actor Group

多个 actor 可以组成本地 group，用于广播消息或批量停止。

## Backpressure

mailbox 支持 bounded capacity。队列达到容量上限时，发送方会受到 backpressure。

## Metrics / Slow Message Detection

可以读取 mailbox metrics 快照，也可以配置 slow message threshold 来发现处理过慢的消息。

## Diagnostics / Tracing

ULinkActor 使用 .NET 标准 `ActivitySource` 暴露消息派发追踪，不绑定具体日志或 APM 平台。

## Source Generator

`ULinkActor.SourceGenerator` 会为 public `IActor<TMessage>` 实现生成 typed spawn 扩展方法，减少重复样板代码。

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

这些应由 [ULinkGame](https://github.com/bruce48x/ULinkGame)、[ULinkRPC](https://github.com/bruce48x/ULinkRPC) 或业务层解决。

---

# 开发本框架

面向 ULinkActor 框架开发者的项目结构、设计约束、测试覆盖和工程约定见 [CONTRIBUTING.md](./CONTRIBUTING.md)。

---

# License

MIT
