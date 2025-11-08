namespace SqliteWasmBlazor.Demo.Services;

public class DBInitializationService(string? errorMessage = null)
{
    public string? ErrorMessage { get; set; } = errorMessage;
}