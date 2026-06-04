using System.Diagnostics;
using ULinkActor.Abstractions;
using ULinkActor.Core;
using ULinkActor.Messaging;

namespace ULinkActor;

public sealed class ActorSystem : IAsyncDisposable
{
    private readonly AsyncLocal<ActorCallContext?> currentCallContext = new();
    private readonly ActorRegistry registry = new();
    private readonly ActorSystemDiagnosticsPublisher diagnostics = new();
    private readonly ActorSystemOptions options;
    private long nextActorId;
    private bool disposed;

    public event Action<DeadLetter>? DeadLetterPublished
    {
        add => diagnostics.DeadLetterPublished += value;
        remove => diagnostics.DeadLetterPublished -= value;
    }

    public event Action<SlowMessage>? SlowMessageDetected
    {
        add => diagnostics.SlowMessageDetected += value;
        remove => diagnostics.SlowMessageDetected -= value;
    }

    public event Action<ActorCallTimeout>? CallTimedOut
    {
        add => diagnostics.CallTimedOut += value;
        remove => diagnostics.CallTimedOut -= value;
    }

    public event Action<ActorObserverError>? ObserverErrorPublished
    {
        add => diagnostics.ObserverErrorPublished += value;
        remove => diagnostics.ObserverErrorPublished -= value;
    }

    internal ActorCallContext? CurrentCallContext
    {
        get
        {
            ActorCallContext? context = currentCallContext.Value;
            return context is { IsActive: true } ? context : null;
        }
        set => currentCallContext.Value = value;
    }

    internal IActorMessageInterceptor? MessageInterceptor => options.MessageInterceptor;

    internal ActorSystemDiagnosticsPublisher Diagnostics => diagnostics;

    public ActorSystem()
        : this(new ActorSystemOptions())
    {
    }

    public ActorSystem(ActorSystemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MailboxCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MailboxCapacity must be greater than zero.");
        }

        if (options.SlowMessageThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SlowMessageThreshold must be greater than zero when set.");
        }

