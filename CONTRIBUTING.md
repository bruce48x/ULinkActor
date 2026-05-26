# Contributing

This document is for ULinkActor framework contributors. For user-facing introductions, quick starts, and feature descriptions, see [README.md](./README.md).

---

# Design Positioning And Boundaries

The core idea of ULinkActor is:

```text
message-driven service runtime
```

It is not:

```text
enterprise distributed actor platform
```

Preserve these constraints when changing the runtime:

- Keep the core small.
- Prefer single-process runtime scenarios.
- One actor owns one mailbox.
- Each actor processes messages sequentially.
- Actor state should usually be lock-free.
- Support Send.
- Support Call<T>.
- Support timers.
- Support backpressure.
- Build on TPL Dataflow.
- Prefer compile-time source generation over runtime reflection for API ergonomics.
- Keep source generators and analyzers as compile-time assets, not runtime dependencies.
- Do not introduce MMO business concepts into the core.
- Do not depend on Unity.
- Do not bind the core to a network protocol.
- Do not use dynamic proxies, runtime reflection, or `MethodInfo.Invoke` as a required path for actor dispatch, actor discovery, generated proxy calls, or request/response binding.

The core model comes from skynet:

```text
service = mailbox + state + message handler
```

Each actor:

- Owns its state.
- Owns its mailbox.
- Communicates only through messages.
- Processes messages sequentially.

Because of this, state inside a single actor usually does not need `lock`, `ConcurrentDictionary`, or CAS-style concurrency protection.

Skynet-derived runtime principles:

- Actor execution is process-local. ULinkActor should not hide network, serialization, remote timeout, or remote backpressure behind a local-looking actor reference.
- A local actor call can be ergonomic, but a remote actor call belongs to ULinkGame.Cluster or application code and must use a different API shape.
- One actor may be assigned to a runtime execution lane such as a logic thread, but that lane is an implementation detail. Public APIs should identify actors and mailboxes, not scheduler lanes.
- Mailbox overload is a normal runtime condition. Bounded capacity, send failure, timeout, dead-letter publication, and metrics are better than unbounded queues that fail later through memory pressure.
- Long-running or blocking work must not monopolize actor execution while touching actor state. If blocking work is unavoidable, isolate it from actor state and resume the actor by posting a message back through the mailbox.
- Runtime diagnostics should make hidden scheduling costs visible: mailbox length, backpressure, slow message handling, dead letters, timer delivery, and call timeout root cause.
- Shutdown should be explicit and bounded. Prefer lightweight stop/drain hooks over destructor-style cleanup that coordinates across many actors.

The following concerns are outside ULinkActor Core:

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

These concerns belong in ULinkGame, ULinkRPC, or application code. Do not introduce them into the core API when modifying the runtime.

---

# Source Generation And Reflection

Source generation is considered part of the intended ULinkActor developer experience. It should be used to remove repetitive code, improve IDE completion, and keep actor APIs strongly typed while preserving the small runtime model.

The runtime model remains:

```text
typed generated API -> ActorRef<TMessage> / ActorSystem -> mailbox -> actor handler
```

Generated code should call normal public runtime APIs such as `Spawn`, `Send`, and `Call<T>`. It should not depend on runtime method lookup, dynamic invocation, dynamic proxy libraries, or reflection-driven dispatch.

Roslyn dependencies must stay in generator or analyzer projects. The `ULinkActor` runtime assembly must not reference Roslyn packages. If generators or analyzers are distributed through the main `ULinkActor` NuGet package, they should be packed under analyzer paths such as:

```text
analyzers/dotnet/cs
```

This keeps the user installation simple while preserving zero runtime cost.

Acceptable compile-time generation targets include:

- Typed spawn extension methods.
- Strongly typed request/response helpers.
- Optional local actor client or proxy code that lowers method-like calls into `Send` or `Call<T>`.
- Diagnostics that catch unsafe actor usage at compile time.

Generated actor clients are local runtime ergonomics only. They must not become transparent remote actor proxies or hide cluster routing, serialization, network latency, timeout, retry, or remote backpressure costs.

Avoid adding runtime reflection-based alternatives unless they are optional tooling paths and not part of normal dispatch.

---

# External Design References

External actor and game-server frameworks are useful references for ergonomics, but ULinkActor should only absorb ideas that fit the core boundary.

Good candidates for ULinkActor Core:

- Compile-time generated actor APIs instead of runtime proxy/reflection machinery.
- Actor call-chain diagnostics for self-calls, circular awaits, and timeout root-cause analysis.
- Lightweight lifecycle hooks for startup and shutdown work.
- System-level scheduling that still delivers messages through actor mailboxes.

Good candidates for ULinkGame or application code:

- Entity / Component / State modeling.
- Hotfix agent architecture.
- Transparent persistence.
- Idle game-data eviction.
- Game events, Gate / Realm / Scene / AOI, protocol and config tooling.

---

# Development Plan

Completed items should reflect implemented repository behavior, not only design intent. Keep the next tasks focused on the process-local actor runtime; cluster routing and remote actor semantics belong to ULinkGame or application code.

## Completed Baseline

| Status | Task |
| --- | --- |
| Done | Document that source generation is the preferred API ergonomics path and runtime reflection is not part of the normal actor dispatch path. |
| Done | Package `ULinkActor.SourceGenerator` as a compile-time analyzer asset inside the main `ULinkActor` package. |
| Done | Keep Roslyn dependencies out of the `ULinkActor` runtime assembly. |
| Done | Add compile-time diagnostics for actor self-calls and blocking waits inside actor types. |
| Done | Add compile-time diagnostics for discarded `Call<T>` request results. |
| Done | Add optional local generated actor client/proxy APIs that lower method-like calls into `Send` and `Call<T>` without runtime reflection. |
| Done | Add compile-time diagnostics for unsupported `[ActorClient]` interface shapes. |

