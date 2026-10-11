using CorePilot.Core;

namespace CorePilot.MacOS;

public enum MacOSAutoResolutionState
{
    Prepared,
    SourceReady,
    ManualAction,
    Unresolved
}

public sealed record MacOSAutoResolutionItem(
    MacOSAutoResolutionState State,
    string Category,
    string Requirement,
    string Resolution,
    string? SourceId = null,
    string? SourceVersion = null)
{
    public string StateText => State switch
    {
        MacOSAutoResolutionState.Prepared => "AUTO",
        MacOSAutoResolutionState.SourceReady => "FOUND",
        MacOSAutoResolutionState.ManualAction => "MANUAL",
        _ => "UNRESOLVED"
    };
}

public sealed record MacOSAutoResolutionResult(
    IReadOnlyList<MacOSAutoResolutionItem> Items,
    bool HasDeepScan,
    bool IsGenuineAppleMac,
    bool AutomaticConfigurationReady)
{
    public int AutomaticCount => Items.Count(x =>
        x.State is MacOSAutoResolutionState.Prepared or MacOSAutoResolutionState.SourceReady);

    public int ManualCount => Items.Count(x =>
        x.State == MacOSAutoResolutionState.ManualAction);

    public int UnresolvedCount => Items.Count(x =>
        x.State == MacOSAutoResolutionState.Unresolved);

    public string Summary =>
        $"Auto-configured/resolved {AutomaticCount} · manual {ManualCount} · unresolved {UnresolvedCount}";
}

public sealed class MacOSAutoResolutionService
{
    private readonly OnlineSourceCatalogService _sources;

    public MacOSAutoResolutionService(
        OnlineSourceCatalogService? sources = null)
    {
        _sources = sources ?? new OnlineSourceCatalogService();
    }

