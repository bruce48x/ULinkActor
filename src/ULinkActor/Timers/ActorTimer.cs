using System.Diagnostics;

namespace ULinkActor;

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
        _ = SendTick();
    }

    private async Task SendTick()
    {
        try
        {
            await target.Send(message, parentActivityContext).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
