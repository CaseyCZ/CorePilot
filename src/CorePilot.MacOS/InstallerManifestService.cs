using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CorePilot.MacOS;

public sealed record InstallerManifestFile(
    string Path,
    string Role,
    long SizeBytes,
    string Sha256);

public sealed record InstallerManifestDocument(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string TargetId,
    string DarwinVersion,
    string SmbiosModel,
    string UpstreamCommit,
    string UpstreamArchiveSha256,
    string OcValidateStatus,
    string StructuralValidationStatus,
    bool RecoveryVerifiedByMacRecovery,
    IReadOnlyList<InstallerManifestFile> Files);

public sealed record InstallerManifestResult(
    string ManifestPath,
    string Sha256Path,
    string ManifestSha256,
    int FileCount,
    long TotalBytes);

public sealed record InstallerManifestVerificationResult(
    bool Success,
    IReadOnlyList<string> Errors,
    int VerifiedFiles);

public sealed class InstallerManifestService
{
    private const string ManifestName = "CorePilotInstallerManifest.json";
    private const string ShaFileName = "CorePilotInstallerManifest.sha256";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<InstallerManifestResult> CreateAsync(
        OpCoreStagingResult stage,
        MacOSAutomationProfile profile,
        OpCoreBuildResult efiBuild,
        AppleRecoveryResult recovery,
        CancellationToken cancellationToken = default)
    {
        if (!efiBuild.Success ||
            !efiBuild.OcValidateStatus.Equals("success", StringComparison.OrdinalIgnoreCase) ||
            !efiBuild.StructuralValidationStatus.StartsWith("success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "EFI must be fully validated before creating the installer manifest.");

        if (!recovery.VerifiedByMacRecovery)
            throw new InvalidOperationException(
                "Recovery payload must be verified by macrecovery before manifest creation.");

        var workspace = NormalizeRoot(stage.WorkspaceDirectory);
        var files = new List<InstallerManifestFile>();

        foreach (var file in Directory
                     .EnumerateFiles(efiBuild.EfiDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(file);

            var relative = ToSafeRelative(workspace, file);
            var sha = await ComputeSha256Async(file, cancellationToken);
            files.Add(new(
                relative,
                "EFI",
                new FileInfo(file).Length,
                sha));
        }

        AddKnownRecoveryFile(
            workspace,
            recovery.DmgPath,
            "AppleRecoveryDMG",
            recovery.DmgSizeBytes,
            recovery.DmgSha256,
            files);

        AddKnownRecoveryFile(
            workspace,
            recovery.ChunklistPath,
            "AppleRecoveryChunklist",
            recovery.ChunklistSizeBytes,
            recovery.ChunklistSha256,
            files);

        files = files
            .OrderBy(x => x.Path, StringComparer.Ordinal)
            .ToList();

        var document = new InstallerManifestDocument(
            SchemaVersion: 1,
            CreatedAt: DateTimeOffset.Now,
            TargetId: profile.TargetId,
            DarwinVersion: profile.DarwinVersion,
            SmbiosModel: efiBuild.SmbiosModel,
            UpstreamCommit: stage.UpstreamCommit,
            UpstreamArchiveSha256: stage.ArchiveSha256,
            OcValidateStatus: efiBuild.OcValidateStatus,
            StructuralValidationStatus: efiBuild.StructuralValidationStatus,
            RecoveryVerifiedByMacRecovery: recovery.VerifiedByMacRecovery,
            Files: files);

        var manifestPath = Path.Combine(workspace, ManifestName);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        await File.WriteAllTextAsync(
            manifestPath,
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        var manifestSha = await ComputeSha256Async(manifestPath, cancellationToken);
        var shaPath = Path.Combine(workspace, ShaFileName);
        await File.WriteAllTextAsync(
            shaPath,
            $"{manifestSha}  {ManifestName}{Environment.NewLine}",
            Encoding.ASCII,
            cancellationToken);

        return new(
            manifestPath,
            shaPath,
            manifestSha,
            files.Count,
            files.Sum(x => x.SizeBytes));
    }

    public async Task<InstallerManifestVerificationResult> VerifyAsync(
        string manifestPath,
        string workspaceDirectory,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var verified = 0;
        var workspace = NormalizeRoot(workspaceDirectory);

        if (!File.Exists(manifestPath))
            return new(false, ["Installer manifest is missing."], 0);

        var expectedManifestPath = Path.Combine(workspace, ManifestName);
        if (!Path.GetFullPath(manifestPath).Equals(
                Path.GetFullPath(expectedManifestPath),
                StringComparison.OrdinalIgnoreCase))
            return new(false, ["Installer manifest is outside the expected workspace root."], 0);

        var shaPath = Path.Combine(workspace, ShaFileName);
        if (!File.Exists(shaPath))
            errors.Add("Installer manifest SHA-256 sidecar is missing.");
        else
        {
            var sidecar = (await File.ReadAllTextAsync(shaPath, cancellationToken))
                .Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            var actualManifestSha = await ComputeSha256Async(manifestPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(sidecar) ||
                !sidecar.Equals(actualManifestSha, StringComparison.OrdinalIgnoreCase))
                errors.Add("Installer manifest SHA-256 does not match its sidecar.");
        }

        InstallerManifestDocument? document;
        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            document = JsonSerializer.Deserialize<InstallerManifestDocument>(json);
        }
        catch (Exception ex)
        {
            errors.Add($"Installer manifest JSON is invalid: {ex.Message}");
            return new(false, errors, verified);
        }

        if (document is null)
        {
            errors.Add("Installer manifest could not be parsed.");
            return new(false, errors, verified);
        }

        if (document.SchemaVersion != 1)
            errors.Add($"Unsupported installer manifest schema: {document.SchemaVersion}.");

        foreach (var item in document.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fullPath;
            try
            {
                fullPath = ResolveSafe(workspace, item.Path);
            }
            catch (Exception ex)
            {
                errors.Add($"{item.Path}: {ex.Message}");
                continue;
            }

            if (!File.Exists(fullPath))
            {
                errors.Add($"Manifest file is missing: {item.Path}");
                continue;
            }

            try
            {
                RejectReparsePoint(fullPath);
            }
            catch (Exception ex)
            {
                errors.Add($"{item.Path}: {ex.Message}");
                continue;
            }

            var info = new FileInfo(fullPath);
            if (info.Length != item.SizeBytes)
            {
                errors.Add(
                    $"Size mismatch for {item.Path}: expected {item.SizeBytes}, found {info.Length}.");
                continue;
            }

            var sha = await ComputeSha256Async(fullPath, cancellationToken);
            if (!sha.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"SHA-256 mismatch for {item.Path}.");
                continue;
            }

            verified++;
        }

        return new(errors.Count == 0, errors, verified);
    }

    private static void AddKnownRecoveryFile(
        string workspace,
        string path,
        string role,
        long expectedSize,
        string expectedSha,
        ICollection<InstallerManifestFile> destination)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Recovery file is missing: {path}", path);

        RejectReparsePoint(path);

        var info = new FileInfo(path);
        if (info.Length != expectedSize)
            throw new InvalidOperationException(
                $"Recovery file size changed after verification: {path}");

        destination.Add(new(
            ToSafeRelative(workspace, path),
            role,
            expectedSize,
            expectedSha));
    }

    private static string NormalizeRoot(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;

    private static string ToSafeRelative(string workspaceRoot, string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        if (!full.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"File is outside the workspace: {fullPath}");

        var relative = Path.GetRelativePath(workspaceRoot, full)
            .Replace('\\', '/');

        if (relative.StartsWith("../", StringComparison.Ordinal) ||
            relative.Equals("..", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Unsafe workspace-relative path: {relative}");

        return relative;
    }

    private static string ResolveSafe(string workspaceRoot, string relativePath)
    {
        var normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        var full = Path.GetFullPath(Path.Combine(workspaceRoot, normalized));
        if (!full.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path escapes the workspace.");

        return full;
    }

    private static void RejectReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                "Reparse-point files are not allowed in the installer manifest.");
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
}
