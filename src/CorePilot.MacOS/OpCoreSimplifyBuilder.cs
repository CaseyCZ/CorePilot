using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace CorePilot.MacOS;

public sealed record OpCoreBuildResult(
    bool Success,
    string EfiDirectory,
    string ConfigPath,
    string SmbiosModel,
    IReadOnlyList<string> Kexts,
    IReadOnlyList<string> AcpiPatches,
    IReadOnlyList<string> DisabledDevices,
    bool NeedsOclp,
    string OcValidateStatus,
    string OcValidateOutput,
    string Error,
    string StructuralValidationStatus = "not-run");

public sealed class OpCoreSimplifyBuilder
{
    private readonly EfiStructureValidator _structureValidator = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<OpCoreBuildResult> BuildAsync(
        OpCoreStagingResult stage,
        MacOSAutomationProfile profile,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!profile.CanBuildEfi)
            throw new InvalidOperationException("The current compatibility profile blocks EFI generation.");

        if (profile.RequiresReview)
            throw new InvalidOperationException("Advanced review is required before EFI generation.");

        if (!stage.PythonAvailable || string.IsNullOrWhiteSpace(stage.PythonExecutable))
            throw new InvalidOperationException(
                "Python 3 was not found. CorePilot will not install it silently.");

        if (!Directory.Exists(stage.UpstreamDirectory))
            throw new DirectoryNotFoundException(
                $"Staged OpCore Simplify source is missing: {stage.UpstreamDirectory}");

        progress?.Report("Extracting CorePilot EFI bridge…");
        var bridgePath = Path.Combine(stage.WorkspaceDirectory, "corepilot_opcore_bridge.py");
        await ExtractBridgeAsync(bridgePath, cancellationToken);

        var resultPath = Path.Combine(stage.WorkspaceDirectory, "CorePilotEfiBuild.json");
        if (File.Exists(resultPath))
            File.Delete(resultPath);

        progress?.Report("Building OpenCore EFI… This downloads the required OpenCore/kext components.");

        var startInfo = new ProcessStartInfo
        {
            FileName = stage.PythonExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = stage.WorkspaceDirectory
        };

        startInfo.ArgumentList.Add(bridgePath);
        startInfo.ArgumentList.Add("--upstream");
        startInfo.ArgumentList.Add(stage.UpstreamDirectory);
        startInfo.ArgumentList.Add("--workspace");
        startInfo.ArgumentList.Add(stage.WorkspaceDirectory);

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
            throw new InvalidOperationException("Python EFI bridge could not be started.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        OpCoreBuildResult? result = null;
        if (File.Exists(resultPath))
        {
            var json = await File.ReadAllTextAsync(resultPath, cancellationToken);
            result = JsonSerializer.Deserialize<OpCoreBuildResult>(json, JsonOptions);
        }

        if (process.ExitCode != 0 || result is null || !result.Success)
        {
            var detail = result?.Error;
            if (string.IsNullOrWhiteSpace(detail))
                detail = FirstUseful(stderr, stdout);

            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"EFI bridge exited with code {process.ExitCode}."
                    : detail);
        }

        if (!Directory.Exists(result.EfiDirectory) || !File.Exists(result.ConfigPath))
            throw new InvalidOperationException(
                "EFI bridge reported success but the generated EFI/config.plist is missing.");

        if (!result.OcValidateStatus.Equals("success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "EFI was generated but did not pass ocvalidate.");

        progress?.Report("Auditing EFI structure against config.plist…");
        var audit = _structureValidator.Validate(result.EfiDirectory, result.ConfigPath);

        if (!audit.Success)
        {
            var summary = string.Join("; ", audit.Errors.Take(4));
            throw new InvalidOperationException(
                $"EFI structure audit failed: {summary}");
        }

        result = result with
        {
            StructuralValidationStatus =
                $"success ({audit.CheckedEntries} enabled references checked)"
        };

        await File.WriteAllTextAsync(
            resultPath,
            JsonSerializer.Serialize(result, JsonOptions),
            cancellationToken);

        progress?.Report("EFI generated, ocvalidated and structurally audited successfully.");
        return result;
    }

    private static async Task ExtractBridgeAsync(
        string destination,
        CancellationToken cancellationToken)
    {
        var assembly = typeof(OpCoreSimplifyBuilder).Assembly;
        var resource = assembly
            .GetManifestResourceNames()
            .SingleOrDefault(x => x.EndsWith("opcore_bridge.py", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "Embedded CorePilot EFI bridge resource was not found.");

        await using var input = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                "Embedded CorePilot EFI bridge could not be opened.");
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static string FirstUseful(params string[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var lines = value
                .Split(
                    new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (lines.Length > 0)
                return lines[^1];
        }

        return "";
    }
}
