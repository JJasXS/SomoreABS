namespace YourApp.Models;

/// <summary>Singleton row (Id=1) in SQL Server: persisted machine fingerprint for licensing / ops.</summary>
public sealed class LocalDeploymentInfo
{
    public const int SingletonRowId = 1;

    public int Id { get; set; } = SingletonRowId;

    /// <summary>64-char hex SHA-256 machine fingerprint (same algorithm as licensing SDK / <c>MachineFingerprint.Compute()</c>).</summary>
    public string MachineFingerprintHex { get; set; } = "";

    public DateTime UpdatedAtUtc { get; set; }
}
