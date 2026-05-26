# Contributing

This document is for ULinkActor framework contributors. For user-facing introductions, quick starts, and feature descriptions, see [README.md](./README.md).

## Contributor Checklist

Before changing ULinkActor:

- Confirm the change fits the small, process-local runtime boundary.
- Keep network, persistence, distributed identity, Unity, and gameplay concepts outside this repository.
- Preserve actor state access through mailbox-mediated actor turns.
- Add or update tests for the affected runtime contract.
- Keep Roslyn dependencies out of the `ULinkActor` runtime assembly.
- Use .NET 10 and the repository `.slnx` solution format.

## Design Positioning

ULinkActor is a:

```text
message-driven service runtime
```

It is not an:

```text
enterprise distributed actor platform
```

Preserve that distinction when changing the runtime. The core model is:

```text
actor = mailbox + state + message handler
```

Each actor:

- Owns its state.
- Owns its mailbox.
- Communicates only through messages.
- Processes messages sequentially.

Because of this, state inside a single actor usually does not need `lock`, `ConcurrentDictionary`, or CAS-style concurrency protection.

## Core Boundaries

Keep the core small and process-local.

ULinkActor should provide:

- Typed actors.
- Local actor references.
- `Send`.
- `Call<T>`.
- Timers delivered through actor mailboxes.
- Bounded mailboxes and explicit backpressure.
- Named local actor lookup.
- Bounded stop/drain semantics.
- Optional local lifecycle hooks.
- Runtime diagnostics through standard .NET observability APIs.
- Compile-time generated convenience APIs.
- Analyzer diagnostics for unsafe actor usage.

ULinkActor should not provide:

- Cluster routing.
- Transparent remote actor references.
- Virtual actors.
- Persistence.
- Event sourcing.
- Supervisor trees.
- Network protocols.
- RPC transports.
- Unity integration.
- MMO business concepts such as Gate, Realm, Scene, Map, AOI, RoomGroup, or gameplay events.

Those concerns belong in ULinkGame, ULinkRPC, or application code.

If a feature needs serialization, network routing, persistence, distributed identity, or gameplay semantics, it belongs outside ULinkActor.

## Project Structure

| Path | Responsibility |
| --- | --- |
| `src/ULinkActor` | Runtime public API, mailbox execution, timers, lifecycle hooks, diagnostics, metrics, and named actors. |
| `src/ULinkActor.SourceGenerator` | Typed spawn generation, actor client generation, and actor usage analyzer diagnostics. |
| `tests/ULinkActor.Tests` | Runtime behavior tests, source generator tests, and analyzer tests. |

## Engineering Conventions

Only .NET 10 is supported:

```xml
<TargetFramework>net10.0</TargetFramework>
```

The repository uses the .NET 10 `.slnx` solution format:

```text
ULinkActor.slnx
```

The package version is defined by `src/ULinkActor/ULinkActor.csproj`.

`src/ULinkActor.SourceGenerator` is an internal build project. It is not independently packable and should not be published as a standalone NuGet package.

The `ULinkActor` runtime targets .NET 10 only and does not declare an extra `System.Threading.Tasks.Dataflow` package reference.

## Build And Test Commands

Use the repository solution for normal validation:

```powershell
dotnet test ULinkActor.slnx
```

## Runtime Principles

### Local Runtime Model

Actor execution is process-local. A local actor call can be ergonomic, but a remote actor call must use a different API shape because it has serialization, routing, retry, timeout, and remote backpressure costs.

### Mailbox And Backpressure

Mailbox overload is a normal runtime condition. Prefer bounded capacity, send failure, timeout diagnostics, dead-letter publication, and metrics over unbounded queues that fail later through memory pressure.

### Scheduler Encapsulation

Execution-lane or scheduler details are implementation details. Public APIs and generated code should expose actor ids, actor refs, and mailboxes, not scheduler lanes or logic-thread concepts.

### Blocking Work

Long-running or blocking work must not monopolize actor execution while touching actor state. Follow the `Long-Running Work` rules below for concrete offload patterns.

### Shutdown Semantics

Shutdown should be explicit and bounded. Graceful stop hooks are for explicit stop paths. System disposal is a cleanup path and should not run user graceful-stop logic that depends on mailbox delivery.

