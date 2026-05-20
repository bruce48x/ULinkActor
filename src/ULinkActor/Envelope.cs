namespace ULinkActor;

internal sealed class Envelope
{
    public Envelope(object message, TaskCompletionSource<object?>? response = null)
    {
        Message = message;
        Response = response;
    }

    public object Message { get; }

    public TaskCompletionSource<object?>? Response { get; }
}
