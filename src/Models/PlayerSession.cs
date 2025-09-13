namespace A2ActivityTracker.Models;

public class PlayerSession
{
    // Player SteamID64 (non-bot, non-HLTV)
    public ulong SteamId { get; set; }

    // When the player connected (server local time)
    public DateTime ConnectTime { get; set; }

    // When the player disconnected (null if still connected)
    public DateTime? DisconnectTime { get; set; }

    // Total session duration in seconds
    public long DurationSeconds { get; set; }
    
    // Number of sessions (connect/disconnect pairs)
    public int SessionCount { get; set; }
}