## Source Generation Boundaries

### Generated API Shape

Source generation is part of the intended developer experience. It should remove repetitive code, improve IDE completion, and keep actor APIs strongly typed while preserving the small runtime model.

The runtime model remains:

```text
typed generated API -> ActorRef<TMessage> / ActorSystem -> mailbox -> actor handler
```

Generated code should call normal public runtime APIs such as `Spawn`, `Send`, and `Call<T>`. It must not depend on runtime method lookup, dynamic invocation, dynamic proxy libraries, or reflection-driven dispatch.

Acceptable compile-time generation targets include:

- Typed spawn extension methods.
- Strongly typed request/response helpers.
- Local actor client code that lowers method-like calls into `Send` or `Call<T>`.
- Diagnostics that catch unsafe actor usage at compile time.

Generated actor clients are local runtime ergonomics only. They must not become transparent remote actor proxies or hide cluster routing, serialization, network latency, timeout, retry, or remote backpressure costs.

### Roslyn Dependency Boundary

Roslyn dependencies must stay in generator or analyzer projects. The `ULinkActor` runtime assembly must not reference Roslyn packages. If generators or analyzers are distributed through the main `ULinkActor` package, they should be packed under analyzer paths such as:

```text
analyzers/dotnet/cs
```

### No Reflection Dispatch

Avoid adding runtime reflection-based alternatives unless they are optional tooling paths and not part of normal dispatch.

## Lifecycle Hooks

Lifecycle hooks are optional public contracts:

- `IActorStarted<TMessage>` runs after the actor is registered.
- `IActorStopping<TMessage>` runs during explicit graceful stop before the mailbox is completed.

The minimum actor contract remains `IActor<TMessage>`. Do not require lifecycle hooks for ordinary actors.

Lifecycle hooks are local runtime hooks, not supervision, persistence, dependency injection, or distributed activation. If a hook schedules follow-up work, that work should still enter through the mailbox.

## Long-Running Work

Actor state should be touched only during actor turns. For long-running work:

1. Capture the input needed for the work.
2. Start the work outside the actor turn.
3. Send a completion message back to the actor.
4. Update actor state when the completion message is processed.

Analyzer warnings should focus on high-confidence blocking patterns inside actor types, such as `.Wait()`, `.Result`, `Task.WaitAll(...)`, `Task.WaitAny(...)`, `.GetAwaiter().GetResult()`, and `Thread.Sleep(...)`. Avoid noisy warnings for safe offload patterns such as `Task.Run` followed by `ctx.Self.Send(...)`.

## Observability

Diagnostics should make hidden scheduling and mailbox costs visible without binding the runtime to a specific backend.

Use standard .NET APIs:

- `ActivitySource` for traces.
- `Meter` for metrics.
- Events for dead letters, slow messages, and call timeouts.

Metric tags must stay low-cardinality. Do not put actor ids, actor names, message payloads, or request values into metric tags. Trace tags may include actor ids because they belong to individual spans.

## Test Responsibility

Tests should protect the actor runtime contract rather than mirror implementation details.

| Area | Required coverage when changed |
| --- | --- |
| Messaging | Send dispatch, Call<T> responses, timeout behavior, response type validation, and dead-letter behavior. |
| Mailbox | Send order, single-actor non-concurrency, bounded backpressure, stop drain behavior, and mailbox metrics. |
| Scheduler | Execution remains mailbox-mediated, scheduler lanes stay private implementation details, and long-running work cannot bypass actor state safety. |
| Timers | Timer delivery through the mailbox and timer disposal during stop. |
| Lifecycle | Startup hook behavior, graceful stop hook behavior, startup failure rollback, and disposal behavior. |
| Observability | ActivitySource tracing, Meter metrics, slow message detection, call-timeout diagnostics, and dead-letter publication. |
| Registry | Named actor lookup and typed lookup validation. |
| Source generation | Generated spawn extensions, actor client proxies, generated source shape, and unsupported interface diagnostics. |
| Analyzer | Actor self-calls, blocking waits inside actors, discarded request calls, and any newly added diagnostics. |