    public async Task<MacOSAutoResolutionResult> ResolveAsync(
        HardwareReport hardware,
        SystemVariant target,
        CompatibilityReport compatibility,
        MacOSAutomationProfile? profile,
        OnlineSourceSnapshot? verifySnapshot,
        bool hasDeepScan,
        CancellationToken cancellationToken = default)
    {
        var items = new List<MacOSAutoResolutionItem>();
        var sourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var genuineApple = MacOSCompatibilityAnalyzer.IsGenuineAppleMac(hardware);

        if (!genuineApple && !hasDeepScan)
        {
            items.Add(new(
                MacOSAutoResolutionState.Unresolved,
                "Hardware",
                "Deep hardware scan",
                "Exact ACPI/PCI hardware data was not available, so CorePilot cannot safely finalize the automatic EFI profile."));
        }

        if (profile is not null)
        {
            foreach (var decision in profile.Decisions)
            {
                if (decision.Category.Equals("Firmware", StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(new(
                        MacOSAutoResolutionState.ManualAction,
                        decision.Category,
                        decision.Subject,
                        $"{decision.Choice} · {decision.Reason}"));
                    continue;
                }

                items.Add(new(
                    decision.RequiresReview
                        ? MacOSAutoResolutionState.Unresolved
                        : MacOSAutoResolutionState.Prepared,
                    decision.Category,
                    decision.Subject,
                    $"{decision.Choice} · {decision.Reason}"));
            }

            foreach (var disabled in profile.DisabledDevices)
            {
                items.Add(new(
                    MacOSAutoResolutionState.Prepared,
                    "Device",
                    disabled,
                    "CorePilot prepared this device to be disabled/ignored by the generated macOS configuration."));
            }
        }

        foreach (var patch in compatibility.RequiredPatches)
        {
            items.Add(new(
                MacOSAutoResolutionState.Prepared,
                "Patch",
                patch,
                "Patch requirement was derived from the detected hardware and will be passed into the generated configuration."));

            if (patch.Contains("AMD Vanilla", StringComparison.OrdinalIgnoreCase))
                sourceIds.Add("macos.amd-vanilla");
        }

        foreach (var bootArgument in compatibility.BootArguments)
        {
            items.Add(new(
                MacOSAutoResolutionState.Prepared,
                "Boot argument",
                bootArgument,
                "Boot argument was selected automatically from the detected hardware."));
        }

        foreach (var kext in compatibility.RequiredKexts)
        {
            var sourceId = SourceForKext(kext);

            if (sourceId is null)
            {
                items.Add(new(
                    MacOSAutoResolutionState.Unresolved,
                    "Driver/kext",
                    kext,
                    "CorePilot does not yet have a trusted online source mapping for this required component."));
                continue;
            }

            sourceIds.Add(sourceId);
        }

        if (profile?.WifiMode.Contains("HeliPort", StringComparison.OrdinalIgnoreCase) == true)
            sourceIds.Add("macos.heliport");

        if (genuineApple &&
            compatibility.Findings.Any(x =>
                x.Component.Equals("Patcher", StringComparison.OrdinalIgnoreCase)))
            sourceIds.Add("macos.oclp");

        foreach (var sourceId in sourceIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var existing = verifySnapshot?.Sources.FirstOrDefault(x =>
                x.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase));

            if (existing is { Success: true, Live: true })
            {
                items.Add(SourceReadyItem(sourceId, existing));
                continue;
            }

            try
            {
                var resolved = await _sources.ResolveRequiredSourceAsync(
                    sourceId,
                    requireLive: true,
                    cancellationToken);

                items.Add(SourceReadyItem(sourceId, resolved));
            }
            catch (Exception ex)
            {
                items.Add(new(
                    MacOSAutoResolutionState.Unresolved,
                    "Online source",
                    sourceId,
                    $"Required source could not be resolved live: {ex.Message}",
                    sourceId));
            }
        }

        foreach (var finding in compatibility.Findings)
        {
            if (finding.State == CompatibilityState.Blocked)
            {
                items.Add(new(
                    MacOSAutoResolutionState.Unresolved,
                    finding.Component,
                    finding.Title,
                    finding.SuggestedAction ?? finding.Details));
                continue;
            }

            if (finding.State == CompatibilityState.Unknown)
            {
                items.Add(new(
                    MacOSAutoResolutionState.Unresolved,
                    finding.Component,
                    finding.Title,
                    finding.SuggestedAction ?? finding.Details));
                continue;
            }

            if (finding.State != CompatibilityState.ActionRequired)
                continue;

            if (finding.Component.Equals("Firmware", StringComparison.OrdinalIgnoreCase))
                continue;

            if (FindingCoveredByProfile(finding, profile))
                continue;

            if (finding.Component.Equals("Patcher", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new(
                    MacOSAutoResolutionState.Unresolved,
                    finding.Component,
                    finding.Title,
                    "The current OCLP source can be located automatically, but the guarded legacy-Mac writer is not implemented yet. CorePilot will not silently cross that boundary."));
                continue;
            }

            if (finding.Component.Equals("T1 / Wi-Fi / USB", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new(
                    MacOSAutoResolutionState.Unresolved,
                    finding.Component,
                    finding.Title,
                    finding.SuggestedAction ?? finding.Details));
                continue;
            }

            items.Add(new(
                MacOSAutoResolutionState.ManualAction,
                finding.Component,
                finding.Title,
                finding.SuggestedAction ?? finding.Details));
        }

        items = items
            .GroupBy(
                x => $"{x.State}|{x.Category}|{x.Requirement}|{x.Resolution}",
                StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();

        var unresolved = items.Any(x =>
            x.State == MacOSAutoResolutionState.Unresolved);
        var manual = items.Any(x =>
            x.State == MacOSAutoResolutionState.ManualAction);

        var automaticReady =
            compatibility.CanProceed &&
            !unresolved &&
            !manual &&
            (genuineApple || hasDeepScan) &&
            (genuineApple || profile is { CanBuildEfi: true, RequiresReview: false });

        return new(
            items,
            hasDeepScan,
            genuineApple,
            automaticReady);
    }

    private static MacOSAutoResolutionItem SourceReadyItem(
        string sourceId,
        OnlineSourceResolution source)
    {
        var version = string.IsNullOrWhiteSpace(source.Version)
            ? source.ResolvedRef
            : source.Version;

        return new(
            MacOSAutoResolutionState.SourceReady,
            "Online source",
            source.Name,
            $"Resolved live from {source.Repository ?? source.SourceUrl ?? sourceId}.",
            sourceId,
            version);
    }

    private static bool FindingCoveredByProfile(
        CompatibilityFinding finding,
        MacOSAutomationProfile? profile)
    {
        if (profile is null)
            return false;

        var category = finding.Component switch
        {
            "CPU" => "Kernel",
            "Wi-Fi" => "Wi-Fi",
            _ => finding.Component
        };

        return profile.Decisions.Any(x =>
            x.Category.Equals(category, StringComparison.OrdinalIgnoreCase) &&
            !x.RequiresReview);
    }

    private static string? SourceForKext(string requirement)
    {
        if (requirement.StartsWith("Lilu", StringComparison.OrdinalIgnoreCase))
            return "macos.kext.lilu";

        if (requirement.StartsWith("VirtualSMC", StringComparison.OrdinalIgnoreCase))
            return "macos.kext.virtualsmc";

        if (requirement.StartsWith("WhateverGreen", StringComparison.OrdinalIgnoreCase))
            return "macos.kext.whatevergreen";

        if (requirement.StartsWith("AppleALC", StringComparison.OrdinalIgnoreCase))
            return "macos.kext.applealc";

        if (requirement.StartsWith("IntelMausi", StringComparison.OrdinalIgnoreCase))
            return "macos.kext.intelmausi";

        if (requirement.StartsWith("AirportBrcmFixup", StringComparison.OrdinalIgnoreCase))
            return "macos.kext.airportbrcmfixup";

        if (requirement.StartsWith("NVMeFix", StringComparison.OrdinalIgnoreCase))
            return "macos.kext.nvmefix";

        if (requirement.StartsWith("RestrictEvents", StringComparison.OrdinalIgnoreCase))
            return "macos.kext.restrictevents";

        if (requirement.Contains("itlwm", StringComparison.OrdinalIgnoreCase) ||
            requirement.Contains("AirportItlwm", StringComparison.OrdinalIgnoreCase))
            return "macos.kext.itlwm";

        return null;
    }
}
