namespace YourApp.Services;

public sealed class ActivationValidationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";

    public static ActivationValidationResult Ok(string? detail = null) =>
        new() { Success = true, Message = detail ?? "Activation valid." };

    public static ActivationValidationResult Fail(string message) =>
        new() { Success = false, Message = message };
}
