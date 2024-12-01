namespace SlotTracker;

public class DatabaseConfig
{
    public string Host { get; init; } = "YOUR_DATABASE_HOST";
    public int Port { get; init; } = 3306;
    public string Database { get; init; } = "YOUR_DATABASE_NAME";
    public string User { get; init; } = "YOUR_DATABASE_USER";
    public string Password { get; init; } = "YOUR_DATABASE_PASSWORD";
}
