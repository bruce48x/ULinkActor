using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using ULinkActor.Abstractions;
using ULinkActor.Lifecycle;
using ULinkActor.Messaging;
using MailboxCore = ULinkActor.Mailbox.Mailbox;

namespace ULinkActor.Core;

internal sealed class ActorCell
{
    private readonly IActor actor;
    private readonly ConcurrentBag<IDisposable> timers = new();
    private readonly ActorSystem system;
    private readonly TimeSpan? slowMessageThreshold;
    private readonly object stopGate = new();
    private Task? stopTask;
    private int stopping;

    public ActorCell(
        ActorSystem system,
        ActorRef self,
        IActor actor,
        Type messageType,
        int mailboxCapacity,
        TimeSpan? slowMessageThreshold,
        string? name)
    {
        this.system = system;
        Self = self;
        this.actor = actor;
        MessageType = messageType;
        this.slowMessageThreshold = slowMessageThreshold;
        Name = name;
        Mailbox = new MailboxCore(Dispatch, mailboxCapacity);
    }

    internal ActorRef Self { get; }

    internal Type MessageType { get; }

    internal string? Name { get; }

    internal MailboxCore Mailbox { get; }

    internal Task Completion => Mailbox.Completion;

    internal bool IsStopping => Volatile.Read(ref stopping) != 0;

    internal ActorState State =>
        Volatile.Read(ref stopping) == 0 ? ActorState.Active
        : Completion.IsCompleted ? ActorState.Dead
        : ActorState.Draining;

    public MailboxMetrics GetMailboxMetrics()
    {
        return Mailbox.GetMetrics();
    }

    public ValueTask Send(Envelope envelope, CancellationToken cancellationToken)
    {
        return Mailbox.Send(envelope, cancellationToken);
    }

    public bool TrySend(Envelope envelope)
    {
        return Mailbox.TrySend(envelope);
    }

    public async ValueTask StartAsync()
    {
        ActorContextCore context = new(system, Self, this, new Envelope(ActorLifecycleMessage.Started));
        await actor.OnStarted(context).ConfigureAwait(false);
    }

    public void AddTimer(IDisposable timer)
    {
        timers.Add(timer);
    }

    public void DisposeTimers()
    {
        foreach (IDisposable timer in timers)
        {
            timer.Dispose();
        }
    }

    public void Complete()
    {
        Mailbox.Complete();
    }

    public async ValueTask StopAsync()
    {
        await RequestStopAsync(runStoppingHook: false).ConfigureAwait(false);
    }

    public async ValueTask<ActorStopResult> StopAsync(TimeSpan drainTimeout)
    {
        if (drainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(drainTimeout), "Drain timeout must be greater than zero.");
        }