        this.options = options;
    }

    public ValueTask<ActorHandle<TMessage>> SpawnAsync<TMessage>(IActor<TMessage> actor)
    {
        return SpawnAsync(actor, null);
    }

    public ValueTask<ActorHandle<TMessage>> SpawnAsync<TMessage>(string name, IActor<TMessage> actor)
    {
        return SpawnAsync(name, actor, null);
    }

    public async ValueTask<ActorHandle<TMessage>> SpawnAsync<TMessage>(
        string name,
        IActor<TMessage> actor,
        ActorSpawnOptions? spawnOptions)
    {
        ValidateActorName(name);
        ArgumentNullException.ThrowIfNull(actor);

        ActorRef actorRef = await SpawnCoreAsync(
            new TypedActorAdapter<TMessage>(actor),
            typeof(TMessage),
            spawnOptions,
            name).ConfigureAwait(false);
        return new ActorHandle<TMessage>(actorRef);
    }

    public async ValueTask<ActorHandle<TMessage>> SpawnAsync<TMessage>(IActor<TMessage> actor, ActorSpawnOptions? spawnOptions)
    {
        ArgumentNullException.ThrowIfNull(actor);

        ActorRef actorRef = await SpawnCoreAsync(
            new TypedActorAdapter<TMessage>(actor),
            typeof(TMessage),
            spawnOptions,
            null).ConfigureAwait(false);
        return new ActorHandle<TMessage>(actorRef);
    }

    private async ValueTask<ActorRef> SpawnCoreAsync(
        IActor actor,
        Type messageType,
        ActorSpawnOptions? spawnOptions,
        string? name)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(messageType);

        int mailboxCapacity = spawnOptions?.MailboxCapacity ?? options.MailboxCapacity;

        if (mailboxCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spawnOptions), "MailboxCapacity must be greater than zero.");
        }

        ActorId id = new(Interlocked.Increment(ref nextActorId));
        ActorRef actorRef = new(this, id);
        ActorCell cell = new(this, actorRef, actor, messageType, mailboxCapacity, options.SlowMessageThreshold, name);

        if (!registry.TryAdd(id, cell))
        {
            throw new InvalidOperationException($"Actor id {id} already exists.");
        }

        if (name is not null && !registry.TryAddName(name, id))
        {
            registry.Remove(id, cell);
            cell.Complete();
            throw new InvalidOperationException($"Actor name '{name}' already exists.");
        }

        try
        {
            await cell.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            registry.Remove(id, cell);
            cell.Complete();
            throw;
        }

        return actorRef;
    }

    internal async ValueTask Send(ActorId target, object message, CancellationToken cancellationToken = default)
    {
        await Send(target, message, GetCurrentActivityContext(), cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask Send(
        ActorId target,
        object message,
        ActivityContext parentActivityContext,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(message);

        ActorCell cell = GetActorForDelivery(target, message);

        try
        {
            await cell.Send(
                new Envelope(message, callChain: GetCurrentCallChain(), parentActivityContext: parentActivityContext),
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "completed"));
            diagnostics.PublishDeadLetter(target, message, "Actor mailbox is completed.");
            throw;
        }
    }

    internal ActorSendResult TrySend(ActorId target, object message)
    {
        return TrySend(target, message, GetCurrentActivityContext());
    }

    internal ActorSendResult TrySend(
        ActorId target,
        object message,
        ActivityContext parentActivityContext)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(message);

        if (!registry.TryGet(target, out ActorCell? cell))
        {
            ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "unavailable"));
            diagnostics.PublishDeadLetter(target, message, "Actor does not exist.");
            return ActorSendResult.ActorUnavailable;
        }

        if (cell.IsStopping)
        {
            ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "stopping"));
            diagnostics.PublishDeadLetter(target, message, "Actor is stopping.");
            return ActorSendResult.ActorUnavailable;
        }

        if (cell.TrySend(new Envelope(
            message,
            callChain: GetCurrentCallChain(),
            parentActivityContext: parentActivityContext)))
        {
            return ActorSendResult.Accepted;
        }

        string reason = cell.Completion.IsCompleted
            ? "Actor mailbox is completed."
            : "Actor mailbox is full.";

        ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>(
            "reason",
            cell.Completion.IsCompleted ? "completed" : "full"));
        diagnostics.PublishDeadLetter(target, message, reason);

        return cell.Completion.IsCompleted
            ? ActorSendResult.ActorUnavailable
            : ActorSendResult.MailboxFull;
    }

    internal async ValueTask<TResponse> Call<TResponse>(
        ActorId target,
        object request,
        ActorCallOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        if (options.QueueTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "QueueTimeout must be greater than or equal to zero.");
        }

        if (options.ResponseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ResponseTimeout must be greater than zero.");
        }

        ActorCell cell = GetActorForDelivery(target, request);
        TaskCompletionSource<object?> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ActorCallContext? caller = CurrentCallContext;
        IReadOnlyList<ActorId> callChain = caller?.CallChain ?? Array.Empty<ActorId>();
        long startedAt = Stopwatch.GetTimestamp();

        if (callChain.Contains(target))
        {
            throw new InvalidOperationException(
                $"Circular actor call detected. The target actor {target.Value} is already in the call chain " +
                $"({string.Join(" -> ", callChain.Select(id => id.Value.ToString()))}). " +
                "Circular calls between actors indicate a design problem. " +
                "Restructure your actors to avoid circular dependencies.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        ULinkActorDiagnostics.CallStartedCounter.Add(1);
        Envelope envelope = new(request, response, callChain, GetCurrentActivityContext());

        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(static state =>
        {
            ((TaskCompletionSource<object?>)state!).TrySetCanceled();
        }, response);

        if (options.QueueTimeout == TimeSpan.Zero)
        {
            if (!cell.TrySend(envelope))
            {
                if (cell.Completion.IsCompleted)
                {
                    ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "completed"));
                    diagnostics.PublishDeadLetter(target, request, "Actor mailbox is completed.");
                    throw new InvalidOperationException($"Actor {target} mailbox is completed.");
                }

                ActorCallTimeout timeoutDiagnostic = diagnostics.PublishCallTimeout(
                    caller?.ActorId,
                    target,
                    request,
                    options,
                    Stopwatch.GetElapsedTime(startedAt),
                    ActorCallTimeoutReason.QueueTimeout,
                    callChain);
                TimeoutException exception = diagnostics.CreateCallTimeoutException(
                    timeoutDiagnostic,
                    "The actor call timed out before it could be queued.");
                response.TrySetException(exception);
                throw exception;
            }
        }
        else
        {
            using CancellationTokenSource queueTimeoutCts = new(options.QueueTimeout);
            using CancellationTokenSource linkedQueueCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                queueTimeoutCts.Token);

            try
            {
                await cell.Send(envelope, linkedQueueCts.Token).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "completed"));
                diagnostics.PublishDeadLetter(target, request, "Actor mailbox is completed.");
                throw;
            }
            catch (OperationCanceledException) when (
                queueTimeoutCts.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                ActorCallTimeout timeoutDiagnostic = diagnostics.PublishCallTimeout(
                    caller?.ActorId,
                    target,
                    request,
                    options,
                    Stopwatch.GetElapsedTime(startedAt),
                    ActorCallTimeoutReason.QueueTimeout,
                    callChain);
                TimeoutException exception = diagnostics.CreateCallTimeoutException(
                    timeoutDiagnostic,
                    "The actor call timed out before it could be queued.");
                response.TrySetException(exception);
                throw exception;
            }
        }

        using CancellationTokenSource responseTimeoutCts = new(options.ResponseTimeout);
        using CancellationTokenSource linkedResponseCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            responseTimeoutCts.Token);

        object? result;

        try
        {
            result = await response.Task.WaitAsync(linkedResponseCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            responseTimeoutCts.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            ActorCallTimeout timeoutDiagnostic = diagnostics.PublishCallTimeout(
                caller?.ActorId,
                target,
                request,
                options,
                Stopwatch.GetElapsedTime(startedAt),
                ActorCallTimeoutReason.ResponseTimeout,
                callChain);
            TimeoutException exception = diagnostics.CreateCallTimeoutException(timeoutDiagnostic, "The actor call timed out.");
            response.TrySetException(exception);
            throw exception;
        }

        if (result is null)
        {
            return default!;
        }

        if (result is TResponse typed)
        {
            return typed;
        }

        throw new InvalidCastException($"Actor responded with {result.GetType().FullName}, not {typeof(TResponse).FullName}.");
    }

    public async ValueTask Stop(ActorId target)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!registry.TryGet(target, out ActorCell? cell))
        {
            return;
        }

        try
        {
            await cell.RequestStopAsync().ConfigureAwait(false);
        }
        finally
        {
            RemoveActor(target, cell);
        }
    }

    public async ValueTask<ActorStopResult> Stop(ActorId target, TimeSpan drainTimeout)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (drainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(drainTimeout), "Drain timeout must be greater than zero.");
        }

        if (!registry.TryGet(target, out ActorCell? cell))
        {
            return ActorStopResult.Drained;
        }

        ActorStopResult result;
        Task stopTask = cell.RequestStopAsync();

        try
        {
            await stopTask.WaitAsync(drainTimeout).ConfigureAwait(false);
            result = ActorStopResult.Drained;
        }
        catch (TimeoutException)
        {
            result = ActorStopResult.TimedOut;
        }
        catch
        {
            RemoveActor(target, cell);
            throw;
        }

        if (result == ActorStopResult.Drained)
        {
            RemoveActor(target, cell);
        }
        else
        {
            _ = RemoveActorWhenCompleted(target, cell);
        }

        return result;
    }

    internal ValueTask Stop(ActorRef actorRef)
    {
        ArgumentNullException.ThrowIfNull(actorRef);

        return Stop(actorRef.Id);
    }

    internal ValueTask<ActorStopResult> Stop(ActorRef actorRef, TimeSpan drainTimeout)
    {
        ArgumentNullException.ThrowIfNull(actorRef);

        return Stop(actorRef.Id, drainTimeout);
    }

    public ValueTask Stop(string name)
    {
        ValidateActorName(name);

        if (!registry.TryGetNamed(name, out ActorId id))
        {
            return ValueTask.CompletedTask;
        }

        return Stop(id);
    }

    public ValueTask<ActorStopResult> Stop(string name, TimeSpan drainTimeout)
    {
        ValidateActorName(name);

        if (drainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(drainTimeout), "Drain timeout must be greater than zero.");
        }

        if (!registry.TryGetNamed(name, out ActorId id))
        {
            return ValueTask.FromResult(ActorStopResult.Drained);
        }

        return Stop(id, drainTimeout);
    }

    public MailboxMetrics GetMailboxMetrics(ActorId target)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        ActorCell cell = GetActor(target);
        return cell.GetMailboxMetrics();
    }

    public ActorState GetActorState(ActorId target)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!registry.TryGet(target, out ActorCell? cell))
        {
            return ActorState.Dead;
        }

        return cell.State;
    }

    internal MailboxMetrics GetMailboxMetrics(ActorRef actorRef)
    {
        ArgumentNullException.ThrowIfNull(actorRef);

        return GetMailboxMetrics(actorRef.Id);
    }

    private bool TryGetActor(string name, out ActorRef? actorRef)
    {
        ValidateActorName(name);

        if (registry.TryGetNamed(name, out ActorId id))
        {
            actorRef = new ActorRef(this, id);
            return true;
        }

        actorRef = null;
        return false;
    }

    public bool TryGetActor<TMessage>(string name, out ActorRef<TMessage>? actorRef)
    {
        if (TryGetActor(name, typeof(TMessage), out ActorRef? untyped))
        {
            actorRef = new ActorRef<TMessage>(untyped!);
            return true;
        }

        actorRef = null;
        return false;
    }

    public ActorRef<TMessage> GetActor<TMessage>(string name)
    {
        if (TryGetActor<TMessage>(name, out ActorRef<TMessage>? actorRef))
        {
            return actorRef!;
        }

        throw new InvalidOperationException($"Actor name '{name}' does not exist.");
    }

    internal bool TryGetActor(ActorId target, out ActorCell? cell)
    {
        return registry.TryGet(target, out cell);
    }

    private ActorCell GetActor(ActorId target)
    {
        if (!registry.TryGet(target, out ActorCell? cell))
        {
            throw new InvalidOperationException($"Actor {target} does not exist.");
        }

        return cell;
    }

    private bool TryGetActor(string name, Type messageType, out ActorRef? actorRef)
    {
        ValidateActorName(name);

        if (registry.TryGetNamed(name, out ActorId id, out ActorCell? cell))
        {
            if (cell.MessageType != messageType)
            {
                throw new InvalidOperationException(
                    $"Actor name '{name}' was registered for message type {cell.MessageType.FullName}, not {messageType.FullName}.");
            }

            actorRef = new ActorRef(this, id);
            return true;
        }

        actorRef = null;
        return false;
    }

    private ActorCell GetActorForDelivery(ActorId target, object message)
    {
        if (!registry.TryGet(target, out ActorCell? cell))
        {
            ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "unavailable"));
            diagnostics.PublishDeadLetter(target, message, "Actor does not exist.");
            throw new InvalidOperationException($"Actor {target} does not exist.");
        }

        if (cell.IsStopping)
        {
            ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "stopping"));
            diagnostics.PublishDeadLetter(target, message, "Actor is stopping.");
            throw new InvalidOperationException($"Actor {target} is stopping.");
        }

        return cell;
    }

    private async Task RemoveActorWhenCompleted(ActorId target, ActorCell cell)
    {
        try
        {
            await cell.Completion.ConfigureAwait(false);
        }
        finally
        {
            RemoveActor(target, cell);
        }
    }

    private void RemoveActor(ActorId target, ActorCell cell)
    {
        registry.Remove(target, cell);
    }

    private static void ValidateActorName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
    }

    private IReadOnlyList<ActorId> GetCurrentCallChain()
    {
        return CurrentCallContext?.CallChain ?? Array.Empty<ActorId>();
    }

    private static ActivityContext GetCurrentActivityContext()
    {
        return Activity.Current?.Context ?? default;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        ActorCell[] cells = registry.SnapshotAndClear();

        foreach (ActorCell cell in cells)
        {
            await cell.StopAsync().ConfigureAwait(false);
        }
    }
}
