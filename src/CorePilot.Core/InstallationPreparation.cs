namespace CorePilot.Core;

public enum PreparationItemState
{
    Detected,
    ResolvedAutomatically,
    SourceResolved,
    ManualActionRequired,
    Unresolved
}

public sealed record PreparationItem(
    PreparationItemState State,
    string Category,
    string Problem,
    string Resolution,
    string? Source = null)
{
    public string StateText => State switch
    {
        PreparationItemState.ResolvedAutomatically => "AUTO",
        PreparationItemState.SourceResolved => "FOUND",
        PreparationItemState.ManualActionRequired => "MANUAL",
        PreparationItemState.Unresolved => "UNRESOLVED",
        _ => "DETECTED"
    };
}

public sealed record InstallationPreparationResult(
    string SystemId,
    string TargetId,
    IReadOnlyList<PreparationItem> Items,
    bool CompatibilityEvaluated,
    bool SearchCompleted,
    bool ConfigurationPrepared,
    bool MediaWriterAvailable)
{
    public int ResolvedCount => Items.Count(x =>
        x.State is PreparationItemState.ResolvedAutomatically
            or PreparationItemState.SourceResolved);

    public int ManualCount => Items.Count(x =>
        x.State == PreparationItemState.ManualActionRequired);

    public int UnresolvedCount => Items.Count(x =>
        x.State == PreparationItemState.Unresolved);

    public bool SystemPrepared =>
        CompatibilityEvaluated &&
        SearchCompleted &&
        ConfigurationPrepared &&
        ManualCount == 0 &&
        UnresolvedCount == 0;

    public bool ReadyToWrite =>
        SystemPrepared &&
        MediaWriterAvailable;

    public string Verdict =>
        ReadyToWrite
            ? "READY TO WRITE"
            : SystemPrepared
                ? "SYSTEM PREPARED · MEDIA WRITER NOT AVAILABLE"
                : UnresolvedCount > 0
                    ? "NOT READY · UNRESOLVED ITEMS REMAIN"
                    : ManualCount > 0
                        ? "MANUAL ACTION REQUIRED"
                        : "PREPARING";

    public string Summary =>
        $"Resolved {ResolvedCount} · manual {ManualCount} · unresolved {UnresolvedCount}";
}

public static class InstallationPreparationBuilder
{
    public static InstallationPreparationResult FromGenericCompatibility(
        string systemId,
        SystemVariant target,
        CompatibilityReport compatibility,
        OnlineSourceSnapshot? sources,
        bool mediaWriterAvailable)
    {
        var items = new List<PreparationItem>();

        foreach (var finding in compatibility.Findings)
        {
            switch (finding.State)
            {
                case CompatibilityState.Supported:
                    items.Add(new(
                        PreparationItemState.ResolvedAutomatically,
                        finding.Component,
                        finding.Title,
                        finding.Details));
                    break;

                case CompatibilityState.ActionRequired:
                    items.Add(new(
                        PreparationItemState.ManualActionRequired,
                        finding.Component,
                        finding.Title,
                        finding.SuggestedAction ?? finding.Details));
                    break;

                case CompatibilityState.Warning:
                    items.Add(new(
                        PreparationItemState.Detected,
                        finding.Component,
                        finding.Title,
                        finding.SuggestedAction ?? finding.Details));
                    break;

                case CompatibilityState.Blocked:
                case CompatibilityState.Unknown:
                    items.Add(new(
                        PreparationItemState.Unresolved,
                        finding.Component,
                        finding.Title,
                        finding.SuggestedAction ?? finding.Details));
                    break;
            }
        }

        if (sources is not null)
        {
            foreach (var source in sources.Sources)
            {
                if (source.Success && source.Live)
                {
                    items.Add(new(
                        PreparationItemState.SourceResolved,
                        "Online source",
                        source.Name,
                        string.IsNullOrWhiteSpace(source.Version)
                            ? "Current live source resolved."
                            : $"Current live source resolved: {source.Version}.",
                        source.SourceUrl ?? source.Repository));
                }
                else if (source.Critical)
                {
                    items.Add(new(
                        PreparationItemState.Unresolved,
                        "Online source",
                        source.Name,
                        source.Error ?? "Critical source could not be resolved live.",
                        source.SourceUrl ?? source.Repository));
                }
            }
        }

        var unresolved = items.Any(x =>
            x.State == PreparationItemState.Unresolved);
        var manual = items.Any(x =>
            x.State == PreparationItemState.ManualActionRequired);

        return new(
            systemId,
            target.Id,
            items,
            CompatibilityEvaluated: true,
            SearchCompleted: sources is not null,
            ConfigurationPrepared:
                compatibility.CanProceed &&
                !unresolved &&
                !manual,
            mediaWriterAvailable);
    }
}
