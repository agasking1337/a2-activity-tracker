using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Timers;
using MySqlConnector;
using System.Text.Json;
using A2ActivityTracker.Models;
using DailyServerStats = A2ActivityTracker.Models.ServerStats;

namespace A2ActivityTracker;

[MinimumApiVersion(100)]
public partial class SlotTracker : BasePlugin
{
    public override string ModuleName => "A2ActivityTracker";
    public override string ModuleVersion => "2.0.0";
    public override string ModuleAuthor => "agasking1337";

    private DatabaseConfig _config = null!;
    private int _serverSlots;
    private bool _writeScheduled = false;
    private string _lastEventType = "connect";
    private MapSession? _currentMapSession;
    private readonly List<(ulong SteamId, DateTime ConnectTime)> _pendingOpens = new();
    private readonly List<(ulong SteamId, DateTime DisconnectTime)> _pendingCloses = new();
    private readonly List<MapSession> _pendingMapSessions = new();
    private MapSession? _lastPersistedMapSession = null;
    private readonly object _queueLock = new();

    public class ServerStats
    {
        public DateTime Timestamp { get; set; }
        public int PlayerCount { get; set; }
        public int ServerSlots { get; set; }
    }

    private void ScheduleDailyAggregation()
    {
        try
        {
            var secondsUntilRun = GetSecondsUntilNextAggregation();
            Log($"[A2ActivityTracker] Scheduling daily aggregation in {secondsUntilRun} seconds.");
            AddTimer((float)secondsUntilRun, () =>
            {
                RunDailyAggregation();
                // Schedule next run 24 hours later
                AddTimer(24f * 60f * 60f, RunDailyAggregation);
            });
        }
        catch (Exception ex)
        {
            Log($"[A2ActivityTracker] Failed to schedule daily aggregation: {ex.Message}");
        }
    }

    // Ensure a map session is active by checking the current server map name.
    // Useful when the plugin loads mid-map or when certain engine events are missed.
    private void EnsureMapSessionActive(string? reason = null)
    {
        if (_currentMapSession != null && _currentMapSession.IsActive)
            return;

        string map = string.Empty;
        try { map = Server.MapName ?? string.Empty; } catch { }
        if (!string.IsNullOrEmpty(map))
        {
            _currentMapSession = new MapSession();
            _currentMapSession.Start(map, DateTime.Now);
            
            // Immediately save the new map session to database
            SaveMapSessionToDatabase(_currentMapSession);
            
            Log($"[A2ActivityTracker] EnsureMapSessionActive: started map session for '{map}' and saved to database (reason={reason})");
        }
    }

    private double GetSecondsUntilNextAggregation()
    {
        // Run daily at 00:05 server local time
        var now = DateTime.Now;
        var next = new DateTime(now.Year, now.Month, now.Day, 0, 5, 0, now.Kind);
        if (now >= next)
        {
            next = next.AddDays(1);
        }
        return (next - now).TotalSeconds;
    }

    private void RunDailyAggregation()
    {
        Log("[A2ActivityTracker] Running daily aggregation...");
        var connectionString = GetConnectionString();
        if (string.IsNullOrEmpty(connectionString))
        {
            Log("[A2ActivityTracker] Skipping daily aggregation: invalid connection string");
            return;
        }

        using var conn = new MySqlConnection(connectionString);
        try
        {
            conn.Open();

            // Generate analytics for yesterday (UTC date derived from server local time - 1 day)
            var dateUtc = DateTime.UtcNow.AddDays(-1).Date;
            Analytics.GenerateDailyAnalytics(conn, dateUtc, Log);
            Log("[A2ActivityTracker] Daily analytics generated successfully.");
        }
        catch (Exception ex)
        {
            Log($"[A2ActivityTracker] Daily aggregation failed: {ex.Message}");
        }
    }

