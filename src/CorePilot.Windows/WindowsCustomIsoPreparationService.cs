using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CorePilot.Core;

namespace CorePilot.Windows;

/// <summary>
/// Inspects a user-selected Windows ISO without downloading or modifying it.
/// A matching user-entered checksum does not establish publisher provenance.
/// </summary>
public sealed class WindowsCustomIsoPreparationService
{
    private const long MinImageBytes = 1024L * 1024L * 1024L;
    private const long MaxImageBytes = 32L * 1024L * 1024L * 1024L;

    public async Task<PreparedIsoImage> PrepareAsync(
        SystemVariant target,
        string isoPath,
        string? userChecksum = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows ISO inspection requires Windows.");

        if (target.Id is not ("windows-10" or "windows-11"))
            throw new InvalidOperationException("Select Windows 10 or Windows 11 before choosing an ISO.");

        if (string.IsNullOrWhiteSpace(isoPath))
            throw new ArgumentException("Choose a Windows ISO file first.", nameof(isoPath));

        var path = Path.GetFullPath(isoPath);
        if (!Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a Windows installation file ending in .iso.");

        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException("The selected ISO file was not found.", path);

        if (file.Length < MinImageBytes || file.Length > MaxImageBytes)
            throw new InvalidDataException("The selected ISO has an unexpected size for a Windows installer.");

        var originalLength = file.Length;
        var originalModifiedUtc = file.LastWriteTimeUtc;

        var expected = NormalizeChecksum(userChecksum);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("Checking the selected Windows ISO…");

        await InspectMountedImageAsync(path, target.Id, cancellationToken);

        progress?.Report("Checking the selected file…");
        var sha256 = await ComputeSha256Async(path, cancellationToken);

        if (expected is not null && !sha256.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The file doesn't match the checksum you entered. Nothing was written to USB.");

        file.Refresh();
        if (!file.Exists ||
            file.Length != originalLength ||
            file.LastWriteTimeUtc != originalModifiedUtc)
            throw new InvalidDataException("The selected ISO changed while it was being checked.");

        var workspace = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CorePilot", "media", "windows", "custom");
        Directory.CreateDirectory(workspace);
        var manifestPath = Path.Combine(workspace, sha256 + ".corepilot.json");

        var metadata = new
        {
            schemaVersion = 1,
            preparedAt = DateTimeOffset.UtcNow,
            system = "windows",
            target = target.Id,
            architecture = "x64",
            source = new
            {
                kind = "user-selected",
                originVerified = false,
                checksumSource = expected is null ? "not-provided" : "user-entered"
            },
            image = new
            {
                path,
                fileName = file.Name,
                sizeBytes = file.Length,
                sha256,
                expectedSha256 = expected
            }
        };

        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false),
            cancellationToken);

        progress?.Report(expected is null
            ? "Custom ISO checked. Origin still not verified."
            : "Checksum matches the value you entered. Origin still not verified.");

        return new PreparedIsoImage(
            "windows",
            target.Id,
            target.DisplayName,
            path,
            file.Name,
            "",
            sha256,
            expected,
            false,
            file.Length,
            manifestPath,
            "User-selected ISO. Origin not verified.",
            UserSelected: true);
    }

    public static string? NormalizeChecksum(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        if (!Regex.IsMatch(normalized, "\\A[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException(
                "The optional checksum must contain exactly 64 hexadecimal characters.",
                nameof(value));

        return normalized.ToUpperInvariant();
    }

    private static async Task InspectMountedImageAsync(
        string isoPath,
        string targetId,
        CancellationToken cancellationToken)
    {
        // Encode the path as data. It is never interpolated into PowerShell syntax.
        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(isoPath));
        var expectedBuild = targetId == "windows-11" ? "windows-11" : "windows-10";
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $isoPath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{encodedPath}}'))
            $mountedByCorePilot = $false
            $inspectionError = $null
            try {
                $disk = Get-DiskImage -ImagePath $isoPath -ErrorAction SilentlyContinue
                if ($null -eq $disk -or -not $disk.Attached) {
                    $disk = Mount-DiskImage -ImagePath $isoPath -StorageType ISO -Access ReadOnly -PassThru -ErrorAction Stop
                    $mountedByCorePilot = $true
                }
                $volume = $disk | Get-Volume -ErrorAction Stop | Where-Object { $_.DriveLetter } | Select-Object -First 1
                if ($null -eq $volume) { throw 'The ISO cannot be opened as a Windows installation disc.' }

                $root = [string]$volume.DriveLetter + ':\'
                foreach ($required in @('setup.exe', 'sources\boot.wim', 'efi\boot\bootx64.efi')) {
                    if (-not (Test-Path -LiteralPath (Join-Path $root $required) -PathType Leaf)) {
                        throw 'The selected file is missing required Windows installer files.'
                    }
                }

                $installImage = Join-Path $root 'sources\install.wim'
                if (-not (Test-Path -LiteralPath $installImage -PathType Leaf)) {
                    $installImage = Join-Path $root 'sources\install.esd'
                }
                if (-not (Test-Path -LiteralPath $installImage -PathType Leaf)) {
                    throw 'The selected file does not contain a Windows installation image.'
                }

                $details = Get-WindowsImage -ImagePath $installImage -Index 1 -ErrorAction Stop
                $arch = [string]$details.Architecture
                if ($arch -notmatch '^(x64|amd64|9)$') {
                    throw 'This Windows installer is not for a standard 64-bit PC.'
                }

                $version = [version]([string]$details.Version)
                $name = [string]$details.ImageName
                if ($version.Major -ne 10 -or $name -match 'Server') {
                    throw 'This is not a supported Windows desktop installation image.'
                }
                if ('{{expectedBuild}}' -eq 'windows-11' -and $version.Build -lt 22000) {
                    throw 'The selected file contains Windows 10, not Windows 11.'
                }
                if ('{{expectedBuild}}' -eq 'windows-10' -and ($version.Build -ge 22000 -or $version.Build -lt 10240)) {
                    throw 'The selected file does not match Windows 10.'
                }
                Write-Output 'COREPILOT_ISO_STRUCTURE_OK'
            }
            catch {
                $inspectionError = $_.Exception.Message
            }
            finally {
                if ($mountedByCorePilot) {
                    Dismount-DiskImage -ImagePath $isoPath -ErrorAction SilentlyContinue | Out-Null
                }
            }
            if ($null -ne $inspectionError) {
                Write-Output ('COREPILOT_ISO_ERROR:' + $inspectionError)
                exit 1
            }
            """;

        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Unable to open the selected ISO for inspection.");

        // Let the mount/inspection operation finish its PowerShell finally block;
        // killing it during cancellation could leave the ISO mounted. The following
        // file hashing step remains cancellable immediately.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var stdout = await stdoutTask;
        await stderrTask;
        if (process.ExitCode != 0 ||
            !stdout.Contains("COREPILOT_ISO_STRUCTURE_OK", StringComparison.Ordinal))
        {
            var marker = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith("COREPILOT_ISO_ERROR:", StringComparison.Ordinal));
            var reason = marker is null ? null : marker["COREPILOT_ISO_ERROR:".Length..];
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(reason)
                    ? "This file can't be used as a Windows installer for the selected version."
                    : $"This ISO could not be validated: {reason}");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(input, cancellationToken));
    }
}
