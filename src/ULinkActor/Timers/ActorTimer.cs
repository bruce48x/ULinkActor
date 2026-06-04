using System.Diagnostics;
using ULinkActor.Messaging;

namespace ULinkActor.Timers;

internal sealed class ActorTimer : IDisposable
{
    private readonly ActorRef target;
    private readonly object message;
    private readonly ActivityContext parentActivityContext;
    private readonly Timer timer;

    internal ActorTimer(
        ActorRef target,
        object message,
        TimeSpan dueTime,
        TimeSpan period,
        ActivityContext parentActivityContext)
    {
        this.target = target;
        this.message = message;
        this.parentActivityContext = parentActivityContext;
        timer = new Timer(OnTick, null, dueTime, period);
    }

    public void Dispose()
    {
        timer.Dispose();
    }

    private void OnTick(object? state)
    {
        try
        {
            target.TrySend(message, parentActivityContext);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
