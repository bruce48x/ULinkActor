using System.Collections.Concurrent;
using System.Diagnostics;
using ULinkActor.Abstractions;
using ULinkActor.Core;
using ULinkActor.Messaging;

namespace ULinkActor;

public sealed class ActorSystem : IDisposable, IAsyncDisposable
{
    private readonly AsyncLocal<ActorCallContext?> currentCallContext = new();
    private readonly ConcurrentDictionary<ActorId, ActorCell> actors = new();
    private readonly ConcurrentDictionary<string, ActorId> names = new(StringComparer.Ordinal);
    private readonly ActorSystemOptions options;
    private long nextActorId;
    private bool disposed;

    public event Action<DeadLetter>? DeadLetterPublished;

    public event Action<SlowMessage>? SlowMessageDetected;

    public event Action<ActorCallTimeout>? CallTimedOut;

    internal ActorCallContext? CurrentCallContext
    {
        get => currentCallContext.Value;
        set => currentCallContext.Value = value;
    }

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

    public ActorRef<TMessage> Spawn<TMessage>(IActor<TMessage> actor)
    {
        return Spawn(actor, null);
    }

    public ActorRef<TMessage> Spawn<TMessage>(string name, IActor<TMessage> actor)
    {
        return Spawn(name, actor, null);
    }

    public ActorRef<TMessage> Spawn<TMessage>(
        string name,
        IActor<TMessage> actor,
        ActorSpawnOptions? spawnOptions)
    {
        ValidateActorName(name);
        ArgumentNullException.ThrowIfNull(actor);

        ActorRef actorRef = SpawnCore(new TypedActorAdapter<TMessage>(actor), typeof(TMessage), spawnOptions, name);
        return new ActorRef<TMessage>(actorRef);
    }

    public ActorRef<TMessage> Spawn<TMessage>(IActor<TMessage> actor, ActorSpawnOptions? spawnOptions)
    {
        ArgumentNullException.ThrowIfNull(actor);

        ActorRef actorRef = SpawnCore(new TypedActorAdapter<TMessage>(actor), typeof(TMessage), spawnOptions, null);
        return new ActorRef<TMessage>(actorRef);
    }

    private ActorRef SpawnCore(IActor actor, Type messageType, ActorSpawnOptions? spawnOptions, string? name)
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

        if (!actors.TryAdd(id, cell))
        {
            throw new InvalidOperationException($"Actor id {id} already exists.");
        }

        if (name is not null && !names.TryAdd(name, id))
        {
            actors.TryRemove(id, out _);
            cell.Complete();
            throw new InvalidOperationException($"Actor name '{name}' already exists.");
        }