        return await WaitForStop(RequestStopAsync(runStoppingHook: false), drainTimeout).ConfigureAwait(false);
    }

    public Task RequestStopAsync(bool runStoppingHook = true)
    {
        lock (stopGate)
        {
            if (stopTask is not null)
            {
                return stopTask;
            }

            Interlocked.Exchange(ref stopping, 1);
            DisposeTimers();
            stopTask = RunStopSequenceAsync(runStoppingHook);
            return stopTask;
        }
    }

    private async Task RunStopSequenceAsync(bool runStoppingHook)
    {
        Exception? stopError = null;

        try
        {
            if (runStoppingHook)
            {
                TaskCompletionSource<object?> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await Mailbox.Send(new Envelope(ActorLifecycleMessage.Stopping, stopped), CancellationToken.None).ConfigureAwait(false);
                await stopped.Task.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            stopError = ex;
        }
        finally
        {
            Complete();
            await Completion.ConfigureAwait(false);
        }

        if (stopError is not null)
        {
            ExceptionDispatchInfo.Capture(stopError).Throw();
        }
    }

    private static async ValueTask<ActorStopResult> WaitForStop(Task stopTask, TimeSpan drainTimeout)
    {
        try
        {
            await stopTask.WaitAsync(drainTimeout).ConfigureAwait(false);
            return ActorStopResult.Drained;
        }
        catch (TimeoutException)
        {
            return ActorStopResult.TimedOut;
        }
    }

    private async ValueTask Dispatch(Envelope envelope)
    {
        ActorContextCore context = new(system, Self, this, envelope);
        ActorCallContext? previousCallContext = system.CurrentCallContext;
        IReadOnlyList<ActorId> callChain = AppendCallChain(envelope.CallChain, Self.Id);
        long startedAt = slowMessageThreshold is null ? 0 : Stopwatch.GetTimestamp();
        string messageType = envelope.Message.GetType().FullName ?? envelope.Message.GetType().Name;
        IActorMessageInterceptor? interceptor = system.MessageInterceptor;

        using Activity? activity = StartDispatchActivity(envelope);

        activity?.SetTag("ulinkactor.actor.id", Self.Id.Value);
        activity?.SetTag("ulinkactor.message.type", messageType);
        activity?.SetTag("ulinkactor.message.kind", envelope.Response is null ? "send" : "call");
        activity?.SetTag("ulinkactor.call.chain", string.Join(">", callChain.Select(id => id.Value)));

        Exception? error = null;

        try
        {
            if (interceptor is not null)
            {
                await interceptor.OnBeforeMessage(Self.Id, envelope.Message, CancellationToken.None).ConfigureAwait(false);
            }

            system.CurrentCallContext = new ActorCallContext(Self.Id, callChain);

            if (envelope.Message is ActorLifecycleMessage.Stopping)
            {
                await actor.OnStopping(context).ConfigureAwait(false);
                envelope.Response?.TrySetResult(null);
            }
            else
            {
                await actor.OnMessage(context, envelope.Message).ConfigureAwait(false);
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            error = ex;
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("exception.type", ex.GetType().FullName);
            activity?.SetTag("exception.message", ex.Message);
            envelope.Response?.TrySetException(ex);
        }
        finally
        {
            system.CurrentCallContext = previousCallContext;
            ULinkActorDiagnostics.MessageProcessedCounter.Add(1, CreateMessageKindTag(envelope));

            if (slowMessageThreshold is not null)
            {
                TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);

                if (elapsed >= slowMessageThreshold.Value)
                {
                    activity?.AddEvent(new ActivityEvent(
                        "ULinkActor.Actor.SlowMessage",
                        tags: new ActivityTagsCollection
                        {
                            ["ulinkactor.slow_message.elapsed_ms"] = elapsed.TotalMilliseconds
                        }));
                    activity?.SetTag("ulinkactor.slow_message", true);
                    activity?.SetTag("ulinkactor.slow_message.elapsed_ms", elapsed.TotalMilliseconds);
                    system.PublishSlowMessage(Self.Id, envelope.Message, elapsed);
                }
            }

            if (interceptor is not null)
            {
                try
                {
                    await interceptor.OnAfterMessage(Self.Id, envelope.Message, error, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Swallow interceptor errors to avoid crashing the mailbox.
                }
            }
        }
    }

    private static IReadOnlyList<ActorId> AppendCallChain(IReadOnlyList<ActorId> callChain, ActorId actorId)
    {
        ActorId[] next = new ActorId[callChain.Count + 1];

        for (int i = 0; i < callChain.Count; i++)
        {
            next[i] = callChain[i];
        }

        next[^1] = actorId;
        return next;
    }

    private static KeyValuePair<string, object?> CreateMessageKindTag(Envelope envelope)
    {
        return new KeyValuePair<string, object?>("kind", envelope.Response is null ? "send" : "call");
    }

    private static Activity? StartDispatchActivity(Envelope envelope)
    {
        if (envelope.ParentActivityContext.TraceId != default)
        {
            return ULinkActorDiagnostics.ActivitySource.StartActivity(
                "ULinkActor.Actor.Dispatch",
                ActivityKind.Internal,
                envelope.ParentActivityContext);
        }

        return ULinkActorDiagnostics.ActivitySource.StartActivity(
            "ULinkActor.Actor.Dispatch",
            ActivityKind.Internal);
    }
}
