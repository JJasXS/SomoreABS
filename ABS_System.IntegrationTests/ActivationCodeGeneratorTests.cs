using YourApp.Services;

namespace ABS_System.IntegrationTests;

public sealed class ActivationCodeGeneratorTests
{
    [Fact]
    public void Generate_IncludesLicenseIdAndDateAndMarker()
    {
        var s = ActivationCodeGenerator.Generate(7, new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc));
        Assert.StartsWith("7-20260403-TENT-", s);
        Assert.True(s.Length > "7-20260403-TENT-".Length);
    }
}
