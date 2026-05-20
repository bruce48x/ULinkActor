using System.Threading.Tasks.Dataflow;

namespace ULinkActor;

internal sealed class Mailbox
{
    private readonly ActionBlock<Envelope> block;
    private readonly int capacity;
    private long enqueuedCount;
    private long processedCount;

    public Mailbox(Func<Envelope, ValueTask> dispatch, int capacity)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        this.capacity = capacity;
        block = new ActionBlock<Envelope>(
            async envelope =>
            {
                try
                {
                    await dispatch(envelope).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Increment(ref processedCount);
                }
            },
            new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = capacity,
                EnsureOrdered = true,
                MaxDegreeOfParallelism = 1
            });
    }

    public Task Completion => block.Completion;

    public async ValueTask Send(Envelope envelope, CancellationToken cancellationToken)
    {
        bool accepted = await block.SendAsync(envelope, cancellationToken).ConfigureAwait(false);

        if (!accepted)
        {
            throw new InvalidOperationException("The actor mailbox is completed.");
        }

        Interlocked.Increment(ref enqueuedCount);
    }

    public void Complete()
    {
        block.Complete();
    }

    public MailboxMetrics GetMetrics()
    {
        return new MailboxMetrics(
            capacity,
            block.InputCount,
            Interlocked.Read(ref enqueuedCount),
            Interlocked.Read(ref processedCount),
            Completion.IsCompleted);
    }
}
