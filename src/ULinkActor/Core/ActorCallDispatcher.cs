using System.Diagnostics;
using ULinkActor.Messaging;

namespace ULinkActor.Core;

internal sealed class ActorCallDispatcher
{
    private readonly ActorSystemDiagnosticsPublisher diagnostics;
    private readonly ActorCallResponseWaiter responseWaiter;
    private readonly Func<ActorCallContext?> getCurrentCallContext;

    internal ActorCallDispatcher(
        ActorSystemDiagnosticsPublisher diagnostics,
        Func<ActorCallContext?> getCurrentCallContext)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(getCurrentCallContext);

        this.diagnostics = diagnostics;
        responseWaiter = new ActorCallResponseWaiter(diagnostics);
        this.getCurrentCallContext = getCurrentCallContext;
    }

    internal async ValueTask<TResponse> Call<TResponse>(
        ActorId target,
        ActorCell cell,
        object request,
        ActorCallOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        if (options.QueueTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "QueueTimeout must be greater than or equal to zero.");
        }

        if (options.ResponseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ResponseTimeout must be greater than zero.");
        }

        TaskCompletionSource<object?> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ActorCallContext? caller = getCurrentCallContext();
        IReadOnlyList<ActorId> callChain = caller?.CallChain ?? Array.Empty<ActorId>();
        long startedAt = Stopwatch.GetTimestamp();

        if (callChain.Contains(target))
        {
            throw new InvalidOperationException(
                $"Circular actor call detected. The target actor {target.Value} is already in the call chain " +
                $"({string.Join(" -> ", callChain.Select(id => id.Value.ToString()))}). " +
                "Circular calls between actors indicate a design problem. " +
                "Restructure your actors to avoid circular dependencies.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        ULinkActorDiagnostics.CallStartedCounter.Add(1);
        Envelope envelope = new(request, response, callChain, GetCurrentActivityContext());

        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(static state =>
        {
            ((TaskCompletionSource<object?>)state!).TrySetCanceled();
        }, response);

        await QueueCall(
            target,
            cell,
            request,
            options,
            cancellationToken,
            response,
            caller,
            callChain,
            startedAt,
            envelope).ConfigureAwait(false);

        return await responseWaiter.WaitForResponse<TResponse>(
            target,
            request,
            options,
            cancellationToken,
            response,
            caller,
            callChain,
            startedAt).ConfigureAwait(false);
    }

    private async ValueTask QueueCall(
        ActorId target,
        ActorCell cell,
        object request,
        ActorCallOptions options,
        CancellationToken cancellationToken,
        TaskCompletionSource<object?> response,
        ActorCallContext? caller,
        IReadOnlyList<ActorId> callChain,
        long startedAt,
        Envelope envelope)
    {
        if (options.QueueTimeout == TimeSpan.Zero)
        {
            if (!cell.TrySend(envelope))
            {
                if (cell.Completion.IsCompleted)
                {
                    ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "completed"));
                    diagnostics.PublishDeadLetter(target, request, "Actor mailbox is completed.");
                    throw new InvalidOperationException($"Actor {target} mailbox is completed.");
                }

                TimeoutException exception = PublishQueueTimeout(
                    caller,
                    target,
                    request,
                    options,
                    Stopwatch.GetElapsedTime(startedAt),
                    callChain);
                response.TrySetException(exception);
                throw exception;
            }

            return;
        }

        using CancellationTokenSource queueTimeoutCts = new(options.QueueTimeout);
        using CancellationTokenSource linkedQueueCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            queueTimeoutCts.Token);

        try
        {
            await cell.Send(envelope, linkedQueueCts.Token).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "completed"));
            diagnostics.PublishDeadLetter(target, request, "Actor mailbox is completed.");
            throw;
        }
        catch (OperationCanceledException) when (
            queueTimeoutCts.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            TimeoutException exception = PublishQueueTimeout(
                caller,
                target,
                request,
                options,
                Stopwatch.GetElapsedTime(startedAt),
                callChain);
            response.TrySetException(exception);
            throw exception;
        }
    }

    private TimeoutException PublishQueueTimeout(
        ActorCallContext? caller,
        ActorId target,
        object request,
        ActorCallOptions options,
        TimeSpan elapsed,
        IReadOnlyList<ActorId> callChain)
    {
        ActorCallTimeout timeoutDiagnostic = diagnostics.PublishCallTimeout(
            caller?.ActorId,
            target,
            request,
            options,
            elapsed,
            ActorCallTimeoutReason.QueueTimeout,
            callChain);
        return diagnostics.CreateCallTimeoutException(
            timeoutDiagnostic,
            "The actor call timed out before it could be queued.");
    }

    private static ActivityContext GetCurrentActivityContext()
    {
        return Activity.Current?.Context ?? default;
    }
}
