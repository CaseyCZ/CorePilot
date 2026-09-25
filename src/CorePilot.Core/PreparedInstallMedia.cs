namespace CorePilot.Core;

public sealed record PreparedIsoImage(
    string SystemId,
    string TargetId,
    string DisplayName,
    string IsoPath,
    string FileName,
    string SourceUrl,
    string Sha256,
    string? ExpectedSha256,
    bool IntegrityVerified,
    long SizeBytes,
    string ManifestPath,
    string Provenance);

public sealed record GenericUsbWriteResult(
    string SystemId,
    string TargetId,
    int TargetDiskIndex,
    string TargetIdentityFingerprint,
    string TranscriptPath,
    string TranscriptSha256,
    long BytesWritten,
    bool Verified);
