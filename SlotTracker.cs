using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Timers;
using Dapper;
using MySqlConnector;
using SlotTracker.Models;
using System.Text.Json;

namespace SlotTracker;

[MinimumApiVersion(100)]
public class SlotTracker : BasePlugin
{
    public override string ModuleName => "SlotTracker";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "agasking1337";

    private DatabaseConfig _config = null!;
    private int _serverSlots;
    private bool _updatePending = false;

    public class ServerStats
    {
        public DateTime Timestamp { get; set; }
        public int PlayerCount { get; set; }
        public int ServerSlots { get; set; }
    }

    public override void Load(bool hotReload)
    {
        base.Load(hotReload);
        
        Console.WriteLine("[SlotTracker] Plugin loading...");
        
        _config = LoadConfig();
        InitializeDatabase();

        // Initialize with default value
        _serverSlots = 10; // Default value
        Console.WriteLine($"[SlotTracker] Server initialized with default slots: {_serverSlots}");

        // Register event handlers
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnect, HookMode.Post);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect, HookMode.Pre);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        
        Console.WriteLine("[SlotTracker] Plugin loaded successfully!");
        Console.WriteLine("[SlotTracker] Database config:");
        Console.WriteLine($"Host: {_config.Host}");
        Console.WriteLine($"Database: {_config.Database}");
        Console.WriteLine($"User: {_config.User}");
        Console.WriteLine($"Port: {_config.Port}");

        // Add timer to update server slots once server is ready
        AddTimer(1.0f, () => 
        {
            try 
            {
                _serverSlots = Server.MaxPlayers;
                Console.WriteLine($"[SlotTracker] Updated server slots to: {_serverSlots}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SlotTracker] Error getting server slots: {ex.Message}");
            }
        });
    }

    private void CommandStats(CCSPlayerController? player, CommandInfo command)
    {
        try
        {
            using var conn = new MySqlConnection(GetConnectionString());
            
            // Get the latest record
            const string latestSql = @"
                SELECT * FROM server_stats 
                ORDER BY timestamp DESC 
                LIMIT 1";
            
            var latest = conn.QueryFirstOrDefault<ServerStats>(latestSql);

            if (latest != null)
            {
                var message = $"Latest Stats: {latest.PlayerCount}/{latest.ServerSlots} players (recorded at {latest.Timestamp:yyyy-MM-dd HH:mm:ss})";
                
                if (player != null)
                {
                    player.PrintToChat($" \x04[SlotTracker]\x01 {message}");
                }
                else
                {
                    Console.WriteLine($"[SlotTracker] {message}");
                }

                // Get stats for the last 24 hours
                const string dailyStatsSql = @"
                    SELECT 
                        MAX(player_count) as max_players,
                        AVG(player_count) as avg_players,
                        MIN(player_count) as min_players
                    FROM server_stats 
                    WHERE timestamp >= DATE_SUB(NOW(), INTERVAL 24 HOUR)";

                var dailyStats = conn.QueryFirst(dailyStatsSql);
                var statsMessage = $"24h Stats - Max: {dailyStats.max_players}, Avg: {Math.Round((double)dailyStats.avg_players, 1)}, Min: {dailyStats.min_players}";

                if (player != null)
                {
                    player.PrintToChat($" \x04[SlotTracker]\x01 {statsMessage}");
                }
                else
                {
                    Console.WriteLine($"[SlotTracker] {statsMessage}");
                }
            }
            else
            {
                var message = "No stats recorded yet.";
                if (player != null)
                {
                    player.PrintToChat($" \x04[SlotTracker]\x01 {message}");
                }
                else
                {
                    Console.WriteLine($"[SlotTracker] {message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error retrieving stats: {ex.Message}");
            if (player != null)
            {
                player.PrintToChat(" \x02[SlotTracker]\x01 Error retrieving stats.");
            }
        }
    }

    private void InitializeDatabase()
    {
        try
        {
            Console.WriteLine("[SlotTracker] Initializing database...");
            using var conn = new MySqlConnection(GetConnectionString());
            conn.Open();
            Console.WriteLine("[SlotTracker] Database connection test successful");

            // Create table if it doesn't exist
            const string createTableSql = @"
                CREATE TABLE IF NOT EXISTS server_stats (
                    id BIGINT AUTO_INCREMENT PRIMARY KEY,
                    timestamp DATETIME,
                    player_count INT,
                    server_slots INT
                )";
            
            conn.Execute(createTableSql);
            Console.WriteLine("[SlotTracker] Database initialized successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Database initialization error: {ex.Message}");
            Console.WriteLine($"[SlotTracker] Stack trace: {ex.StackTrace}");
        }
    }

    private void OnMapStart(string mapName)
    {
        // Update server slots after map change
        try 
        {
            _serverSlots = Server.MaxPlayers;
            Console.WriteLine($"[SlotTracker] Server slots updated on map start: {_serverSlots}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error updating server slots on map start: {ex.Message}");
        }
    }

    private DatabaseConfig LoadConfig()
    {
        var configPath = Path.Join(ModuleDirectory, "config.json");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("Config file not found!");
        }

        var jsonString = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize<DatabaseConfig>(jsonString) 
            ?? throw new Exception("Failed to deserialize config!");
    }

    private HookResult OnGameMessage(EventGameMessage @event, GameEventInfo info)
    {
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        Console.WriteLine("[SlotTracker] OnPlayerConnect event triggered");
        
        var player = @event.Userid;
        if (player == null)
        {
            Console.WriteLine("[SlotTracker] Player is null");
            return HookResult.Continue;
        }

        if (player.IsBot)
        {
            Console.WriteLine("[SlotTracker] Skipping bot connect");
            return HookResult.Continue;
        }

        if (player.IsHLTV)
        {
            Console.WriteLine("[SlotTracker] Skipping HLTV connect");
            return HookResult.Continue;
        }

        Console.WriteLine($"[SlotTracker] Player connecting: {player.PlayerName} (SteamID: {player.SteamID})");
        UpdateDatabase(player, "connect");
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        Console.WriteLine("[SlotTracker] OnPlayerDisconnect event triggered");
        
        var player = @event.Userid;
        if (player == null)
        {
            Console.WriteLine("[SlotTracker] Player is null");
            return HookResult.Continue;
        }

        if (player.IsBot)
        {
            Console.WriteLine("[SlotTracker] Skipping bot disconnect");
            return HookResult.Continue;
        }

        if (player.IsHLTV)
        {
            Console.WriteLine("[SlotTracker] Skipping HLTV disconnect");
            return HookResult.Continue;
        }

        // Log disconnect details for debugging
        Console.WriteLine($"[SlotTracker] Disconnect event - Player: {player.PlayerName}, SteamID: {player.SteamID}, Reason: {@event.Reason}");

        // Check for Steam ban disconnect (2006) or kick (6)
        if (@event.Reason == 2006 || @event.Reason == 6)
        {
            Console.WriteLine($"[SlotTracker] Ban/kick detected for {player.PlayerName}, skipping database update");
            return HookResult.Continue;
        }

        UpdateDatabase(player, "disconnect");
        return HookResult.Continue;
    }

    private void UpdateDatabase(CCSPlayerController player, string eventType)
    {
        if (_updatePending)
        {
            Console.WriteLine("[SlotTracker] Update already pending, skipping");
            return;
        }

        _updatePending = true;
        Console.WriteLine($"[SlotTracker] Starting database update for {eventType} - Player: {player.PlayerName}");

        try
        {
            // Get current player count
            var players = Utilities.GetPlayers();
            Console.WriteLine($"[SlotTracker] Total players found: {players.Count}");
            
            var currentPlayers = players
                .Count(p => p != null && 
                       p.Connected == PlayerConnectedState.PlayerConnected && 
                       !p.IsBot && 
                       !p.IsHLTV);

            Console.WriteLine($"[SlotTracker] Connected non-bot players: {currentPlayers}");

            // Adjust count for disconnects since the player is still counted
            if (eventType == "disconnect")
            {
                currentPlayers = Math.Max(0, currentPlayers - 1);
                Console.WriteLine($"[SlotTracker] Adjusted player count after disconnect: {currentPlayers}");
            }

            // Get connection string
            var connectionString = GetConnectionString();
            if (string.IsNullOrEmpty(connectionString))
            {
                Console.WriteLine("[SlotTracker] Error: Invalid connection string");
                return;
            }

            using var conn = new MySqlConnection(connectionString);
            
            // Test connection before proceeding
            try
            {
                conn.Open();
                Console.WriteLine("[SlotTracker] Database connection test successful");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SlotTracker] Failed to connect to database: {ex.Message}");
                Console.WriteLine($"[SlotTracker] Connection error stack trace: {ex.StackTrace}");
                return;
            }

            var stats = new ServerStats
            {
                Timestamp = DateTime.UtcNow,
                PlayerCount = currentPlayers,
                ServerSlots = _serverSlots
            };

            const string insertSql = @"
                INSERT INTO server_stats (timestamp, player_count, server_slots) 
                VALUES (@Timestamp, @PlayerCount, @ServerSlots)";

            try
            {
                var rowsAffected = conn.Execute(insertSql, stats);
                
                if (rowsAffected > 0)
                {
                    Console.WriteLine($"[SlotTracker] Database update successful - {rowsAffected} rows affected");
                    Console.WriteLine($"[SlotTracker] Updated values - Players: {currentPlayers}/{_serverSlots}, Time: {stats.Timestamp:yyyy-MM-dd HH:mm:ss}");
                }
                else
                {
                    Console.WriteLine("[SlotTracker] Warning: Database update completed but no rows were affected");
                }

                // Verify the update by retrieving the latest record
                const string verifySql = @"
                    SELECT * FROM server_stats 
                    ORDER BY timestamp DESC 
                    LIMIT 1";

                var latestRecord = conn.QueryFirstOrDefault<ServerStats>(verifySql);
                if (latestRecord != null)
                {
                    Console.WriteLine($"[SlotTracker] Verification - Latest record: Players: {latestRecord.PlayerCount}/{latestRecord.ServerSlots}, Time: {latestRecord.Timestamp:yyyy-MM-dd HH:mm:ss}");
                }
                else
                {
                    Console.WriteLine("[SlotTracker] Warning: Could not verify the update - no records found");
                }
            }
            catch (MySqlException ex)
            {
                Console.WriteLine($"[SlotTracker] MySQL Error during insert: {ex.Message}");
                Console.WriteLine($"[SlotTracker] Error Code: {ex.Number}");
                Console.WriteLine($"[SlotTracker] Stack Trace: {ex.StackTrace}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Unexpected error in UpdateDatabase: {ex.Message}");
            Console.WriteLine($"[SlotTracker] Stack Trace: {ex.StackTrace}");
        }
        finally
        {
            _updatePending = false;
        }
    }

    private string GetConnectionString()
    {
        try
        {
            if (_config == null)
            {
                Console.WriteLine("[SlotTracker] Error: Database config is null!");
                return string.Empty;
            }

            var connString = $"Server={_config.Host};Database={_config.Database};User={_config.User};Password={_config.Password};Port={_config.Port};";
            Console.WriteLine($"[SlotTracker] Connection string: {connString.Replace(_config.Password, "********")}");
            return connString;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error building connection string: {ex.Message}");
            return string.Empty;
        }
    }

    private HookResult OnServerOutput(string message)
    {
        return HookResult.Continue;
    }
}