## P0 Runtime Contract Hardening

These tasks unblock reliable use by ULinkGame.Cluster without leaking scheduler internals.

| Task | Output | Completion Signal |
| --- | --- | --- |
| Mailbox backpressure contract | Make bounded mailbox full behavior explicit in public results, exceptions, dead letters, and metrics. | Tests cover send failure, call timeout, mailbox capacity, and dead-letter publication. |
| Call timeout root-cause diagnostics | Trace actor call chains enough to identify timeout source actors and circular waits. | Tests or diagnostics fixtures show self-call, circular await, and downstream timeout paths. |
| Scheduler lane privacy | Ensure any logic-thread or execution-lane implementation remains private to the runtime. | Public API and generated code expose actor ids/refs only, never scheduler lane ids. |
| Stop and drain semantics | Define bounded actor/system stop behavior. | Tests cover rejecting new messages after stop, bounded drain of queued work, timer disposal, and dead-letter handling. |

## P1 Runtime Observability

| Task | Output | Completion Signal |
| --- | --- | --- |
| Mailbox metrics | Add or verify counters/gauges for mailbox length, accepted sends, rejected sends, calls, timeouts, and dead letters. | Meter listener tests assert low-cardinality tags. |
| Slow handler tracing | Emit trace/log events for slow message handlers and timer callbacks. | Tests can force a slow handler and observe the diagnostic event. |
| Call-chain tracing | Propagate activity context through `Send`, `Call<T>`, timer delivery, and generated clients where possible. | Activity listener tests can correlate caller, target actor, and response/timeout. |

## P2 Safe Long-Running Work Pattern

| Task | Output | Completion Signal |
| --- | --- | --- |
| Offload guidance | Document how to run blocking or long-running work outside actor state and resume by posting a message back through the mailbox. | README or docs include a safe pattern and an unsafe anti-pattern. |
| Analyzer coverage | Extend analyzer warnings for common blocking work inside actor handlers when feasible. | Analyzer tests cover newly supported unsafe patterns without noisy false positives. |
| Runtime examples | Add a test or sample showing background work completion delivered through actor mailbox. | Example preserves single-actor non-concurrency. |

## P3 Lifecycle Hooks

| Task | Output | Completion Signal |
| --- | --- | --- |
| Activation hook review | Decide whether startup hooks should stay implicit or become a lightweight public contract. | Decision recorded and tests added if public API changes. |
| Stop hook review | Decide whether actor stop/drain hooks are needed beyond system disposal. | Decision recorded and stop/drain tests cover the final behavior. |

## Non-Goals

- Do not add transparent remote actor references.
- Do not bind ULinkActor to ULinkGame.Cluster.
- Do not expose scheduler lanes or `LogicThread` concepts in public APIs.
- Do not introduce transport, RPC, persistence, service discovery, or cluster membership into the runtime.

---

# Current Status

v0.1 is complete for the local actor/mailbox runtime feature set described in [README.md](./README.md).

The source generator and actor usage analyzer are packaged as compile-time analyzer assets inside the main `ULinkActor` package. `src/ULinkActor.SourceGenerator` remains an internal build project, not a standalone package.

---

# Project Structure

| Path | Responsibility |
| --- | --- |
| `src/ULinkActor` | Runtime public API, mailbox execution, timers, diagnostics, metrics, named actors, and actor groups. |
| `src/ULinkActor.SourceGenerator` | Typed spawn generation, actor client proxy generation, and actor usage analyzer diagnostics. |
| `tests/ULinkActor.Tests` | Runtime behavior tests, source generator tests, and analyzer tests. |

---

# Engineering Conventions

## Target Framework

Only .NET 10 is supported:

```xml
<TargetFramework>net10.0</TargetFramework>
```

## Solution

The repository uses the .NET 10 `.slnx` solution format:

```text
ULinkActor.slnx
```

## Version

The package version is defined by `src/ULinkActor/ULinkActor.csproj`.

`src/ULinkActor.SourceGenerator` is retained as an internal build project so Roslyn dependencies stay out of the runtime assembly, but it is not independently packable and should not be published as a standalone NuGet package.

## Repository

[bruce48x/ULinkActor](https://github.com/bruce48x/ULinkActor)

## Dependencies

The `ULinkActor` runtime targets .NET 10 only and does not declare an extra `System.Threading.Tasks.Dataflow` package reference.

`ULinkActor.SourceGenerator` uses Roslyn:

```xml
<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.12.0" PrivateAssets="all" />
```

---

# Test Responsibility

Tests should protect the actor runtime contract rather than mirror implementation details.

| Area | Required coverage when changed |
| --- | --- |
| Messaging | Send dispatch, Call<T> responses, timeout behavior, response type validation, and dead-letter behavior. |
| Mailbox | Send order, single-actor non-concurrency, bounded backpressure, stop drain behavior, and mailbox metrics. |
| Scheduler | Execution remains mailbox-mediated, scheduler lanes stay private implementation details, and long-running work cannot bypass actor state safety. |
| Timers | Timer delivery through the mailbox and timer disposal during stop. |
| Observability | ActivitySource tracing, slow message detection, and dead-letter publication. |
| Registry and groups | Named actor lookup, typed lookup validation, group broadcast, deduplication, and group stop. |
| Source generation | Generated spawn extensions, actor client proxies, generated source shape, and unsupported interface diagnostics. |
| Analyzer | Actor self-calls, blocking waits inside actors, discarded request calls, and any newly added diagnostics. |
