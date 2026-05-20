using System.Diagnostics;

namespace ULinkActor;

public static class ULinkActorDiagnostics
{
    public const string ActivitySourceName = "ULinkActor";

    public static readonly ActivitySource ActivitySource = new(
        ActivitySourceName,
        typeof(ULinkActorDiagnostics).Assembly.GetName().Version?.ToString());
}
