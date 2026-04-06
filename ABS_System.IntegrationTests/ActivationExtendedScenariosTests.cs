using System.Linq;
using YourApp.Services;

namespace ABS_System.IntegrationTests;

/// <summary>
/// Additional scenarios for activation / seat rules (pure logic; no Firebird or web host).
/// Confirms generators and policy formulas align with <see cref="ActivationValidationService"/> expectations.
/// </summary>
public sealed class ActivationExtendedScenariosTests
{
    /// <summary>Matches <see cref="ActivationValidationService.DeriveSeatDeviceIdFromFingerprint"/> (trim, upper, then first 64 chars).</summary>
    private static string TruncateDeviceIdColumn(string fingerprint)
    {
        var s = (fingerprint ?? "").Trim().ToUpperInvariant();
        return s.Length <= 64 ? s : s[..64];
    }

    private static bool SeatCapBlocksNewDevice(long activeCount, int maxDevices) =>
        maxDevices > 0 && activeCount >= maxDevices;

    private static int EvictionsToBringActiveBelowCap(long activeCount, int cap)
    {
        if (cap <= 0 || activeCount < cap)
            return 0;
        var n = 0;
        var a = activeCount;
        while (a >= cap && n < 256)
        {
            a--;
            n++;
        }

        return n;
    }

    [Theory]
    [InlineData(0, 5, false)]
    [InlineData(4, 5, false)]
    [InlineData(5, 5, true)]
    [InlineData(10, 5, true)]
    [InlineData(1, 1, true)]
    [InlineData(100, 0, false)]
    public void Seat_cap_blocks_only_when_active_reaches_or_exceeds_max(long active, int max, bool expectBlock)
    {
        Assert.Equal(expectBlock, SeatCapBlocksNewDevice(active, max));
    }

    [Theory]
    [InlineData(3, 2, 2)]
    [InlineData(2, 2, 1)]
    [InlineData(5, 3, 3)]
    [InlineData(1, 5, 0)]
    [InlineData(10, 10, 1)]
    public void Eviction_loop_count_matches_while_active_at_or_above_cap(long activeStart, int cap, int expectedEvictions)
    {
        Assert.Equal(expectedEvictions, EvictionsToBringActiveBelowCap(activeStart, cap));
    }

    [Theory]
    [InlineData("abc", "ABC")]
    [InlineData("  xy  ", "XY")]
    public void Device_id_column_truncates_to_64_chars_upper_trim(string input, string expected)
    {
        Assert.Equal(expected, TruncateDeviceIdColumn(input));
    }

    [Fact]
    public void Device_id_exactly_64_chars_unchanged_after_upper()
    {
        var block = "0123456789ABCDEF";
        var input = string.Concat(Enumerable.Repeat(block, 4));
        Assert.Equal(64, input.Length);
        Assert.Equal(input, TruncateDeviceIdColumn(input));
    }

    [Fact]
    public void Device_id_66_chars_truncates_to_first_64()
    {
        var block = "0123456789ABCDEF";
        var input = string.Concat(Enumerable.Repeat(block, 4)) + "01";
        Assert.Equal(66, input.Length);
        var expected = string.Concat(Enumerable.Repeat(block, 4));
        Assert.Equal(expected, TruncateDeviceIdColumn(input));
    }

    [Fact]
    public void Empty_fingerprint_truncates_to_empty()
    {
        Assert.Equal("", TruncateDeviceIdColumn(""));
        Assert.Equal("", TruncateDeviceIdColumn("   "));
    }

    [Theory]
    [InlineData("A", "B", false)]
    [InlineData("KEY", "KEY", true)]
    [InlineData("  spaced  ", "SPACED", true)]
    public void License_key_match_ignores_case_and_trim(string a, string b, bool match)
    {
        Assert.Equal(match, string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("FORM", "CFG", "FORM")]
    [InlineData(null, "CFG", "CFG")]
    [InlineData("", "CFG", "CFG")]
    [InlineData("   ", "Z", "Z")]
    [InlineData("X", null, "X")]
    public void Seat_merge_prefers_nonempty_form_else_config(string? form, string? config, string expected)
    {
        var merged = (string.IsNullOrWhiteSpace(form) ? config : form)?.Trim() ?? "";
        Assert.Equal(expected, merged);
    }

    [Fact]
    public void ActivationCodeGenerator_Generate_throws_when_license_id_non_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ActivationCodeGenerator.Generate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ActivationCodeGenerator.Generate(-1));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void ActivationCodeGenerator_Generate_accepts_entropy_hex_lengths(int hexLen)
    {
        var s = ActivationCodeGenerator.Generate(42, new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc), hexLen);
        Assert.Contains("-TENT-", s);
        Assert.True(s.Length > 0);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    public void ActivationCodeGenerator_Generate_throws_on_invalid_entropy_length(int badLen)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ActivationCodeGenerator.Generate(1, entropyHexChars: badLen));
    }

    [Fact]
    public void ActivationCodeGenerator_TryParse_round_trips_generated_code()
    {
        var utc = new DateTime(2026, 1, 20, 8, 0, 0, DateTimeKind.Utc);
        var code = ActivationCodeGenerator.Generate(99, utc, entropyHexChars: 8);
        Assert.True(ActivationCodeGenerator.TryParse(code, out var lid, out var date, out var entropy));
        Assert.Equal(99, lid);
        Assert.Equal(new DateOnly(2026, 1, 20), date);
        Assert.Equal(8, entropy.Length);
    }

    [Fact]
    public void ActivationCodeGenerator_TryParse_fails_on_garbage()
    {
        Assert.False(ActivationCodeGenerator.TryParse("not-a-code", out _, out _, out _));
        Assert.False(ActivationCodeGenerator.TryParse("", out _, out _, out _));
        Assert.False(ActivationCodeGenerator.TryParse(null, out _, out _, out _));
    }

    [Fact]
    public void SegmentMarker_constant_matches_embedded_in_generated_codes()
    {
        var code = ActivationCodeGenerator.Generate(1);
        Assert.Contains(ActivationCodeGenerator.SegmentMarker, code);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(10, 1, 10)]
    [InlineData(7, 4, 4)]
    public void Evictions_needed_formula_matches_loop(long activeStart, int cap, int expected)
    {
        var formula = activeStart >= cap && cap > 0 ? (int)(activeStart - cap + 1) : 0;
        if (activeStart >= cap && cap > 0)
            Assert.Equal(expected, formula);
        Assert.Equal(expected, EvictionsToBringActiveBelowCap(activeStart, cap));
    }

    [Fact]
    public void Two_distinct_uuids_are_never_equal_for_bind_separation()
    {
        var a = Guid.NewGuid().ToString("N");
        var b = Guid.NewGuid().ToString("N");
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("DEV-0001", "DEV-0002")]
    [InlineData("A", "B")]
    public void Device_identity_strings_distinguish_seats(string id1, string id2)
    {
        Assert.NotEqual(id1.Trim().ToUpperInvariant(), id2.Trim().ToUpperInvariant());
    }
}