    public override void Load(bool hotReload)
    {
        base.Load(hotReload);
        
        Log("[A2ActivityTracker] Plugin loading...");
        
        // Ensure config file exists (create default if missing) and load it
        EnsureConfigFile();
        _config = LoadConfig();

        // Initialize database only if config appears to be filled with real credentials
        if (IsConfigFilled(_config))
        {
            InitializeDatabase();
            ScheduleDailyAggregation();
        }
        else
        {
            Log("[A2ActivityTracker] Config has placeholder or empty values. Skipping database initialization until valid credentials are provided.");
        }

        // Initialize with default value
        _serverSlots = 10; // Default value
        Log($"[A2ActivityTracker] Server initialized with default slots: {_serverSlots}");

        // Register event handlers
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnect, HookMode.Post);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect, HookMode.Pre);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        // Register debug-only command handler (runtime check ensures debug mode)
        AddCommand("css_serverstats", "Aggregate today's analytics into a2_server_analytics (debug only)", CommandServerStats);
        AddCommand("css_stats_now", "Force-write a snapshot to a2_server_stats (debug only)", CommandStatsNow);
        AddCommand("css_who", "Show current non-bot players and map info (debug)", CommandWho);
        
        Log("[A2ActivityTracker] Plugin loaded successfully!");

        // Add timer to update server slots once server is ready
        AddTimer(1.0f, () => 
        {
            try 
            {
                _serverSlots = Server.MaxPlayers;
                Log($"[A2ActivityTracker] Updated server slots to: {_serverSlots}");
            }
            catch (Exception ex)
            {
                Log($"[A2ActivityTracker] Error getting server slots: {ex.Message}");
            }
        });

        // Initialize current map session if the plugin is loaded while a map is already running
        AddTimer(1.2f, () =>
        {
            try
            {
                string map = string.Empty;
                try { map = Server.MapName ?? string.Empty; } catch { }
                if (!string.IsNullOrEmpty(map) && (_currentMapSession == null || !_currentMapSession.IsActive))
                {
                    _currentMapSession = new MapSession();
                    _currentMapSession.Start(map, DateTime.Now);
                    
                    // Immediately save the new map session to database
                    SaveMapSessionToDatabase(_currentMapSession);
                    
                    Log($"[A2ActivityTracker] Initialized map session on load and saved to database: {map}");
                }
            }
            catch (Exception ex)
            {
                Log($"[A2ActivityTracker] Error initializing map session on load: {ex.Message}");
            }
        });

        // Start debug heartbeat (only logs when debug is enabled/overridden)
        StartDebugHeartbeat();

        // Start pooled flush timer (every 60s)
        StartPooledFlush();
    }

    // Removed CommandStats and any command registration to simplify the plugin

    private void InitializeDatabase()
    {
        try
        {
            Log("[A2ActivityTracker] Initializing database...");
            using var conn = new MySqlConnection(GetConnectionString());
            conn.Open();
            Log("[A2ActivityTracker] Database connection test successful");

            // Create table if it doesn't exist
            using (var cmd = new MySqlCommand(DbSql.CreateAnalyticsTable, conn))
            {
                cmd.ExecuteNonQuery();
            }

            // Create index on timestamp to speed up latest and range queries
            try
            {
                using var idxCmd = new MySqlCommand(DbSql.CreateAnalyticsTimestampIndex, conn);
                idxCmd.ExecuteNonQuery();
            }
            catch (MySqlException idxEx)
            {
                // 1061: duplicate key name (index already exists) — safe to ignore
                if (idxEx.Number != 1061)
                {
                    Log($"[A2ActivityTracker] Index creation warning: {idxEx.Message}");
                }
            }

            // Create daily stats table if missing (date-based only)
            using (var dailyCmd = new MySqlCommand(DbSql.CreateDailyStatsTable, conn))
            {
                dailyCmd.ExecuteNonQuery();
            }

            // Attempt to migrate legacy schema: drop old index and columns if they exist
            try
            {
                using var dropIdx = new MySqlCommand(DbSql.DropLegacyDailyIndex, conn);
                dropIdx.ExecuteNonQuery();
            }
            catch (MySqlException idxEx)
            {
                // 1091: Can't DROP; check that column/key exists — safe to ignore
                if (idxEx.Number != 1091)
                {
                    Log($"[A2ActivityTracker] Index drop warning: {idxEx.Message}");
                }
            }

            foreach (var legacyCol in DbSql.DropLegacyDailyColumns)
            {
                try
                {
                    using var dropCol = new MySqlCommand($"ALTER TABLE a2_server_analytics DROP COLUMN {legacyCol}", conn);
                    dropCol.ExecuteNonQuery();
                }
                catch (MySqlException colEx)
                {
                    // 1091: Can't DROP; check that column/key exists
                    // 1054: Unknown column — both safe to ignore
                    if (colEx.Number != 1091 && colEx.Number != 1054)
                    {
                        Log($"[A2ActivityTracker] Column drop warning for '{legacyCol}': {colEx.Message}");
                    }
                }
            }

            // Create map sessions table
            using (var mapCmd = new MySqlCommand(DbSql.CreateMapSessionsTable, conn))
            {
                mapCmd.ExecuteNonQuery();
            }

            // Create player sessions table
            using (var psCmd = new MySqlCommand(DbSql.CreatePlayerSessionsTable, conn))
            {
                psCmd.ExecuteNonQuery();
            }
            

            // Ensure additional analytics schema (extra columns and per-map daily table)
            Analytics.EnsureAnalyticsSchema(conn, Log);

            // No additional index required: PRIMARY KEY on date_utc is sufficient.
            Log("[A2ActivityTracker] Database initialized successfully");
        }
        catch (Exception ex)
        {
            Log($"[A2ActivityTracker] Database initialization error: {ex.Message}");
            Log($"[A2ActivityTracker] Stack trace: {ex.StackTrace}");
        }
    }

    private void OnMapStart(string mapName)
    {
        // Update server slots after map change
        try 
        {
            _serverSlots = Server.MaxPlayers;
            Log($"[A2ActivityTracker] Server slots updated on map start: {_serverSlots}");
            Log($"[A2ActivityTracker] Map started: {mapName}");

            // Close previous map session if one is open
            if (_currentMapSession != null && _currentMapSession.IsActive)
            {
                _currentMapSession.End(DateTime.Now);
                PersistMapSession(_currentMapSession);
            }

            // Start a new map session
            _currentMapSession = new MapSession();
            _currentMapSession.Start(mapName, DateTime.Now);
            
            // Immediately save the new map session to database
            SaveMapSessionToDatabase(_currentMapSession);
            
            Log($"[A2ActivityTracker] New map session created and saved to database: {mapName}");
        }
        catch (Exception ex)
        {
            Log($"[A2ActivityTracker] Error updating server slots on map start: {ex.Message}");
        }
    }

    private void OnMapEnd()
    {
        try
        {
            string map = string.Empty;
            try { map = Server.MapName ?? string.Empty; } catch { }
            Log($"[A2ActivityTracker] Map ended: {map}");

            // End and persist current map session
            if (_currentMapSession != null && _currentMapSession.IsActive)
            {
                _currentMapSession.End(DateTime.Now);
                PersistMapSession(_currentMapSession);
            }
        }
        catch (Exception ex)
        {
            Log($"[A2ActivityTracker] Error in OnMapEnd: {ex.Message}");
        }
    }

    private DatabaseConfig LoadConfig()
    {
        var configPath = GetConfigFilePath();
        string? loadPath = null;

        if (File.Exists(configPath))
        {
            loadPath = configPath;
        }
        else
        {
            // Try legacy configs path first (pre-plugins subfolder)
            var legacy = GetLegacyConfigFilePath();
            if (File.Exists(legacy))
            {
                try
                {
                    var dir = Path.GetDirectoryName(configPath)!;
                    Directory.CreateDirectory(dir);
                    File.Copy(legacy, configPath, overwrite: true);
                    loadPath = configPath;
                    Log($"[A2ActivityTracker] Migrated legacy config from '{legacy}' to '{configPath}'.");
                }
                catch (Exception migLegacyEx)
                {
                    Log($"[A2ActivityTracker] Could not migrate legacy config to new path: {migLegacyEx.Message}. Will use legacy path.");
                    loadPath = legacy;
                }
            }

            // Then try fallback next to plugin DLL
            if (loadPath == null)
            {
                var fallback = GetFallbackConfigPath();
                if (File.Exists(fallback))
                {
                    // Try to migrate to new configs path
                    try
                    {
                        var dir = Path.GetDirectoryName(configPath)!;
                        Directory.CreateDirectory(dir);
                        File.Copy(fallback, configPath, overwrite: true);
                        loadPath = configPath;
                        Log($"[A2ActivityTracker] Migrated existing config from '{fallback}' to '{configPath}'.");
                    }
                    catch (Exception migEx)
                    {
                        Log($"[A2ActivityTracker] Could not migrate config to configs path: {migEx.Message}. Will use fallback path.");
                        loadPath = fallback;
                    }
                }
            }
        }

        if (loadPath == null)
        {
            throw new FileNotFoundException("Config file not found in configs or fallback path. A default config should have been created automatically.");
        }

        var jsonString = File.ReadAllText(loadPath);
        return JsonSerializer.Deserialize<DatabaseConfig>(jsonString)
            ?? throw new Exception("Failed to deserialize config!");
    }

    private void EnsureConfigFile()
    {
        try
        {
            var configPath = GetConfigFilePath();
            if (!File.Exists(configPath))
            {
                // Create default config with placeholder values
                var defaultConfig = new DatabaseConfig
                {
                    Host = "YOUR_DATABASE_HOST",
                    Port = 3306,
                    Database = "YOUR_DATABASE_NAME",
                    User = "YOUR_DATABASE_USERNAME",
                    Password = "YOUR_DATABASE_PASSWORD"
                };

                // Ensure configs directory exists
                var dir = Path.GetDirectoryName(configPath)!;
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch (Exception dirEx)
                {
                    Log($"[A2ActivityTracker] Failed to create configs directory '{dir}': {dirEx.Message}");
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(defaultConfig, options);
                try
                {
                    File.WriteAllText(configPath, json);
                }
                catch (Exception writeEx)
                {
                    Log($"[A2ActivityTracker] Failed to write config at '{configPath}': {writeEx.Message}");
                }

                // Verify creation
                if (File.Exists(configPath))
                {
                    Log($"[A2ActivityTracker] No config.json found. A default config has been created at: {configPath}");
                    Log("[A2ActivityTracker] Please update the file with your MySQL credentials. Database initialization will be attempted on next load.");
                }
                else
                {
                    // Fallback to ModuleDirectory
                    var fallbackPath = GetFallbackConfigPath();
                    try
                    {
                        var fallbackDir = Path.GetDirectoryName(fallbackPath)!;
                        Directory.CreateDirectory(fallbackDir);
                        File.WriteAllText(fallbackPath, json);
                        if (File.Exists(fallbackPath))
                        {
                            Log($"[A2ActivityTracker] Warning: Failed to create config in configs path. Created fallback config at: {fallbackPath}");
                            Log("[A2ActivityTracker] You can move this file to the configs path or leave it here; the plugin prefers the configs path.");
                        }
                        else
                        {
                            Log("[A2ActivityTracker] Error: Could not create config file in either location.");
                        }
                    }
                    catch (Exception fbEx)
                    {
                        Log($"[A2ActivityTracker] Fallback config creation failed: {fbEx.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[A2ActivityTracker] Failed to ensure/create config file: {ex.Message}");
        }
    }

    private string GetConfigFilePath()
    {
        // ModuleDirectory is typically: game/csgo/addons/counterstrikesharp/plugins/a2-activity-tracker
        // We want: game/csgo/addons/counterstrikesharp/configs/plugins/a2-activity-tracker/config.json
        // So go up two levels to reach counterstrikesharp, then into configs/plugins/<plugin>/config.json
        var counterStrikeSharpRoot = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", ".."));
        var configDir = Path.Combine(counterStrikeSharpRoot, "configs", "plugins", "a2-activity-tracker");
        var configPath = Path.Combine(configDir, "config.json");
        return configPath;
    }

    private string GetFallbackConfigPath()
    {
        // Old behavior: put config.json directly next to the plugin DLL
        return Path.Combine(ModuleDirectory, "config.json");
    }

    private string GetLegacyConfigFilePath()
    {
        // Legacy path from old plugin name without the plugins subfolder
        var counterStrikeSharpRoot = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", ".."));
        var configDir = Path.Combine(counterStrikeSharpRoot, "configs", "cs2-slots-tracker");
        return Path.Combine(configDir, "config.json");
    }

    private bool IsConfigFilled(DatabaseConfig cfg)
    {
        try
        {
            if (cfg == null) return false;

            bool NotEmpty(string? s) => !string.IsNullOrWhiteSpace(s);

            var placeholderUsers = new[] { "YOUR_DATABASE_USER", "YOUR_DATABASE_USERNAME" };

            bool hostOk = NotEmpty(cfg.Host) && cfg.Host != "YOUR_DATABASE_HOST";
            bool dbOk = NotEmpty(cfg.Database) && cfg.Database != "YOUR_DATABASE_NAME";
            bool userOk = NotEmpty(cfg.User) && !placeholderUsers.Contains(cfg.User);
            bool passOk = NotEmpty(cfg.Password) && cfg.Password != "YOUR_DATABASE_PASSWORD";
            bool portOk = cfg.Port > 0;

            return hostOk && dbOk && userOk && passOk && portOk;
        }
        catch
        {
            return false;
        }
    }

    private HookResult OnGameMessage(EventGameMessage @event, GameEventInfo info)
    {
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        Log("[A2ActivityTracker] OnPlayerConnect event triggered");
        
        var player = @event.Userid;
        if (player == null)
        {
            Log("[A2ActivityTracker] Player is null");
            return HookResult.Continue;
        }

        if (player.IsBot)
        {
            Log("[A2ActivityTracker] Skipping bot connect");
            return HookResult.Continue;
        }

        if (player.IsHLTV)
        {
            Log("[A2ActivityTracker] Skipping HLTV connect");
            return HookResult.Continue;
        }

        Log($"[A2ActivityTracker] Player connecting: {player.PlayerName} (SteamID: {player.SteamID})");
        // Player session debug log
        Log($"[PlayerSession] OPEN steam_id={player.SteamID} connect_time={DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        // Update current map session runtime state
        try
        {
            EnsureMapSessionActive("player_connect");
            _currentMapSession?.OnPlayerConnect(player.SteamID, DateTime.Now);
        }
        catch { }

        // Enqueue player session open (pooled write)
        lock (_queueLock)
        {
            _pendingOpens.Add((player.SteamID, DateTime.Now));
        }
        ScheduleDebouncedWrite("connect");
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        Log("[A2ActivityTracker] OnPlayerDisconnect event triggered");
        
        var player = @event.Userid;
        if (player == null)
        {
            Log("[A2ActivityTracker] Player is null");
            return HookResult.Continue;
        }

        if (player.IsBot)
        {
            Log("[A2ActivityTracker] Skipping bot disconnect");
            return HookResult.Continue;
        }

        if (player.IsHLTV)
        {
            Log("[A2ActivityTracker] Skipping HLTV disconnect");
            return HookResult.Continue;
        }

        // Log disconnect details for debugging
        Log($"[A2ActivityTracker] Disconnect event - Player: {player.PlayerName}, SteamID: {player.SteamID}, Reason: {@event.Reason}");
        // Player session debug log
        Log($"[PlayerSession] CLOSE steam_id={player.SteamID} disconnect_time={DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        // Update current map session runtime state
        try
        {
            EnsureMapSessionActive("player_disconnect");
            _currentMapSession?.OnPlayerDisconnect(player.SteamID, DateTime.Now);
        }
        catch { }

        // Enqueue player session close (pooled write)
        lock (_queueLock)
        {
            _pendingCloses.Add((player.SteamID, DateTime.Now));
        }

        // Check for Steam ban disconnect (2006) or kick (6)
        if (@event.Reason == 2006 || @event.Reason == 6)
        {
            Log($"[A2ActivityTracker] Ban/kick detected for {player.PlayerName}, skipping database update");
            return HookResult.Continue;
        }

        ScheduleDebouncedWrite("disconnect");
        return HookResult.Continue;
    }

    private void UpdateDatabase(CCSPlayerController player, string eventType)
    {
        Log($"[A2ActivityTracker] Starting database update for {eventType} - Player: {player.PlayerName}");

        try
        {
            // Compute current player count (adjust for disconnect events)
            var currentPlayers = GetConnectedPlayersCount();
            if (eventType == "disconnect") currentPlayers = Math.Max(0, currentPlayers - 1);

            InsertStats(currentPlayers);
        }
        catch (Exception ex)
        {
            Log($"[A2ActivityTracker] Unexpected error in UpdateDatabase: {ex.Message}");
            Log($"[A2ActivityTracker] Stack Trace: {ex.StackTrace}");
        }
    }

    private int GetConnectedPlayersCount()
    {
        var players = Utilities.GetPlayers();
        Log($"[A2ActivityTracker] Total players found: {players.Count}");
        var currentPlayers = players
            .Count(p => p != null &&
                   p.Connected == PlayerConnectedState.PlayerConnected &&
                   !p.IsBot &&
                   !p.IsHLTV);
        Log($"[A2ActivityTracker] Connected non-bot players: {currentPlayers}");
        return currentPlayers;
    }

    private void ScheduleDebouncedWrite(string eventType)
    {
        _lastEventType = eventType;
        if (_writeScheduled)
        {
            Log("[A2ActivityTracker] Write already scheduled, debouncing additional events.");
            return;
        }

        _writeScheduled = true;
        AddTimer(2.0f, () =>
        {
            try
            {
                var current = GetConnectedPlayersCount();
                if (_lastEventType == "disconnect") current = Math.Max(0, current - 1);
                InsertStats(current);
            }
            catch (Exception ex)
            {
                Log($"[A2ActivityTracker] Error during debounced write: {ex.Message}");
            }
            finally
            {
                _writeScheduled = false;
            }
        });
    }

    private void InsertStats(int currentPlayers)
    {
        // Get connection string
        var connectionString = GetConnectionString();
        if (string.IsNullOrEmpty(connectionString))
        {
            Log("[A2ActivityTracker] Error: Invalid connection string");
            return;
        }

        using var conn = new MySqlConnection(connectionString);

        // Test connection before proceeding
        try
        {
            conn.Open();
            Log("[A2ActivityTracker] Database connection test successful");
        }
        catch (Exception ex)
        {
            Log($"[A2ActivityTracker] Failed to connect to database: {ex.Message}");
            Log($"[A2ActivityTracker] Connection error stack trace: {ex.StackTrace}");
            return;
        }

        var stats = new ServerStats
        {
            Timestamp = DateTime.UtcNow,
            PlayerCount = currentPlayers,
            ServerSlots = _serverSlots
        };

        const string insertSql = @"
            INSERT INTO a2_server_stats (timestamp, player_count, server_slots) 
            VALUES (@ts, @pc, @ss)";

        try
        {
            using (var cmd = new MySqlCommand(insertSql, conn))
            {
                cmd.Parameters.AddWithValue("@ts", stats.Timestamp);
                cmd.Parameters.AddWithValue("@pc", stats.PlayerCount);
                cmd.Parameters.AddWithValue("@ss", stats.ServerSlots);
                var rowsAffected = cmd.ExecuteNonQuery();

                if (rowsAffected > 0)
                {
                    Log($"[A2ActivityTracker] Database update successful - {rowsAffected} rows affected");
                    Log($"[A2ActivityTracker] Updated values - Players: {currentPlayers}/{_serverSlots}, Time: {stats.Timestamp:yyyy-MM-dd HH:mm:ss}");
                }
                else
                {
                    Log("[A2ActivityTracker] Warning: Database update completed but no rows were affected");
                }
            }

            // Verify the update by retrieving the latest record
            const string verifySql = @"
                SELECT timestamp, player_count, server_slots FROM a2_server_stats 
                ORDER BY timestamp DESC 
                LIMIT 1";

            using (var verifyCmd = new MySqlCommand(verifySql, conn))
            using (var reader = verifyCmd.ExecuteReader())
            {
                if (reader.Read())
                {
                    var latestRecord = new ServerStats
                    {
                        Timestamp = reader.GetDateTime("timestamp"),
                        PlayerCount = reader.GetInt32("player_count"),
                        ServerSlots = reader.GetInt32("server_slots")
                    };
                    Log($"[A2ActivityTracker] Verification - Latest record: Players: {latestRecord.PlayerCount}/{latestRecord.ServerSlots}, Time: {latestRecord.Timestamp:yyyy-MM-dd HH:mm:ss}");
                }
                else
                {
                    Log("[A2ActivityTracker] Warning: Could not verify the update - no records found");
                }
            }
        }
        catch (MySqlException ex)
        {
            Log($"[A2ActivityTracker] MySQL Error during insert: {ex.Message}");
            Log($"[A2ActivityTracker] Error Code: {ex.Number}");
            Log($"[A2ActivityTracker] Stack Trace: {ex.StackTrace}");
        }
    }

    private string GetConnectionString()
    {
        try
        {
            if (_config == null)
            {
                Log("[A2ActivityTracker] Error: Database config is null!");
                return string.Empty;
            }

            var connString = $"Server={_config.Host};Database={_config.Database};User={_config.User};Password={_config.Password};Port={_config.Port};";
            // Do not print the full connection string to avoid leaking secrets
            Log("[A2ActivityTracker] Built database connection string (hidden)");
            return connString;
        }
        catch (Exception ex)
        {
            Log($"[A2ActivityTracker] Error building connection string: {ex.Message}");
            return string.Empty;
        }
    }

    private void StartDebugHeartbeat()
    {
        const float interval = 10.0f;
        void Tick()
        {
            try
            {
                // Ensure map session exists even if plugin loaded mid-map or other events were missed
                try { EnsureMapSessionActive("heartbeat"); } catch { }
                // Always accumulate map session playtime regardless of debug mode
                try { _currentMapSession?.Tick(DateTime.Now); } catch { }

                // Only emit heartbeat logs when DebugMode is enabled
                if (_config != null && _config.DebugMode)
                {
                    string map = string.Empty;
                    try { map = Server.MapName ?? string.Empty; } catch { }
                    var count = GetConnectedPlayersCount();
                    Log($"[A2ActivityTracker] Heartbeat - Map: {map}, Players: {count}/{_serverSlots}");
                }
            }
            catch { }
            finally
            {
                AddTimer(interval, Tick);
            }
        }
        AddTimer(interval, Tick);
    }

    private void StartPooledFlush()
    {
        const float interval = 60.0f; // every minute
        void Tick()
        {
            try
            {
                FlushPooledWork();
            }
            catch (Exception ex)
            {
                Log($"[A2ActivityTracker] FlushPooledWork error: {ex.Message}");
            }
            finally
            {
                AddTimer(interval, Tick);
            }
        }
        AddTimer(interval, Tick);
    }

    private void FlushPooledWork()
    {
        // Snapshot current pending work
        List<(ulong SteamId, DateTime ConnectTime)> opens;
        List<(ulong SteamId, DateTime DisconnectTime)> closes;
        List<MapSession> mapSessions;
        lock (_queueLock)
        {
            opens = new List<(ulong, DateTime)>(_pendingOpens);
            closes = new List<(ulong, DateTime)>(_pendingCloses);
            mapSessions = new List<MapSession>(_pendingMapSessions);
            _pendingOpens.Clear();
            _pendingCloses.Clear();
            _pendingMapSessions.Clear();
        }

        var cs = GetConnectionString();
        if (string.IsNullOrEmpty(cs)) { Log("[A2ActivityTracker] FlushPooledWork: empty connection string"); return; }

        using var conn = new MySqlConnection(cs);
        conn.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            // Process opens - players connecting
            if (opens.Count > 0)
            {
                using var cmdOpen = new MySqlCommand(@"INSERT INTO a2_player_sessions
                        (steam_id, connect_time, session_count)
                        VALUES (@sid, @now, 1)
                        ON DUPLICATE KEY UPDATE
                            connect_time = @now,
                            disconnect_time = NULL,
                            session_count = session_count + 1", conn, tx);
                cmdOpen.Parameters.Add("@sid", MySqlDbType.UInt64);
                cmdOpen.Parameters.Add("@now", MySqlDbType.DateTime);
                
                foreach (var o in opens)
                {
                    cmdOpen.Parameters["@sid"].Value = o.SteamId;
                    cmdOpen.Parameters["@now"].Value = o.ConnectTime;
                    cmdOpen.ExecuteNonQuery();
                    Log($"[A2ActivityTracker] Player connect processed: SteamID={o.SteamId}, Time={o.ConnectTime:yyyy-MM-dd HH:mm:ss}");
                }
            }

            // Process closes - players disconnecting
            if (closes.Count > 0)
            {
                using var cmdClose = new MySqlCommand(@"UPDATE a2_player_sessions
                        SET disconnect_time = @now,
                            duration_seconds = CASE 
                                WHEN connect_time IS NOT NULL THEN 
                                    duration_seconds + TIMESTAMPDIFF(SECOND, connect_time, @now) 
                                ELSE duration_seconds
                            END
                        WHERE steam_id = @sid", conn, tx);
                cmdClose.Parameters.Add("@sid", MySqlDbType.UInt64);
                cmdClose.Parameters.Add("@now", MySqlDbType.DateTime);
                
                foreach (var c in closes)
                {
                    cmdClose.Parameters["@sid"].Value = c.SteamId;
                    cmdClose.Parameters["@now"].Value = c.DisconnectTime;
                    cmdClose.ExecuteNonQuery();
                    
                    // Query the updated duration for verification
                    using var verifyCmd = new MySqlCommand("SELECT connect_time, disconnect_time, duration_seconds, session_count FROM a2_player_sessions WHERE steam_id = @sid", conn, tx);
                    verifyCmd.Parameters.AddWithValue("@sid", c.SteamId);
                    using var reader = verifyCmd.ExecuteReader();
                    if (reader.Read())
                    {
                        var connectTime = reader.GetDateTime("connect_time");
                        var disconnectTime = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime("disconnect_time");
                        var duration = reader.GetInt64("duration_seconds");
                        var sessions = reader.GetInt32("session_count");
                        Log($"[A2ActivityTracker] Player session updated: SteamID={c.SteamId}, Connect={connectTime:yyyy-MM-dd HH:mm:ss}, Disconnect={disconnectTime:yyyy-MM-dd HH:mm:ss}, Duration={duration}s, Sessions={sessions}");
                    }
                    reader.Close();
                }
            }

            // Process completed map sessions
            if (mapSessions.Count > 0)
            {
                foreach (var session in mapSessions)
                {
                    if (session.DatabaseId.HasValue)
                    {
                        // Update existing record with end time and final stats
                        using var updateCmd = new MySqlCommand(@"UPDATE a2_map_sessions
                                SET map_end = @me, 
                                    total_players_seen = @seen, 
                                    total_playtime = @play
                                WHERE id = @id", conn, tx);
                        updateCmd.Parameters.AddWithValue("@id", session.DatabaseId.Value);
                        updateCmd.Parameters.AddWithValue("@me", (object?)session.MapEnd ?? DBNull.Value);
                        updateCmd.Parameters.AddWithValue("@seen", session.TotalPlayersSeen);
                        updateCmd.Parameters.AddWithValue("@play", session.TotalPlaytime);
                        updateCmd.ExecuteNonQuery();
                        
                        Log($"[A2ActivityTracker] Updated completed map session (ID: {session.DatabaseId}) - map={session.MapName}, seen={session.TotalPlayersSeen}, play={session.TotalPlaytime}s");
                    }
                    else
                    {
                        // Insert new record for completed map session without existing ID
                        using var insertCmd = new MySqlCommand(@"INSERT INTO a2_map_sessions
                                (map_name, map_start, map_end, total_players_seen, total_playtime)
                                VALUES (@mn, @ms, @me, @seen, @play)", conn, tx);
                        insertCmd.Parameters.AddWithValue("@mn", session.MapName);
                        insertCmd.Parameters.AddWithValue("@ms", session.MapStart);
                        insertCmd.Parameters.AddWithValue("@me", (object?)session.MapEnd ?? DBNull.Value);
                        insertCmd.Parameters.AddWithValue("@seen", session.TotalPlayersSeen);
                        insertCmd.Parameters.AddWithValue("@play", session.TotalPlaytime);
                        insertCmd.ExecuteNonQuery();
                        
                        Log($"[A2ActivityTracker] Inserted new completed map session - map={session.MapName}, seen={session.TotalPlayersSeen}, play={session.TotalPlaytime}s");
                    }
                }
                Log($"[A2ActivityTracker] Processed {mapSessions.Count} completed map sessions");
            }

            // Update active player sessions every minute
            try
            {
                // Get all active players (currently connected)
                using var activePlayersCmd = new MySqlCommand(@"UPDATE a2_player_sessions
                        SET duration_seconds = duration_seconds + 60
                        WHERE disconnect_time IS NULL", conn, tx);
                var updatedRows = activePlayersCmd.ExecuteNonQuery();
                if (updatedRows > 0)
                {
                    Log($"[A2ActivityTracker] Updated {updatedRows} active player sessions (+60 seconds)");
                    
                    // Get details of active sessions for debugging
                    using var detailsCmd = new MySqlCommand(@"SELECT steam_id, connect_time, duration_seconds, session_count 
                            FROM a2_player_sessions WHERE disconnect_time IS NULL", conn, tx);
                    using var reader = detailsCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var steamId = reader.GetUInt64("steam_id");
                        var connectTime = reader.GetDateTime("connect_time");
                        var duration = reader.GetInt64("duration_seconds");
                        var sessions = reader.GetInt32("session_count");
                        Log($"[A2ActivityTracker] Active player: SteamID={steamId}, Connect={connectTime:yyyy-MM-dd HH:mm:ss}, Duration={duration}s, Sessions={sessions}");
                    }
                    reader.Close();
                }
            }
            catch (Exception ex)
            {
                Log($"[A2ActivityTracker] Error updating active player sessions: {ex.Message}");
            }
            
            // Also update current map session stats every minute if active
            if (_currentMapSession != null && _currentMapSession.IsActive)
            {
                // Update the playtime for the current map session
                _currentMapSession.Tick(DateTime.Now);
                
                // Always update the map stats every minute
                {
                    if (_currentMapSession.DatabaseId.HasValue)
                    {
                        // Update existing record if we have a database ID
                        using var updateCmd = new MySqlCommand(@"UPDATE a2_map_sessions
                                SET total_players_seen = @seen, 
                                    total_playtime = @play
                                WHERE id = @id", conn, tx);
                        updateCmd.Parameters.AddWithValue("@id", _currentMapSession.DatabaseId.Value);
                        updateCmd.Parameters.AddWithValue("@seen", _currentMapSession.TotalPlayersSeen);
                        updateCmd.Parameters.AddWithValue("@play", _currentMapSession.TotalPlaytime);
                        updateCmd.ExecuteNonQuery();
                        
                        Log($"[A2ActivityTracker] Minute update: Map session (ID: {_currentMapSession.DatabaseId}) - map={_currentMapSession.MapName}, seen={_currentMapSession.TotalPlayersSeen}, play={_currentMapSession.TotalPlaytime}s");
                    }
                    else
                    {
                        // For active maps without a database ID, insert a new record and get the ID back
                        using var insertCmd = new MySqlCommand(@"INSERT INTO a2_map_sessions
                                (map_name, map_start, map_end, total_players_seen, total_playtime)
                                VALUES (@mn, @ms, NULL, @seen, @play);
                                SELECT LAST_INSERT_ID();", conn, tx);
                        insertCmd.Parameters.AddWithValue("@mn", _currentMapSession.MapName);
                        insertCmd.Parameters.AddWithValue("@ms", _currentMapSession.MapStart);
                        insertCmd.Parameters.AddWithValue("@seen", _currentMapSession.TotalPlayersSeen);
                        insertCmd.Parameters.AddWithValue("@play", _currentMapSession.TotalPlaytime);
                        
                        // Get the newly inserted ID
                        var newId = Convert.ToInt32(insertCmd.ExecuteScalar());
                        _currentMapSession.DatabaseId = newId;
                        
                        Log($"[A2ActivityTracker] Created new map session (ID: {newId}) - map={_currentMapSession.MapName}, seen={_currentMapSession.TotalPlayersSeen}, play={_currentMapSession.TotalPlaytime}s");
                    }
                    
                    // Update our reference with the latest persisted values
                    _lastPersistedMapSession = new MapSession();
                    _lastPersistedMapSession.Start(_currentMapSession.MapName, _currentMapSession.MapStart);
                    _lastPersistedMapSession.TotalPlayersSeen = _currentMapSession.TotalPlayersSeen;
                    _lastPersistedMapSession.TotalPlaytime = _currentMapSession.TotalPlaytime;
                    _lastPersistedMapSession.DatabaseId = _currentMapSession.DatabaseId;
                }
            }

            // Also write a snapshot row once per flush (current players)
            try
            {
                var current = GetConnectedPlayersCount();
                using var snapCmd = new MySqlCommand(@"INSERT INTO a2_server_stats (timestamp, player_count, server_slots) VALUES (@ts, @pc, @ss)", conn, tx);
                snapCmd.Parameters.AddWithValue("@ts", DateTime.UtcNow);
                snapCmd.Parameters.AddWithValue("@pc", current);
                snapCmd.Parameters.AddWithValue("@ss", _serverSlots);
                snapCmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Log($"[A2ActivityTracker] Snapshot during flush failed: {ex.Message}");
            }

            tx.Commit();
            Log($"[A2ActivityTracker] FlushPooledWork committed. Opens={opens.Count}, Closes={closes.Count}, MapSessions={mapSessions.Count}");
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { }
            Log($"[A2ActivityTracker] FlushPooledWork rollback due to error: {ex.Message}");
        }
    }

    private HookResult OnServerOutput(string message)
    {
        return HookResult.Continue;
    }

    private void Log(string message)
    {
        try
        {
            if (_config != null && _config.DebugMode)
            {
                Console.WriteLine(message);
            }
        }
        catch
        {
            // Swallow logging errors
        }
    }

    private void PersistMapSession(MapSession session)
    {
        try
        {
            lock (_queueLock)
            {
                _pendingMapSessions.Add(session);
                Log($"[A2ActivityTracker] MapSession queued for persistence - map={session.MapName}, seen={session.TotalPlayersSeen}, play={session.TotalPlaytime}s");
            }
        }
        catch (Exception ex)
        {
            Log($"[A2ActivityTracker] PersistMapSession error: {ex.Message}");
        }
    }
    
    private void SaveMapSessionToDatabase(MapSession session)
    {
        try
        {
            var cs = GetConnectionString();
            if (string.IsNullOrEmpty(cs)) { Log("[A2ActivityTracker] SaveMapSessionToDatabase: empty connection string"); return; }
            
            using var conn = new MySqlConnection(cs);
            conn.Open();
            
            // For new map sessions, insert a record and get the ID back
            using var insertCmd = new MySqlCommand(@"INSERT INTO a2_map_sessions
                    (map_name, map_start, map_end, total_players_seen, total_playtime)
                    VALUES (@mn, @ms, NULL, @seen, @play);
                    SELECT LAST_INSERT_ID();", conn);
            insertCmd.Parameters.AddWithValue("@mn", session.MapName);
            insertCmd.Parameters.AddWithValue("@ms", session.MapStart);
            insertCmd.Parameters.AddWithValue("@seen", session.TotalPlayersSeen);
            insertCmd.Parameters.AddWithValue("@play", session.TotalPlaytime);
            
            // Get the newly inserted ID
            var newId = Convert.ToInt32(insertCmd.ExecuteScalar());
            session.DatabaseId = newId;
            
            Log($"[A2ActivityTracker] Map session immediately saved to database (ID: {newId}) - map={session.MapName}");
            
            // Update our reference to avoid frequent saves in the pooled flush
            _lastPersistedMapSession = new MapSession();
            _lastPersistedMapSession.Start(session.MapName, session.MapStart);
            _lastPersistedMapSession.TotalPlayersSeen = session.TotalPlayersSeen;
            _lastPersistedMapSession.TotalPlaytime = session.TotalPlaytime;
            _lastPersistedMapSession.DatabaseId = session.DatabaseId;
        }
        catch (Exception ex)
        {
            Log($"[A2ActivityTracker] SaveMapSessionToDatabase error: {ex.Message}");
        }
    }

    private void TryOpenPlayerSession(ulong steamId, DateTime connectTime)
    {
        try
        {
            var cs = GetConnectionString();
            if (string.IsNullOrEmpty(cs)) { Log("[A2ActivityTracker] TryOpenPlayerSession: empty connection string"); return; }
            using var conn = new MySqlConnection(cs);
            conn.Open();
            using var cmd = new MySqlCommand(@"INSERT INTO a2_player_sessions
                    (steam_id, connect_time, session_count)
                    VALUES (@sid, @now, 1)
                    ON DUPLICATE KEY UPDATE
                        connect_time = @now,
                        disconnect_time = NULL,
                        session_count = session_count + 1", conn);
            cmd.Parameters.AddWithValue("@sid", steamId);
            cmd.Parameters.AddWithValue("@now", connectTime);
            var rows = cmd.ExecuteNonQuery();
            Log($"[A2ActivityTracker] PlayerSession OPEN persisted - rows: {rows}, steam_id={steamId}");
        }
        catch (Exception ex)
        {
            Log($"[A2ActivityTracker] TryOpenPlayerSession error: {ex.Message}");
        }
    }

    private void TryClosePlayerSession(ulong steamId, DateTime disconnectTime)
    {
        try
        {
            var cs = GetConnectionString();
            if (string.IsNullOrEmpty(cs)) { Log("[A2ActivityTracker] TryClosePlayerSession: empty connection string"); return; }
            using var conn = new MySqlConnection(cs);
            conn.Open();
            using var cmd = new MySqlCommand(@"UPDATE a2_player_sessions
                    SET disconnect_time = @now,
                        duration_seconds = CASE 
                            WHEN connect_time IS NOT NULL THEN 
                                duration_seconds + TIMESTAMPDIFF(SECOND, connect_time, @now) 
                            ELSE duration_seconds
                        END
                    WHERE steam_id = @sid", conn);
            cmd.Parameters.AddWithValue("@sid", steamId);
            cmd.Parameters.AddWithValue("@now", disconnectTime);
            var rows = cmd.ExecuteNonQuery();
            Log($"[A2ActivityTracker] PlayerSession CLOSE persisted - rows: {rows}, steam_id={steamId}");
        }
        catch (Exception ex)
        {
            Log($"[A2ActivityTracker] TryClosePlayerSession error: {ex.Message}");
        }
    }
}
