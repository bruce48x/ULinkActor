using System.Diagnostics;

namespace ULinkActor.Tests;

public sealed class ActorSystemTests
{
    [Fact]
    public async Task Send_dispatches_message()
    {
        using ActorSystem system = new();
        ProbeActor actor = new();
        ActorRef actorRef = system.Spawn(actor);

        await actorRef.Send("hello");

        Assert.Equal("hello", await actor.NextMessage());
    }

    [Fact]
    public async Task Call_returns_actor_response()
    {
        using ActorSystem system = new();
        ActorRef actorRef = system.Spawn(new EchoActor());

        string response = await actorRef.Call<string>("ping", TimeSpan.FromSeconds(1));

        Assert.Equal("ping", response);
    }

    [Fact]
    public async Task Call_times_out_when_actor_does_not_respond()
    {
        using ActorSystem system = new();
        ActorRef actorRef = system.Spawn(new IgnoringActor());

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await actorRef.Call<string>("ping", TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public async Task Mailbox_processes_messages_in_send_order()
    {
        using ActorSystem system = new();
        ActorRef actorRef = system.Spawn(new OrderingActor());

        for (int i = 0; i < 64; i++)
        {
            await actorRef.Send(i);
        }

        int[] values = await actorRef.Call<int[]>(new GetValues(), TimeSpan.FromSeconds(1));

        Assert.Equal(Enumerable.Range(0, 64), values);
    }

    [Fact]
    public async Task Mailbox_never_executes_same_actor_concurrently()
    {
        using ActorSystem system = new();
        ActorRef actorRef = system.Spawn(new ConcurrencyProbeActor());

        Task[] sends = Enumerable.Range(0, 32)
            .Select(i => actorRef.Send(i).AsTask())
            .ToArray();

        await Task.WhenAll(sends);

        int maxConcurrency = await actorRef.Call<int>(new GetMaxConcurrency(), TimeSpan.FromSeconds(1));

        Assert.Equal(1, maxConcurrency);
    }

    [Fact]
    public async Task Timer_messages_are_dispatched_through_mailbox()
    {
        using ActorSystem system = new();
        ActorRef actorRef = system.Spawn(new TimerActor());

        await actorRef.Send(new StartTimer());
        await Task.Delay(80);

        int ticks = await actorRef.Call<int>(new GetTickCount(), TimeSpan.FromSeconds(1));

        Assert.True(ticks > 0);
    }

    [Fact]
    public async Task Bounded_mailbox_applies_backpressure()
    {
        using ActorSystem system = new(new ActorSystemOptions { MailboxCapacity = 1 });
        BlockingActor actor = new();
        ActorRef actorRef = system.Spawn(actor);

        await actorRef.Send("first");

        Task secondSend = actorRef.Send("second").AsTask();
        Task completed = await Task.WhenAny(secondSend, Task.Delay(50));

        Assert.NotSame(secondSend, completed);

        actor.Release();
        await secondSend.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Spawn_options_can_override_mailbox_capacity()
    {
        using ActorSystem system = new(new ActorSystemOptions { MailboxCapacity = 1 });
        BlockingActor actor = new();
        ActorRef actorRef = system.Spawn(actor, new ActorSpawnOptions { MailboxCapacity = 2 });

        await actorRef.Send("first");
        await actorRef.Send("second");

        Task thirdSend = actorRef.Send("third").AsTask();
        Task completed = await Task.WhenAny(thirdSend, Task.Delay(50));

        Assert.NotSame(thirdSend, completed);
        Assert.Equal(2, actorRef.GetMailboxMetrics().Capacity);

        actor.Release();
        await thirdSend.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Mailbox_metrics_report_capacity_queue_and_counts()
    {
        using ActorSystem system = new(new ActorSystemOptions { MailboxCapacity = 2 });
        BlockingActor actor = new();
        ActorRef actorRef = system.Spawn(actor);

        await actorRef.Send("first");
        await actorRef.Send("second");

        MailboxMetrics queuedMetrics = actorRef.GetMailboxMetrics();

        Assert.Equal(2, queuedMetrics.Capacity);
        Assert.Equal(1, queuedMetrics.QueuedCount);
        Assert.Equal(2, queuedMetrics.EnqueuedCount);
        Assert.Equal(0, queuedMetrics.ProcessedCount);
        Assert.False(queuedMetrics.IsCompleted);

        actor.Release();
        await Eventually(() => actorRef.GetMailboxMetrics().ProcessedCount == 2);
    }

    [Fact]
    public async Task Slow_message_detection_publishes_event_when_threshold_is_exceeded()
    {
        using ActorSystem system = new(new ActorSystemOptions
        {
            SlowMessageThreshold = TimeSpan.FromMilliseconds(10)
        });

        TaskCompletionSource<SlowMessage> detected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        system.SlowMessageDetected += message => detected.TrySetResult(message);
        ActorRef actorRef = system.Spawn(new SlowActor(TimeSpan.FromMilliseconds(30)));

        await actorRef.Send("slow");

        SlowMessage slowMessage = await detected.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(actorRef.Id, slowMessage.ActorId);
        Assert.Equal("slow", slowMessage.Message);
        Assert.True(slowMessage.Elapsed >= TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public async Task Typed_actor_ref_sends_and_calls_typed_messages()
    {
        using ActorSystem system = new();
        ActorRef<CounterMessage> counter = system.Spawn<CounterMessage>(new CounterActor());

        await counter.Send(new Add(2));
        int value = await counter.Call<int>(new GetCounter(), TimeSpan.FromSeconds(1));

        Assert.Equal(2, value);
    }

    [Fact]
    public async Task Typed_actor_ref_exposes_runtime_operations()
    {
        using ActorSystem system = new();
        ActorRef<CounterMessage> counter = system.Spawn<CounterMessage>(
            new CounterActor(),
            new ActorSpawnOptions { MailboxCapacity = 4 });

        Assert.Equal(4, counter.GetMailboxMetrics().Capacity);

        await counter.Stop();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await counter.Send(new Add(1)));
    }

    [Fact]
    public async Task Dispatch_emits_activity_for_tracing()
    {
        TaskCompletionSource<Activity> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == ULinkActorDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "ULinkActor.Actor.Dispatch")
                {
                    stopped.TrySetResult(activity);
                }
            }
        };

        ActivitySource.AddActivityListener(listener);

        using ActorSystem system = new();
        ActorRef actorRef = system.Spawn(new EchoActor());

        string response = await actorRef.Call<string>("trace-me", TimeSpan.FromSeconds(1));
        Activity activity = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("trace-me", response);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.Equal(actorRef.Id.Value, activity.GetTagItem("ulinkactor.actor.id"));
        Assert.Equal(typeof(string).FullName, activity.GetTagItem("ulinkactor.message.type"));
        Assert.Equal("call", activity.GetTagItem("ulinkactor.message.kind"));
    }

    [Fact]
    public async Task Named_actor_can_be_resolved_and_used()
    {
        using ActorSystem system = new();
        ActorRef spawned = system.Spawn("echo", new EchoActor());

        ActorRef resolved = system.GetActor("echo");
        string response = await resolved.Call<string>("named", TimeSpan.FromSeconds(1));

        Assert.Equal(spawned.Id, resolved.Id);
        Assert.Equal("named", response);
    }

    [Fact]
    public void Duplicate_actor_name_is_rejected()
    {
        using ActorSystem system = new();

        system.Spawn("echo", new EchoActor());

        Assert.Throws<InvalidOperationException>(() => system.Spawn("echo", new EchoActor()));
    }

    [Fact]
    public async Task Stop_by_name_removes_named_actor_from_registry()
    {
        using ActorSystem system = new();
        ActorRef actorRef = system.Spawn("echo", new EchoActor());

        await system.Stop("echo");

        Assert.False(system.TryGetActor("echo", out _));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await actorRef.Send("late"));
    }

    [Fact]
    public async Task Typed_named_actor_can_be_resolved_and_used()
    {
        using ActorSystem system = new();
        ActorRef<CounterMessage> spawned = system.Spawn<CounterMessage>("counter", new CounterActor());

        ActorRef<CounterMessage> resolved = system.GetActor<CounterMessage>("counter");
        await resolved.Send(new Add(3));
        int value = await spawned.Call<int>(new GetCounter(), TimeSpan.FromSeconds(1));

        Assert.Equal(spawned.Id, resolved.Id);
        Assert.Equal(3, value);
    }

    [Fact]
    public async Task Actor_group_broadcasts_send_to_members()
    {
        using ActorSystem system = new();
        ActorRef first = system.Spawn(new OrderingActor());
        ActorRef second = system.Spawn(new OrderingActor());
        ActorGroup group = system.CreateGroup(first, second);

        await group.Send(7);

        Assert.Equal(new[] { 7 }, await first.Call<int[]>(new GetValues(), TimeSpan.FromSeconds(1)));
        Assert.Equal(new[] { 7 }, await second.Call<int[]>(new GetValues(), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Actor_group_deduplicates_members()
    {
        using ActorSystem system = new();
        ActorRef actorRef = system.Spawn(new OrderingActor());
        ActorGroup group = system.CreateGroup(actorRef, actorRef);

        await group.Send(1);

        int[] values = await actorRef.Call<int[]>(new GetValues(), TimeSpan.FromSeconds(1));

        Assert.Single(values);
        Assert.Equal(1, group.Count);
    }

    [Fact]
    public async Task Actor_group_stops_all_members()
    {
        using ActorSystem system = new();
        ActorRef first = system.Spawn(new IgnoringActor());
        ActorRef second = system.Spawn(new IgnoringActor());
        ActorGroup group = system.CreateGroup(first, second);

        await group.Stop();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await first.Send("late"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.Send("late"));
    }

    [Fact]
    public async Task Typed_actor_group_broadcasts_typed_messages()
    {
        using ActorSystem system = new();
        ActorRef<CounterMessage> first = system.Spawn<CounterMessage>(new CounterActor());
        ActorRef<CounterMessage> second = system.Spawn<CounterMessage>(new CounterActor());
        ActorGroup<CounterMessage> group = system.CreateGroup(first, second);

        await group.Send(new Add(5));

        Assert.Equal(5, await first.Call<int>(new GetCounter(), TimeSpan.FromSeconds(1)));
        Assert.Equal(5, await second.Call<int>(new GetCounter(), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Source_generator_creates_typed_spawn_extension()
    {
        using ActorSystem system = new();

        ActorRef<GeneratedCounterMessage> counter = system.SpawnGeneratedCounterActor(
            new GeneratedCounterActor());

        await counter.Send(new GeneratedAdd(9));
        int value = await counter.Call<int>(new GeneratedGetCounter(), TimeSpan.FromSeconds(1));

        Assert.Equal(9, value);
    }

    [Fact]
    public async Task Source_generator_creates_actor_client_proxy()
    {
        using ActorSystem system = new();
        ActorRef counterActor = system.Spawn(new GeneratedCounterClientActor());
        IGeneratedCounterClient counter = counterActor.AsGeneratedCounterClient(TimeSpan.FromSeconds(1));

        await counter.Add(11);
        int value = await counter.GetCounter();

        Assert.Equal(11, value);
    }

    [Fact]
    public async Task Stop_drains_queued_messages_before_completion()
    {
        using ActorSystem system = new();
        RecordingActor actor = new();
        ActorRef actorRef = system.Spawn(actor);

        for (int i = 0; i < 16; i++)
        {
            await actorRef.Send(i);
        }

        await actorRef.Stop();

        Assert.Equal(Enumerable.Range(0, 16), actor.Values);
    }

    [Fact]
    public async Task Send_to_stopped_actor_publishes_dead_letter()
    {
        using ActorSystem system = new();
        List<DeadLetter> deadLetters = new();
        system.DeadLetterPublished += deadLetters.Add;
        ActorRef actorRef = system.Spawn(new IgnoringActor());

        await actorRef.Stop();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await actorRef.Send("late-message"));

        DeadLetter deadLetter = Assert.Single(deadLetters);
        Assert.Equal(actorRef.Id, deadLetter.Target);
        Assert.Equal("late-message", deadLetter.Message);
        Assert.Equal("Actor does not exist.", deadLetter.Reason);
    }

    private sealed class ProbeActor : IActor
    {
        private readonly Queue<object> messages = new();
        private readonly SemaphoreSlim available = new(0);

        public ValueTask OnMessage(ActorContext ctx, object message)
        {
            messages.Enqueue(message);
            available.Release();
            return ValueTask.CompletedTask;
        }

        public async Task<object> NextMessage()
        {
            await available.WaitAsync(TimeSpan.FromSeconds(1));
            return messages.Dequeue();
        }
    }

    private sealed class EchoActor : IActor
    {
        public ValueTask OnMessage(ActorContext ctx, object message)
        {
            ctx.Respond(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class IgnoringActor : IActor
    {
        public ValueTask OnMessage(ActorContext ctx, object message)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OrderingActor : IActor
    {
        private readonly List<int> values = new();

        public ValueTask OnMessage(ActorContext ctx, object message)
        {
            switch (message)
            {
                case int value:
                    values.Add(value);
                    break;
                case GetValues:
                    ctx.Respond(values.ToArray());
                    break;
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConcurrencyProbeActor : IActor
    {
        private int active;
        private int maxConcurrency;

        public async ValueTask OnMessage(ActorContext ctx, object message)
        {
            switch (message)
            {
                case int:
                    int current = Interlocked.Increment(ref active);
                    maxConcurrency = Math.Max(maxConcurrency, current);
                    await Task.Delay(5);
                    Interlocked.Decrement(ref active);
                    break;
                case GetMaxConcurrency:
                    ctx.Respond(maxConcurrency);
                    break;
            }
        }
    }

    private sealed class TimerActor : IActor
    {
        private int ticks;
        private IDisposable? timer;

        public ValueTask OnMessage(ActorContext ctx, object message)
        {
            switch (message)
            {
                case StartTimer:
                    timer = ctx.ScheduleRepeated(new Tick(), TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
                    break;
                case Tick:
                    ticks++;
                    break;
                case GetTickCount:
                    timer?.Dispose();
                    ctx.Respond(ticks);
                    break;
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingActor : IActor
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask OnMessage(ActorContext ctx, object message)
        {
            await gate.Task;
        }

        public void Release()
        {
            gate.SetResult();
        }
    }

    private sealed class RecordingActor : IActor
    {
        private readonly List<int> values = new();

        public IReadOnlyList<int> Values => values;

        public ValueTask OnMessage(ActorContext ctx, object message)
        {
            if (message is int value)
            {
                values.Add(value);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class SlowActor(TimeSpan delay) : IActor
    {
        public async ValueTask OnMessage(ActorContext ctx, object message)
        {
            await Task.Delay(delay);
        }
    }

    private sealed class CounterActor : IActor<CounterMessage>
    {
        private int value;

        public ValueTask OnMessage(ActorContext ctx, CounterMessage message)
        {
            switch (message)
            {
                case Add add:
                    value += add.Value;
                    break;
                case GetCounter:
                    ctx.Respond(value);
                    break;
            }

            return ValueTask.CompletedTask;
        }
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));

        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private readonly record struct GetValues;

    private readonly record struct GetMaxConcurrency;

    private readonly record struct StartTimer;

    private readonly record struct Tick;

    private readonly record struct GetTickCount;

    private abstract record CounterMessage;

    private sealed record Add(int Value) : CounterMessage;

    private sealed record GetCounter : CounterMessage;
}
