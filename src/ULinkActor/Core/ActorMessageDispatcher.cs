using System.Diagnostics;
using ULinkActor.Messaging;

namespace ULinkActor.Core;

internal sealed class ActorMessageDispatcher
{
    private readonly ActorRegistry registry;
    private readonly ActorSystemDiagnosticsPublisher diagnostics;
    private readonly Func<ActorCallContext?> getCurrentCallContext;

    internal ActorMessageDispatcher(
        ActorRegistry registry,
        ActorSystemDiagnosticsPublisher diagnostics,
        Func<ActorCallContext?> getCurrentCallContext)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(getCurrentCallContext);

        this.registry = registry;
        this.diagnostics = diagnostics;
        this.getCurrentCallContext = getCurrentCallContext;
    }

    internal async ValueTask Send(ActorId target, object message, CancellationToken cancellationToken = default)
    {
        await Send(target, message, GetCurrentActivityContext(), cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask Send(
        ActorId target,
        object message,
        ActivityContext parentActivityContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        ActorCell cell = GetActorForDelivery(target, message);

        try
        {
            await cell.Send(
                new Envelope(message, callChain: GetCurrentCallChain(), parentActivityContext: parentActivityContext),
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "completed"));
            diagnostics.PublishDeadLetter(target, message, "Actor mailbox is completed.");
            throw;
        }
    }

    internal ActorSendResult TrySend(ActorId target, object message)
    {
        return TrySend(target, message, GetCurrentActivityContext());
    }

    internal ActorSendResult TrySend(
        ActorId target,
        object message,
        ActivityContext parentActivityContext)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!registry.TryGet(target, out ActorCell? cell))
        {
            ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "unavailable"));
            diagnostics.PublishDeadLetter(target, message, "Actor does not exist.");
            return ActorSendResult.ActorUnavailable;
        }

        if (cell.IsStopping)
        {
            ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "stopping"));
            diagnostics.PublishDeadLetter(target, message, "Actor is stopping.");
            return ActorSendResult.ActorUnavailable;
        }

        if (cell.TrySend(new Envelope(
            message,
            callChain: GetCurrentCallChain(),
            parentActivityContext: parentActivityContext)))
        {
            return ActorSendResult.Accepted;
        }

        string reason = cell.Completion.IsCompleted
            ? "Actor mailbox is completed."
            : "Actor mailbox is full.";

        ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>(
            "reason",
            cell.Completion.IsCompleted ? "completed" : "full"));
        diagnostics.PublishDeadLetter(target, message, reason);

        return cell.Completion.IsCompleted
            ? ActorSendResult.ActorUnavailable
            : ActorSendResult.MailboxFull;
    }

    internal async ValueTask<TResponse> Call<TResponse>(
        ActorId target,
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

        ActorCell cell = GetActorForDelivery(target, request);
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

                ActorCallTimeout timeoutDiagnostic = diagnostics.PublishCallTimeout(
                    caller?.ActorId,
                    target,
                    request,
                    options,
                    Stopwatch.GetElapsedTime(startedAt),
                    ActorCallTimeoutReason.QueueTimeout,
                    callChain);
                TimeoutException exception = diagnostics.CreateCallTimeoutException(
                    timeoutDiagnostic,
                    "The actor call timed out before it could be queued.");
                response.TrySetException(exception);
                throw exception;
            }
        }
        else
        {
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
                ActorCallTimeout timeoutDiagnostic = diagnostics.PublishCallTimeout(
                    caller?.ActorId,
                    target,
                    request,
                    options,
                    Stopwatch.GetElapsedTime(startedAt),
                    ActorCallTimeoutReason.QueueTimeout,
                    callChain);
                TimeoutException exception = diagnostics.CreateCallTimeoutException(
                    timeoutDiagnostic,
                    "The actor call timed out before it could be queued.");
                response.TrySetException(exception);
                throw exception;
            }
        }

        using CancellationTokenSource responseTimeoutCts = new(options.ResponseTimeout);
        using CancellationTokenSource linkedResponseCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            responseTimeoutCts.Token);

        object? result;

        try
        {
            result = await response.Task.WaitAsync(linkedResponseCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            responseTimeoutCts.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            ActorCallTimeout timeoutDiagnostic = diagnostics.PublishCallTimeout(
                caller?.ActorId,
                target,
                request,
                options,
                Stopwatch.GetElapsedTime(startedAt),
                ActorCallTimeoutReason.ResponseTimeout,
                callChain);
            TimeoutException exception = diagnostics.CreateCallTimeoutException(timeoutDiagnostic, "The actor call timed out.");
            response.TrySetException(exception);
            throw exception;
        }

        if (result is null)
        {
            return default!;
        }

        if (result is TResponse typed)
        {
            return typed;
        }

        throw new InvalidCastException($"Actor responded with {result.GetType().FullName}, not {typeof(TResponse).FullName}.");
    }

    private ActorCell GetActorForDelivery(ActorId target, object message)
    {
        if (!registry.TryGet(target, out ActorCell? cell))
        {
            ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "unavailable"));
            diagnostics.PublishDeadLetter(target, message, "Actor does not exist.");
            throw new InvalidOperationException($"Actor {target} does not exist.");
        }

        if (cell.IsStopping)
        {
            ULinkActorDiagnostics.MessageRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "stopping"));
            diagnostics.PublishDeadLetter(target, message, "Actor is stopping.");
            throw new InvalidOperationException($"Actor {target} is stopping.");
        }

        return cell;
    }

    private IReadOnlyList<ActorId> GetCurrentCallChain()
    {
        return getCurrentCallContext()?.CallChain ?? Array.Empty<ActorId>();
    }

    private static ActivityContext GetCurrentActivityContext()
    {
        return Activity.Current?.Context ?? default;
    }
}
