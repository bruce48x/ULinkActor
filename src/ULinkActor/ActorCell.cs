using System.Collections.Concurrent;
using System.Diagnostics;

namespace ULinkActor;

internal sealed class ActorCell
{
    private readonly IActor actor;
    private readonly ConcurrentBag<IDisposable> timers = new();
    private readonly ActorSystem system;
    private readonly TimeSpan? slowMessageThreshold;
    private int stopping;

    public ActorCell(
        ActorSystem system,
        ActorRef self,
        IActor actor,
        int mailboxCapacity,
        TimeSpan? slowMessageThreshold,
        string? name)
    {
        this.system = system;
        Self = self;
        this.actor = actor;
        this.slowMessageThreshold = slowMessageThreshold;
        Name = name;
        Mailbox = new Mailbox(Dispatch, mailboxCapacity);
    }

    public ActorRef Self { get; }

    public string? Name { get; }

    public Mailbox Mailbox { get; }

    public Task Completion => Mailbox.Completion;

    public bool IsStopping => Volatile.Read(ref stopping) != 0;

    public MailboxMetrics GetMailboxMetrics()
    {
        return Mailbox.GetMetrics();
    }

    public ValueTask Send(Envelope envelope, CancellationToken cancellationToken)
    {
        return Mailbox.Send(envelope, cancellationToken);
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
        if (Interlocked.Exchange(ref stopping, 1) != 0)
        {
            return;
        }

        DisposeTimers();
        Complete();
        await Completion.ConfigureAwait(false);
    }

    private async ValueTask Dispatch(Envelope envelope)
    {
        ActorContext context = new(system, Self, this, envelope);
        long startedAt = slowMessageThreshold is null ? 0 : Stopwatch.GetTimestamp();
        string messageType = envelope.Message.GetType().FullName ?? envelope.Message.GetType().Name;

        using Activity? activity = ULinkActorDiagnostics.ActivitySource.StartActivity(
            "ULinkActor.Actor.Dispatch",
            ActivityKind.Internal);

        activity?.SetTag("ulinkactor.actor.id", Self.Id.Value);
        activity?.SetTag("ulinkactor.message.type", messageType);
        activity?.SetTag("ulinkactor.message.kind", envelope.Response is null ? "send" : "call");

        try
        {
            await actor.OnMessage(context, envelope.Message).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("exception.type", ex.GetType().FullName);
            activity?.SetTag("exception.message", ex.Message);
            envelope.Response?.TrySetException(ex);
        }
        finally
        {
            if (slowMessageThreshold is not null)
            {
                TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);

                if (elapsed >= slowMessageThreshold.Value)
                {
                    system.PublishSlowMessage(Self.Id, envelope.Message, elapsed);
                }
            }
        }
    }
}
