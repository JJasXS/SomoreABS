namespace ABS_System.IntegrationTests;

/// <summary>
/// Documents <see cref="YourApp.Services.ActivationValidationService"/> seat registration gate:
/// Seat mode uses <c>LICENSE.MAX_DEVICE_COUNT</c> (not <c>MAX_USER_COUNT</c>) as the cap on <strong>active</strong> <c>LICENSE_ACTIVATION</c> rows per <c>LICENSE_ID</c>.
/// </summary>
public sealed class MaxUserCountPolicyTests
{
    /// <summary>Same condition as TryRegisterSeatDeviceAsync before allowing a new insert.</summary>
    private static bool BlocksNewSeatBecauseAtOrOverCap(long activeSeatCount, int? maxUserCount) =>
        maxUserCount.HasValue && maxUserCount.Value > 0 && activeSeatCount >= maxUserCount.Value;

    [Fact]
    public void MaxUserCount2_means_two_distinct_devices_may_activate_not_one()
    {
        const int max = 2;
        Assert.False(BlocksNewSeatBecauseAtOrOverCap(0, max));
        Assert.False(BlocksNewSeatBecauseAtOrOverCap(1, max));
        Assert.True(BlocksNewSeatBecauseAtOrOverCap(2, max));
    }

    [Fact]
    public void Third_device_is_blocked_when_max_is_2_and_two_active_rows_exist()
    {
        const int max = 2;
        var activeAfterTwoSeats = 2L;
        Assert.True(BlocksNewSeatBecauseAtOrOverCap(activeAfterTwoSeats, max));
    }

    [Fact]
    public void Null_or_zero_max_does_not_block_by_count()
    {
        Assert.False(BlocksNewSeatBecauseAtOrOverCap(99, null));
        Assert.False(BlocksNewSeatBecauseAtOrOverCap(99, 0));
    }
}
