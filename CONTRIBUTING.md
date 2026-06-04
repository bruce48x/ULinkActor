# Contributing

This document is for ULinkActor framework contributors. For user-facing introductions, quick starts, and feature descriptions, see [README.md](./README.md).

For the design rationale behind ULinkActor's choices, see **[docs/design-philosophy.md](./docs/design-philosophy.md)**.

## Documentation Map

This file is the single authority for all contributor and maintenance rules. Supporting documents provide deeper rationale:

| Document | Purpose |
| --- | --- |
| [README.md](./README.md) | User-facing introduction, quick start, and feature reference |
| [docs/design-philosophy.md](./docs/design-philosophy.md) | Design principles, influences, tradeoffs, and rationale |
| [src/ULinkActor/README.md](./src/ULinkActor/README.md) | NuGet package page (condensed) |
| [src/ULinkActor.SourceGenerator/README.md](./src/ULinkActor.SourceGenerator/README.md) | Source generator API and usage |

Add new docs under `docs/` and link them here when they address contributor or maintenance concerns.

## Contributor Checklist

Before changing ULinkActor:

- Confirm the change fits the small, process-local runtime boundary.
- Keep network, persistence, distributed identity, Unity, and gameplay concepts outside this repository.
- Preserve actor state access through mailbox-mediated actor turns.
- Add or update tests for the affected runtime contract.
- Keep Roslyn dependencies out of the `ULinkActor` runtime assembly.
- Use .NET 10 and the repository `.slnx` solution format.

## Runtime Identity

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
- Receives explicit dependencies through construction, actor refs, or messages rather than through `ActorContext<TMessage>`.

Because of this, state inside a single actor usually does not need `lock`, `ConcurrentDictionary`, or CAS-style concurrency protection.

## Core Boundaries

Keep the core small and process-local.

| Boundary | Includes | Excludes |
| --- | --- | --- |
| **Messaging** | Typed actors, local refs, `Send`, `Call<T>`, timers | Cluster, virtual actors, transparent remoting |
| **Mailbox** | Bounded capacity, backpressure, stop/drain | Unbounded queues, supervisor trees |
| **Lifecycle** | Optional `IActorStarted`/`IActorStopping` hooks | Persistence, event sourcing, DI activation |
| **Registry** | Named local actor lookup with type validation | Distributed registry, service discovery |
| **Diagnostics** | `ActivitySource`, `Meter`, dead letters, slow-message | APM binding, structured logging framework |
| **Tooling** | Compile-time generators, analyzer diagnostics | Runtime reflection dispatch, dynamic proxies |
| **Application** | Process-local actor/mailbox runtime | Network, RPC, serialization, game concepts, Unity |

See [docs/design-philosophy.md](./docs/design-philosophy.md) for the rationale behind each boundary.

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
tests/test.slnx
```

The package version is defined by `src/ULinkActor/ULinkActor.csproj`.

`src/ULinkActor.SourceGenerator` is an internal build project. It is not independently packable and should not be published as a standalone NuGet package.

The `ULinkActor` runtime targets .NET 10 only and does not declare an extra `System.Threading.Tasks.Dataflow` package reference.

## Build And Test Commands

Use the repository solution for normal validation:

```powershell
dotnet test tests/test.slnx
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

Shutdown should be explicit and bounded. Graceful stop hooks are for explicit stop paths. During explicit stop, new application messages are rejected, queued messages drain, and `IActorStopping<TMessage>` runs as the final mailbox turn before completion. If a drain timeout elapses, the actor remains `Draining` until the current work and final stop hook finish. System disposal is a cleanup path and should not run user graceful-stop logic that depends on mailbox delivery.

## Source Generation Boundaries

### Generated API Shape

Source generation is part of the intended developer experience. It should remove repetitive code, improve IDE completion, and keep actor APIs strongly typed while preserving the small runtime model.

The runtime model remains:

```text
typed generated API -> ActorHandle<TMessage> / ActorRef<TMessage> / ActorSystem -> mailbox -> actor handler
```

Generated code should call normal public runtime APIs such as `Spawn`, `Send`, and `Call<T>`. It must not depend on runtime method lookup, dynamic invocation, dynamic proxy libraries, or reflection-driven dispatch.

Acceptable compile-time generation targets include:

- Typed spawn extension methods.
- Strongly typed request/response helpers.
- Local actor client code that lowers method-like calls into `Send` or `Call<T>`.
- Diagnostics that catch unsafe actor usage at compile time.

Generated actor clients are local runtime ergonomics only. They must not become transparent remote actor proxies or hide cluster routing, serialization, network latency, timeout, retry, or remote backpressure costs.

Do not add implicit conversions between owner/admin handles and message-only refs. Code should use `.Ref` explicitly when downgrading `ActorHandle<TMessage>` to `ActorRef<TMessage>`.

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
- `IActorStopping<TMessage>` runs during explicit graceful stop as the final mailbox turn before the mailbox is completed.

The minimum actor contract remains `IActor<TMessage>`. Do not require lifecycle hooks for ordinary actors.

Lifecycle hooks are local runtime hooks, not supervision, persistence, dependency injection, or distributed activation. `IActorStopping<TMessage>` should do final cleanup directly because the actor is already closed to new application messages when it runs.

## Long-Running Work

Actor context is intentionally narrow. It exposes the current actor ref, response helpers, and timer helpers, but not `ActorSystem`. Do not add broad service-locator access to actor handlers.

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

Metric tags must stay low-cardinality. Do not put actor ids, actor names, message payloads, or request values into metric tags. Diagnostic events should expose message/request type names rather than payload objects. Trace tags may include actor ids because they belong to individual spans.

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

## NuGet Publishing

NuGet publishing is handled by GitHub Actions, not by a local manual push.

Pushing to `main` triggers `.github/workflows/publish-nuget.yml`. The workflow restores test and package projects, runs the test suite, packs `src/ULinkActor/ULinkActor.csproj` into `artifacts/nuget`, and pushes the package to nuget.org with `--skip-duplicate`.

The workflow uses the `release` GitHub environment and `NuGet/login@v1` with `secrets.NUGET_USER`. Do not rely on a local `NUGET_API_KEY` for the normal release path.

`src/ULinkActor.SourceGenerator` is not independently packable. It is packed inside the `ULinkActor` package under `analyzers/dotnet/cs`.

### Version bumping

The package version is defined in `src/ULinkActor/ULinkActor.csproj` via the `<Version>` property.

**Critical rule — any change to library code under `src/` MUST bump the package version before pushing.** The CI workflow pushes to nuget.org with `--skip-duplicate`, so a push without a version bump silently skips publishing. The new code never reaches nuget.org, and downstream consumers get stale packages.

- Bump the `<Version>` in `src/ULinkActor/ULinkActor.csproj` whenever you change source files in `src/`.
- Bump even for small bug fixes — the version is the only signal that triggers a publish.
- Do not bump versions for test-only or docs-only changes unless changes in `src/` also need to ship.

If you forget: the CI run will succeed (pack + push with `--skip-duplicate`), but the updated package won't appear on nuget.org. The fix is to bump the version in a follow-up commit and push again.

For local verification only, pack the package without publishing:

```powershell
dotnet pack src/ULinkActor/ULinkActor.csproj -c Release -o artifacts/nuget
```
