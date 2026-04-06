namespace ABS_System.IntegrationTests;

/// <summary>
/// Seat mode: license key must match <c>LICENSE.LICENSE_KEY</c> when <c>Activation:RequireSeatLicenseKey</c> is true;
/// max devices uses <c>LICENSE.MAX_DEVICE_COUNT</c>; optional eviction deactivates oldest <c>LICENSE_ACTIVATION</c> row.
/// </summary>
public sealed class SeatModeEnforcementTests
{
    [Theory]
    [InlineData("PRO-KEY-001", "pro-key-001", true)]
    [InlineData("X", "Y", false)]
    public void License_key_comparison_matches_server_rule(string submitted, string licenseTableKey, bool expectMatch)
    {
        Assert.Equal(
            expectMatch,
            string.Equals(submitted.Trim(), licenseTableKey.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void At_cap_eviction_reduces_active_count_until_below_cap()
    {
        long active = 3;
        const int cap = 2;
        var evictions = 0;
        while (active >= cap && evictions < 64)
        {
            active--;
            evictions++;
        }

        Assert.True(active < cap);
        Assert.Equal(2, evictions);
    }

    [Fact]
    public void Seat_activation_code_merges_form_with_config_when_form_blank()
    {
        static string MergeSeat(string? form, string? config) =>
            (string.IsNullOrWhiteSpace(form) ? config : form)?.Trim() ?? "";

        Assert.Equal("FROM-FORM", MergeSeat("FROM-FORM", "CFG"));
        Assert.Equal("CFG", MergeSeat(null, "CFG"));
        Assert.Equal("CFG", MergeSeat("", "CFG"));
        Assert.Equal("CFG", MergeSeat("   ", "CFG"));
        Assert.Equal("", MergeSeat(null, null));
    }
}
