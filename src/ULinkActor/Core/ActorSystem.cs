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
    private readonly ActorMessageDispatcher dispatcher;
    private readonly ActorSpawner spawner;
    private readonly ActorSystemOptions options;
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
        dispatcher = new ActorMessageDispatcher(registry, diagnostics, () => CurrentCallContext);
        spawner = new ActorSpawner(this, registry, options);
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
        ObjectDisposedException.ThrowIf(disposed, this);

        ActorRef actorRef = await spawner.SpawnAsync(
            new TypedActorAdapter<TMessage>(actor),
            typeof(TMessage),
            spawnOptions,
            name).ConfigureAwait(false);
        return new ActorHandle<TMessage>(actorRef);
    }

    public async ValueTask<ActorHandle<TMessage>> SpawnAsync<TMessage>(IActor<TMessage> actor, ActorSpawnOptions? spawnOptions)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ObjectDisposedException.ThrowIf(disposed, this);

        ActorRef actorRef = await spawner.SpawnAsync(
            new TypedActorAdapter<TMessage>(actor),
            typeof(TMessage),
            spawnOptions,
            null).ConfigureAwait(false);
        return new ActorHandle<TMessage>(actorRef);
    }

    internal ValueTask Send(ActorId target, object message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        return dispatcher.Send(target, message, cancellationToken);
    }

    internal ValueTask Send(
        ActorId target,
        object message,
        ActivityContext parentActivityContext,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        return dispatcher.Send(target, message, parentActivityContext, cancellationToken);
    }

    internal ActorSendResult TrySend(
        ActorId target,
        object message)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        return dispatcher.TrySend(target, message);
    }

    internal ActorSendResult TrySend(
        ActorId target,
        object message,
        ActivityContext parentActivityContext)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        return dispatcher.TrySend(target, message, parentActivityContext);
    }

    internal ValueTask<TResponse> Call<TResponse>(
        ActorId target,
        object request,
        ActorCallOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        return dispatcher.Call<TResponse>(target, request, options, cancellationToken);
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
