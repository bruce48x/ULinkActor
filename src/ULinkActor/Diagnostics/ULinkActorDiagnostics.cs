using System.Diagnostics;
using System.Diagnostics.Metrics;
using MailboxCore = ULinkActor.Mailbox.Mailbox;

namespace ULinkActor;

public static class ULinkActorDiagnostics
{
    public const string ActivitySourceName = "ULinkActor";

    public const string MeterName = "ULinkActor";

    public static readonly ActivitySource ActivitySource = new(
        ActivitySourceName,
        typeof(ULinkActorDiagnostics).Assembly.GetName().Version?.ToString());

    internal static readonly Meter Meter = new(
        MeterName,
        typeof(ULinkActorDiagnostics).Assembly.GetName().Version?.ToString());

    internal static readonly Counter<long> MessageAcceptedCounter = Meter.CreateCounter<long>(
        "ulinkactor.message.accepted");

    internal static readonly Counter<long> MessageRejectedCounter = Meter.CreateCounter<long>(
        "ulinkactor.message.rejected");

    internal static readonly Counter<long> MessageProcessedCounter = Meter.CreateCounter<long>(
        "ulinkactor.message.processed");

    internal static readonly Counter<long> CallStartedCounter = Meter.CreateCounter<long>(
        "ulinkactor.call.started");

    internal static readonly Counter<long> CallTimeoutCounter = Meter.CreateCounter<long>(
        "ulinkactor.call.timeout");

    internal static readonly Counter<long> DeadLetterCounter = Meter.CreateCounter<long>(
        "ulinkactor.deadletter.published");

    private static readonly ObservableGauge<long> MailboxQueueLengthGauge = Meter.CreateObservableGauge(
        "ulinkactor.mailbox.queue.length",
        static () => MailboxCore.GetTotalQueuedCount());
}
