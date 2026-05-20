using System.Collections.Concurrent;

namespace ULinkActor;

public sealed class ActorSystem : IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<ActorId, ActorCell> actors = new();
    private readonly ConcurrentDictionary<string, ActorId> names = new(StringComparer.Ordinal);
    private readonly ActorSystemOptions options;
    private long nextActorId;
    private bool disposed;

    public event Action<DeadLetter>? DeadLetterPublished;

    public event Action<SlowMessage>? SlowMessageDetected;

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

        return actorRef;
    }

    internal async ValueTask Send(ActorId target, object message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(message);

        ActorCell cell = GetActorForDelivery(target, message);

        try
        {
            await cell.Send(new Envelope(message), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            PublishDeadLetter(target, message, "Actor mailbox is completed.");
            throw;
        }
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

        using CancellationTokenSource timeoutCts = new(timeout);
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        using CancellationTokenRegistration timeoutRegistration = timeoutCts.Token.Register(static state =>
        {
            ((TaskCompletionSource<object?>)state!).TrySetException(new TimeoutException("The actor call timed out."));
        }, response);

        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(static state =>
        {
            ((TaskCompletionSource<object?>)state!).TrySetCanceled();
        }, response);

        try
        {
            await cell.Send(new Envelope(request, response), linkedCts.Token).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            PublishDeadLetter(target, request, "Actor mailbox is completed.");
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The actor call timed out before it could be queued.");
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

        if (!actors.TryRemove(target, out ActorCell? cell))
        {
            return;
        }

        if (cell.Name is not null)
        {
            names.TryRemove(cell.Name, out _);
        }

        await cell.StopAsync().ConfigureAwait(false);
    }

    internal ValueTask Stop(ActorRef actorRef)
    {
        ArgumentNullException.ThrowIfNull(actorRef);

        return Stop(actorRef.Id);
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

    public ActorGroup<TMessage> CreateGroup<TMessage>(params ActorRef<TMessage>[] actorRefs)
    {
        return CreateGroup((IEnumerable<ActorRef<TMessage>>)actorRefs);
    }

    public ActorGroup<TMessage> CreateGroup<TMessage>(IEnumerable<ActorRef<TMessage>> actorRefs)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(actorRefs);

        return new ActorGroup<TMessage>(actorRefs);
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
            PublishDeadLetter(target, message, "Actor does not exist.");
            throw new InvalidOperationException($"Actor {target} does not exist.");
        }

        return cell;
    }

    private static void ValidateActorName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
    }

    private void PublishDeadLetter(ActorId target, object message, string reason)
    {
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
