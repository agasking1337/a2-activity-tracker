namespace A2ActivityTracker.Models;

public class MapSession
{
    // Database ID (null if not yet persisted)
    public int? DatabaseId { get; set; }
    // Map identifier (e.g., de_inferno)
    public string MapName { get; set; } = string.Empty;

    // Session start (server local time)
    public DateTime MapStart { get; set; }

    // Session end (server local time). Null while the map is active.
    public DateTime? MapEnd { get; set; }

    // Distinct number of unique players seen during this map session (excluding bots/HLTV)
    public int TotalPlayersSeen { get; set; }

    // Total accumulated player playtime across the session (sum over players).
    // Define units at integration time (e.g., seconds).
    public long TotalPlaytime { get; set; }

    // --- Runtime tracking state (not intended for persistence) ---
    private readonly HashSet<ulong> _playersSeen = new();
    private readonly HashSet<ulong> _activePlayers = new();
    private DateTime? _lastUpdate;

    public int CurrentActivePlayers => _activePlayers.Count;
    public bool IsActive => MapEnd == null;

    // Initialize a new session
    public void Start(string mapName, DateTime now)
    {
        DatabaseId = null; // Reset database ID for new sessions
        MapName = mapName;
        MapStart = now;
        MapEnd = null;
        TotalPlayersSeen = 0;
        TotalPlaytime = 0;
        _playersSeen.Clear();
        _activePlayers.Clear();
        _lastUpdate = now;
    }

    // End the session and finalize playtime accumulation
    public void End(DateTime now)
    {
        UpdatePlaytime(now);
        MapEnd = now;
    }

    // Record a player connect event (provide non-bot, non-HLTV SteamID64)
    public void OnPlayerConnect(ulong steamId, DateTime now)
    {
        UpdatePlaytime(now);
        _activePlayers.Add(steamId);
        if (_playersSeen.Add(steamId))
        {
            TotalPlayersSeen = _playersSeen.Count;
        }
    }

    // Record a player disconnect event
    public void OnPlayerDisconnect(ulong steamId, DateTime now)
    {
        UpdatePlaytime(now);
        _activePlayers.Remove(steamId);
    }

    // Optional: manual tick to accumulate time when you know the current active count
    public void Tick(DateTime now)
    {
        UpdatePlaytime(now);
    }

    private void UpdatePlaytime(DateTime now)
    {
        if (_lastUpdate.HasValue)
        {
            var delta = (now - _lastUpdate.Value).TotalSeconds;
            if (delta > 0)
            {
                // Accumulate sum of player-seconds: activePlayers * elapsedSeconds
                TotalPlaytime += (long)Math.Round(delta * _activePlayers.Count);
            }
        }
        _lastUpdate = now;
    }
}
