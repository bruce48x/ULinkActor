# Changelog

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
