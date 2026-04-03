using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace YourApp.Services;

/// <summary>
/// Builds unique activation codes for LICENSE_ACTIVATION.ACTIVATION_CODE.
/// Format: <c>{LICENSE_ID}-{yyyyMMdd}-TENT-{ENTROPY}</c> (replaces a static <c>-TEMP</c> suffix that made every code look the same).
/// </summary>
public static partial class ActivationCodeGenerator
{
    /// <summary>Marker segment (tenant-oriented label; was often hardcoded as TEMP).</summary>
    public const string SegmentMarker = "TENT";

    /// <summary>
    /// Generates a new activation code. Call once per machine/seat — each call uses fresh random entropy.
    /// </summary>
    /// <param name="licenseId">License row id (chosen by you / your admin UI).</param>
    /// <param name="issueDateUtc">Date embedded in the code; defaults to UTC today.</param>
    /// <param name="entropyHexChars">Length of random hex suffix (4–16). Default 4 → 2 random bytes.</param>
    public static string Generate(int licenseId, DateTime? issueDateUtc = null, int entropyHexChars = 4)
    {
        if (licenseId < 1)
            throw new ArgumentOutOfRangeException(nameof(licenseId), "License id must be positive.");
        if (entropyHexChars is < 4 or > 16)
            throw new ArgumentOutOfRangeException(nameof(entropyHexChars), "Use between 4 and 16 hex characters.");

        var date = (issueDateUtc ?? DateTime.UtcNow).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var byteLen = (entropyHexChars + 1) / 2;
        Span<byte> buf = stackalloc byte[byteLen];
        RandomNumberGenerator.Fill(buf);
        var hex = Convert.ToHexString(buf);
        var entropy = hex[..entropyHexChars];

        return $"{licenseId}-{date}-{SegmentMarker}-{entropy}";
    }

    /// <summary>Parses a code produced by <see cref="Generate"/> (for tooling / debugging).</summary>
    public static bool TryParse(string? code, out int licenseId, out DateOnly issueDate, out string entropy)
    {
        licenseId = 0;
        issueDate = default;
        entropy = "";

        if (string.IsNullOrWhiteSpace(code))
            return false;

        var m = GeneratedCodeRegex().Match(code.Trim());
        if (!m.Success)
            return false;

        if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out licenseId))
            return false;
        if (!DateOnly.TryParseExact(m.Groups[2].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out issueDate))
            return false;
        entropy = m.Groups[3].Value;
        return true;
    }

    [GeneratedRegex(@"^(\d+)-(\d{8})-" + SegmentMarker + @"-([0-9A-F]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedCodeRegex();
}