        try
        {
            cell.StartAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            actors.TryRemove(id, out _);

            if (name is not null)
            {
                names.TryRemove(name, out _);
            }

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
            PublishDeadLetter(target, message, "Actor mailbox is completed.");
            throw;
        }
    }

    internal ActorSendResult TrySend(ActorId target, object message)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(message);

        if (!actors.TryGetValue(target, out ActorCell? cell))
        {
            ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "unavailable"));
            PublishDeadLetter(target, message, "Actor does not exist.");
            return ActorSendResult.ActorUnavailable;
        }

        if (cell.TrySend(new Envelope(
            message,
            callChain: GetCurrentCallChain(),
            parentActivityContext: GetCurrentActivityContext())))
        {
            return ActorSendResult.Accepted;
        }

        string reason = cell.Completion.IsCompleted
            ? "Actor mailbox is completed."
            : "Actor mailbox is full.";

        ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>(
            "reason",
            cell.Completion.IsCompleted ? "completed" : "full"));
        PublishDeadLetter(target, message, reason);

        return cell.Completion.IsCompleted
            ? ActorSendResult.ActorUnavailable
            : ActorSendResult.MailboxFull;
    }

    internal async ValueTask<TResponse> Call<TResponse>(
        ActorId target,
        object request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be greater than zero.");
        }

        ActorCell cell = GetActorForDelivery(target, request);
        TaskCompletionSource<object?> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ActorCallContext? caller = CurrentCallContext;
        IReadOnlyList<ActorId> callChain = caller?.CallChain ?? Array.Empty<ActorId>();
        ActorCallTimeoutReason timeoutReason = callChain.Contains(target)
            ? ActorCallTimeoutReason.CircularWait
            : ActorCallTimeoutReason.ResponseTimeout;
        ULinkActorDiagnostics.CallStartedCounter.Add(1);

        using CancellationTokenSource timeoutCts = new(timeout);
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        CallTimeoutRegistrationState timeoutState = new(
            this,
            response,
            caller?.ActorId,
            target,
            request,
            timeout,
            timeoutReason,
            callChain);

        using CancellationTokenRegistration timeoutRegistration = timeoutCts.Token.Register(static state =>
        {
            ((CallTimeoutRegistrationState)state!).PublishTimeout();
        }, timeoutState);

        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(static state =>
        {
            ((TaskCompletionSource<object?>)state!).TrySetCanceled();
        }, response);

        try
        {
            await cell.Send(
                new Envelope(request, response, callChain, GetCurrentActivityContext()),
                linkedCts.Token).ConfigureAwait(false);
            timeoutState.MarkQueued();
        }
        catch (InvalidOperationException)
        {
            ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "completed"));
            PublishDeadLetter(target, request, "Actor mailbox is completed.");
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            ActorCallTimeout timeoutDiagnostic = timeoutState.PublishTimeout();
            throw CreateCallTimeoutException(timeoutDiagnostic, "The actor call timed out before it could be queued.");
        }

        object? result = await response.Task.ConfigureAwait(false);

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

        if (!actors.TryGetValue(target, out ActorCell? cell))
        {
            return;
        }

        bool beganStop = await cell.BeginStopAsync().ConfigureAwait(false);

        actors.TryRemove(target, out _);

        if (cell.Name is not null)
        {
            names.TryRemove(cell.Name, out _);
        }

        if (beganStop)
        {
            cell.Complete();
        }

        await cell.Completion.ConfigureAwait(false);
    }

    public async ValueTask<ActorStopResult> Stop(ActorId target, TimeSpan drainTimeout)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (drainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(drainTimeout), "Drain timeout must be greater than zero.");
        }

        if (!actors.TryGetValue(target, out ActorCell? cell))
        {
            return ActorStopResult.Drained;
        }

        bool beganStop = await cell.BeginStopAsync().ConfigureAwait(false);

        actors.TryRemove(target, out _);

        if (cell.Name is not null)
        {
            names.TryRemove(cell.Name, out _);
        }

        if (beganStop)
        {
            cell.Complete();
        }

        try
        {
            await cell.Completion.WaitAsync(drainTimeout).ConfigureAwait(false);
            return ActorStopResult.Drained;
        }
        catch (TimeoutException)
        {
            return ActorStopResult.TimedOut;
        }
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

        if (!names.TryGetValue(name, out ActorId id))
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

        if (!names.TryGetValue(name, out ActorId id))
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

    internal MailboxMetrics GetMailboxMetrics(ActorRef actorRef)
    {
        ArgumentNullException.ThrowIfNull(actorRef);

        return GetMailboxMetrics(actorRef.Id);
    }

    private bool TryGetActor(string name, out ActorRef? actorRef)
    {
        ValidateActorName(name);

        if (names.TryGetValue(name, out ActorId id) && actors.ContainsKey(id))
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
        return actors.TryGetValue(target, out cell);
    }

    private ActorCell GetActor(ActorId target)
    {
        if (!actors.TryGetValue(target, out ActorCell? cell))
        {
            throw new InvalidOperationException($"Actor {target} does not exist.");
        }

        return cell;
    }

    private bool TryGetActor(string name, Type messageType, out ActorRef? actorRef)
    {
        if (TryGetActor(name, out ActorRef? untyped))
        {
            ActorCell cell = GetActor(untyped!.Id);

            if (cell.MessageType != messageType)
            {
                throw new InvalidOperationException(
                    $"Actor name '{name}' was registered for message type {cell.MessageType.FullName}, not {messageType.FullName}.");
            }

            actorRef = untyped;
            return true;
        }

        actorRef = null;
        return false;
    }

    private ActorCell GetActorForDelivery(ActorId target, object message)
    {
        if (!actors.TryGetValue(target, out ActorCell? cell))
        {
            ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "unavailable"));
            PublishDeadLetter(target, message, "Actor does not exist.");
            throw new InvalidOperationException($"Actor {target} does not exist.");
        }

        return cell;
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

    private void PublishDeadLetter(ActorId target, object message, string reason)
    {
        ULinkActorDiagnostics.DeadLetterCounter.Add(1, new KeyValuePair<string, object?>(
            "reason",
            GetDeadLetterMetricReason(reason)));

        Action<DeadLetter>? handlers = DeadLetterPublished;

        if (handlers is null)
        {
            return;
        }

        DeadLetter deadLetter = new(target, message, reason);

        foreach (Action<DeadLetter> handler in handlers.GetInvocationList().Cast<Action<DeadLetter>>())
        {
            try
            {
                handler(deadLetter);
            }
            catch
            {
            }
        }
    }

    internal void PublishSlowMessage(ActorId actorId, object message, TimeSpan elapsed)
    {
        Action<SlowMessage>? handlers = SlowMessageDetected;

        if (handlers is null)
        {
            return;
        }

        SlowMessage slowMessage = new(actorId, message, elapsed);

        foreach (Action<SlowMessage> handler in handlers.GetInvocationList().Cast<Action<SlowMessage>>())
        {
            try
            {
                handler(slowMessage);
            }
            catch
            {
            }
        }
    }

    internal ActorCallTimeout CreateCallTimeout(
        ActorId? caller,
        ActorId target,
        object request,
        TimeSpan timeout,
        ActorCallTimeoutReason reason,
        IReadOnlyList<ActorId> callChain)
    {
        ActorId[] snapshot = callChain.ToArray();
        return new ActorCallTimeout(caller, target, request, timeout, reason, snapshot);
    }

    internal TimeoutException CreateCallTimeoutException(ActorCallTimeout timeout, string message)
    {
        string chain = timeout.CallChain.Count == 0
            ? "<external>"
            : string.Join(" -> ", timeout.CallChain.Select(id => id.Value));

        return new TimeoutException(
            $"{message} Target={timeout.Target.Value}; Caller={timeout.Caller?.Value.ToString() ?? "<external>"}; Reason={timeout.Reason}; Chain={chain}.");
    }

    internal void PublishCallTimeout(ActorCallTimeout timeout)
    {
        ULinkActorDiagnostics.CallTimeoutCounter.Add(1, new KeyValuePair<string, object?>(
            "reason",
            timeout.Reason.ToString()));

        Action<ActorCallTimeout>? handlers = CallTimedOut;

        if (handlers is null)
        {
            return;
        }

        foreach (Action<ActorCallTimeout> handler in handlers.GetInvocationList().Cast<Action<ActorCallTimeout>>())
        {
            try
            {
                handler(timeout);
            }
            catch
            {
            }
        }
    }

    private static string GetDeadLetterMetricReason(string reason)
    {
        return reason switch
        {
            "Actor does not exist." => "unavailable",
            "Actor mailbox is completed." => "completed",
            "Actor mailbox is full." => "full",
            _ => "other"
        };
    }

    private sealed class CallTimeoutRegistrationState
    {
        private readonly ActorSystem system;
        private readonly TaskCompletionSource<object?> response;
        private readonly ActorId? caller;
        private readonly ActorId target;
        private readonly object request;
        private readonly TimeSpan timeout;
        private readonly ActorCallTimeoutReason responseTimeoutReason;
        private readonly IReadOnlyList<ActorId> callChain;
        private readonly object publishGate = new();
        private ActorCallTimeout? timeoutDiagnostic;
        private int queued;

        public CallTimeoutRegistrationState(
            ActorSystem system,
            TaskCompletionSource<object?> response,
            ActorId? caller,
            ActorId target,
            object request,
            TimeSpan timeout,
            ActorCallTimeoutReason responseTimeoutReason,
            IReadOnlyList<ActorId> callChain)
        {
            this.system = system;
            this.response = response;
            this.caller = caller;
            this.target = target;
            this.request = request;
            this.timeout = timeout;
            this.responseTimeoutReason = responseTimeoutReason;
            this.callChain = callChain;
        }

        public void MarkQueued()
        {
            Volatile.Write(ref queued, 1);
        }

        public ActorCallTimeout PublishTimeout()
        {
            lock (publishGate)
            {
                if (timeoutDiagnostic is not null)
                {
                    return timeoutDiagnostic;
                }

                ActorCallTimeoutReason reason = Volatile.Read(ref queued) == 0
                    ? ActorCallTimeoutReason.QueueTimeout
                    : responseTimeoutReason;

                timeoutDiagnostic = system.CreateCallTimeout(
                    caller,
                    target,
                    request,
                    timeout,
                    reason,
                    callChain);

                system.PublishCallTimeout(timeoutDiagnostic);
                response.TrySetException(system.CreateCallTimeoutException(timeoutDiagnostic, "The actor call timed out."));
                return timeoutDiagnostic;
            }
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        ActorCell[] cells = actors.Values.ToArray();
        actors.Clear();
        names.Clear();

        foreach (ActorCell cell in cells)
        {
            await cell.StopAsync().ConfigureAwait(false);
        }
    }
}
