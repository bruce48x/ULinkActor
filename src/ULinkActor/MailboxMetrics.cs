namespace ULinkActor;

public readonly record struct MailboxMetrics(
    int Capacity,
    int QueuedCount,
    long EnqueuedCount,
    long ProcessedCount,
    bool IsCompleted);
