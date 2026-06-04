using System.Runtime.ExceptionServices;
using ULinkActor.Abstractions;
using ULinkActor.Lifecycle;
using ULinkActor.Messaging;
using MailboxCore = ULinkActor.Mailbox.Mailbox;

namespace ULinkActor.Core;

internal sealed class ActorCell
{
    private readonly IActor actor;
    private readonly ActorTimerSet timers = new();
    private readonly ActorTurnRunner turnRunner;
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
        Self = self;
        this.actor = actor;
        Name = name;
        MessageType = messageType;
        turnRunner = new ActorTurnRunner(system, this, self, actor, slowMessageThreshold);
        Mailbox = new MailboxCore(turnRunner.Dispatch, mailboxCapacity);
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
        ActorContextCore context = new(Self, this, new Envelope(ActorLifecycleMessage.Started));
        await actor.OnStarted(context).ConfigureAwait(false);
    }

    internal void AddTimer(IDisposable timer)
    {
        timers.Add(timer);
    }

    private void DisposeTimers()
    {
        timers.DisposeAll();
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

}
