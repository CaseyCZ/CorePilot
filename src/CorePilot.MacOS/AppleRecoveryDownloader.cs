using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace CorePilot.MacOS;

public sealed record AppleRecoveryTarget(
    string TargetId,
    string DisplayName,
    string BoardId,
    string Mlb,
    string OsType);

public sealed record AppleRecoveryResult(
    string TargetId,
    string DisplayName,
    string OutputDirectory,
    string DmgPath,
    string ChunklistPath,
    string DmgSha256,
    string ChunklistSha256,
    long DmgSizeBytes,
    long ChunklistSizeBytes,
    bool VerifiedByMacRecovery);

public sealed class AppleRecoveryDownloader
{
    private const string ZeroMlb = "00000000000000000";

    // Values are taken from OpenCorePkg Utilities/macrecovery/recovery_urls.txt.
    private static readonly IReadOnlyDictionary<string, AppleRecoveryTarget> Targets =
        new Dictionary<string, AppleRecoveryTarget>(StringComparer.OrdinalIgnoreCase)
        {
            ["ventura-13"] = new(
                "ventura-13",
                "macOS Ventura 13",
                "Mac-B4831CEBD52A0C4C",
                ZeroMlb,
                "default"),
            ["sonoma-14"] = new(
                "sonoma-14",
                "macOS Sonoma 14",
                "Mac-827FAC58A8FDFA22",
                ZeroMlb,
                "default"),
            ["sequoia-15"] = new(
                "sequoia-15",
                "macOS Sequoia 15",
                "Mac-7BA5B2D9E42DDD94",
                ZeroMlb,
                "default"),
            ["tahoe-26"] = new(
                "tahoe-26",
                "macOS Tahoe 26",
                "Mac-CFF7D910A743CAAF",
                ZeroMlb,
                "latest")
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<AppleRecoveryResult> DownloadAsync(
        OpCoreStagingResult stage,
        MacOSAutomationProfile profile,
        OpCoreBuildResult efiBuild,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Targets.TryGetValue(profile.TargetId, out var target))
            throw new InvalidOperationException(
                $"No Apple Recovery profile exists for {profile.TargetId}.");

        if (!efiBuild.Success ||
            !efiBuild.OcValidateStatus.Equals("success", StringComparison.OrdinalIgnoreCase) ||
            !efiBuild.StructuralValidationStatus.StartsWith("success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "EFI must pass both ocvalidate and the CorePilot structure audit first.");

        if (!stage.PythonAvailable || string.IsNullOrWhiteSpace(stage.PythonExecutable))
            throw new InvalidOperationException(
                "Python 3 was not found. CorePilot will not install it silently.");

        if (string.IsNullOrWhiteSpace(efiBuild.MacRecoveryPath) ||
            !File.Exists(efiBuild.MacRecoveryPath))
            throw new FileNotFoundException(
                "macrecovery.py was not found in the OpenCorePkg downloaded for this EFI build.",
                efiBuild.MacRecoveryPath);

        var outputDirectory = Path.Combine(
            stage.WorkspaceDirectory,
            "com.apple.recovery.boot");

        Directory.CreateDirectory(outputDirectory);

        var dmgPath = Path.Combine(outputDirectory, "BaseSystem.dmg");
        var chunklistPath = Path.Combine(outputDirectory, "BaseSystem.chunklist");

        // Remove only the known workspace payload names to avoid accepting stale files.
        if (File.Exists(dmgPath))
            File.Delete(dmgPath);
        if (File.Exists(chunklistPath))
            File.Delete(chunklistPath);

        progress?.Report($"Downloading verified {target.DisplayName} Apple Recovery…");

        var startInfo = new ProcessStartInfo
        {
            FileName = stage.PythonExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = stage.WorkspaceDirectory
        };

        startInfo.ArgumentList.Add(efiBuild.MacRecoveryPath);
        startInfo.ArgumentList.Add("download");
        startInfo.ArgumentList.Add("-b");
        startInfo.ArgumentList.Add(target.BoardId);
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(target.Mlb);
        startInfo.ArgumentList.Add("-os");
        startInfo.ArgumentList.Add(target.OsType);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("BaseSystem");

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
            throw new InvalidOperationException("macrecovery.py could not be started.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                FirstUseful(stderr, stdout, $"macrecovery.py exited with code {process.ExitCode}."));

        if (!File.Exists(dmgPath) || !File.Exists(chunklistPath))
            throw new InvalidOperationException(
                "macrecovery.py completed but BaseSystem.dmg or BaseSystem.chunklist is missing.");

        progress?.Report("Computing local SHA-256 hashes…");

        var dmgSha = await ComputeSha256Async(dmgPath, cancellationToken);
        var chunkSha = await ComputeSha256Async(chunklistPath, cancellationToken);

        var result = new AppleRecoveryResult(
            target.TargetId,
            target.DisplayName,
            outputDirectory,
            dmgPath,
            chunklistPath,
            dmgSha,
            chunkSha,
            new FileInfo(dmgPath).Length,
            new FileInfo(chunklistPath).Length,
            VerifiedByMacRecovery: true);

        await File.WriteAllTextAsync(
            Path.Combine(stage.WorkspaceDirectory, "CorePilotRecovery.json"),
            JsonSerializer.Serialize(
                new
                {
                    createdAt = DateTimeOffset.Now,
                    target,
                    result
                },
                JsonOptions),
            cancellationToken);

        progress?.Report("Apple Recovery downloaded and verified successfully.");
        return result;
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
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

        return "Apple Recovery download failed.";
    }
}
