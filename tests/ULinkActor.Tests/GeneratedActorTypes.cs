namespace ULinkActor.Tests;

public abstract record GeneratedCounterMessage;

public sealed record GeneratedAdd(int Value) : GeneratedCounterMessage;

public sealed record GeneratedGetCounter : GeneratedCounterMessage;

public sealed class GeneratedCounterActor : IActor<GeneratedCounterMessage>
{
    private int value;

    public ValueTask OnMessage(ActorContext ctx, GeneratedCounterMessage message)
    {
        switch (message)
        {
            case GeneratedAdd add:
                value += add.Value;
                break;
            case GeneratedGetCounter:
                ctx.Respond(value);
                break;
        }

        return ValueTask.CompletedTask;
    }
}

[ActorClient]
public interface IGeneratedCounterClient
{
    ValueTask Add(int value);

    ValueTask<int> GetCounter();
}

public sealed class GeneratedCounterClientActor : IActor
{
    private int value;

    public ValueTask OnMessage(ActorContext ctx, object message)
    {
        switch (message)
        {
            case GeneratedCounterClientAddRequest add:
                value += add.Value;
                break;
            case GeneratedCounterClientGetCounterRequest:
                ctx.Respond(value);
                break;
        }

        return ValueTask.CompletedTask;
    }
}
