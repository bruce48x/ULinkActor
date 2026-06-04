# Changelog

## 0.3.5 — Unreleased

### Added

- **Actor handle**: `ActorHandle<TMessage>` separates owner/admin operations from message-only `ActorRef<TMessage>`.
- **Actor state machine**: `ActorState` enum (`Active`, `Draining`, `Dead`) exposed via `ActorHandle.GetState()` and `ActorSystem.GetActorState()`. Actors now have an explicit, queryable lifecycle.
- **Message interceptor hooks**: `IActorMessageInterceptor` with `OnBeforeMessage` and `OnAfterMessage` callbacks, configured per-`ActorSystem` via `ActorSystemOptions.MessageInterceptor`. Enables message recording, replay, and custom diagnostics without modifying the runtime.
- **Observer error diagnostics**: `ActorSystem.ObserverErrorPublished` reports failures from diagnostic event handlers and message interceptors without changing actor message execution.
- **Actor call options**: `ActorCallOptions` gives `Call<T>` separate queue and response timeout budgets.
- **Design philosophy documentation**: `docs/design-philosophy.md` documents the Skynet-influenced principles behind the runtime.

### Changed

- **Spawn API** (breaking): `ActorSystem.Spawn(...)` and generated typed spawn extensions now return `ActorHandle<TMessage>`. Use `handle.Ref` when passing a message-only actor reference to other code.
- **Diagnostic events** (breaking): `DeadLetter`, `SlowMessage`, and `ActorCallTimeout` now expose message/request type names instead of the original message or request payload.
- **Message interceptor errors**: `IActorMessageInterceptor` exceptions are now reported through `ObserverErrorPublished` and no longer fail actor message dispatch.
- **Stop flow**: Actor removal from the registry now happens *after* the mailbox drain completes, so `GetActorState()` correctly reports `Draining` during the drain window.
- **Graceful stopping**: `IActorStopping<TMessage>` now runs as the final mailbox turn during explicit stop. The hook no longer runs concurrently with an in-flight message, and drain timeouts leave the actor in `Draining` until the stop sequence actually completes.
- **Call timeout API** (breaking): `ActorRef<TMessage>.Call<TResponse>` now accepts `ActorCallOptions` instead of a single `TimeSpan`. Queue backpressure timeout and response timeout are handled independently.
- **Call timeout diagnostics** (breaking): `ActorCallTimeout` now exposes `QueueTimeout`, `ResponseTimeout`, and `Elapsed` instead of a single `Timeout`.

### Removed

- **ActorRef management APIs** (breaking): Removed `Stop(...)`, `GetState()`, and `GetMailboxMetrics()` from `ActorRef<TMessage>`. Keep the `ActorHandle<TMessage>` returned by spawn for lifecycle and diagnostics.
- **`ActorSystemOptions.ExecutionTimeout`** (breaking): Removed preemptive message execution timeout because timing out a handler with `WaitAsync` allowed the mailbox to advance while the original actor turn could still be running. Slow or stuck actor turns should be diagnosed through slow-message telemetry and handled by application-level shutdown or process supervision.
- **`ActorCallTimeoutReason.CircularWait`** (breaking): Circular actor call chains now throw `InvalidOperationException` synchronously before any message is queued, rather than waiting for a timeout. Circular calls are a design error, not a runtime condition.

## 0.2.3 - 2026-05-26

### Changed

- Aligned internal namespaces with runtime and source generator directory structure while preserving the public runtime API namespace.
- Added a focused test solution under `tests/test.slnx`.

## 0.2.2 - 2026-05-26

### Changed

- Organized runtime and source generator code files into responsibility-based directories.

## 0.2.1 - 2026-05-26

### Fixed

- Fixed a lifecycle stop test assertion that depended on mailbox scheduling timing.

## 0.2.0 - 2026-05-26

### Added

- Added explicit mailbox backpressure results through `ActorRef<TMessage>.TrySend(...)` and `ActorSendResult`.
- Added rejected mailbox metrics and dead-letter publication for failed immediate sends.
- Added structured call-timeout diagnostics through `ActorSystem.CallTimedOut`, including caller, target, timeout reason, request, and actor call chain.
- Added bounded stop/drain overloads that return `ActorStopResult`.
- Added optional lifecycle hooks with `IActorStarted<TMessage>` and `IActorStopping<TMessage>`.
- Added runtime observability through .NET `Meter` counters/gauges for message delivery, calls, timeouts, dead letters, and mailbox queue length.
- Added slow-message trace events and activity context propagation through `Send`, `Call<T>`, timers, and generated actor clients.
- Added guidance and tests for safe long-running work that resumes actors by posting completion messages back through the mailbox.
- Extended analyzer coverage for common blocking APIs inside actor types.

### Changed

- Reworked README and contributor documentation around design principles, runtime boundaries, and maintenance rules.
- Clarified that ULinkActor remains a process-local actor/mailbox runtime and does not provide distributed actor, cluster, RPC, transport, persistence, or gameplay concepts.

### Removed

- Removed `ActorGroup<TMessage>` and `ActorSystem.CreateGroup(...)` from the core runtime. Batch grouping and broadcast semantics should live in ULinkGame or application code.
