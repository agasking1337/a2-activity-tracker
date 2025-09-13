namespace A2ActivityTracker.Models;

public class DatabaseConfig
{
    public string Host { get; init; } = "YOUR_DATABASE_HOST";
    public int Port { get; init; } = 3306;
    public string Database { get; init; } = "YOUR_DATABASE_NAME";
    public string User { get; init; } = "YOUR_DATABASE_USER";
    public string Password { get; init; } = "YOUR_DATABASE_PASSWORD";
    public bool DebugMode { get; init; } = false;
}

// Centralized SQL used by the plugin (schema + statements)
// Keeping this here simplifies usage across the codebase.
    public static class DbSql
    {
        // --- a2_server_stats (raw samples) ---
        public const string CreateAnalyticsTable = @"
            CREATE TABLE IF NOT EXISTS a2_server_stats (
                id BIGINT AUTO_INCREMENT PRIMARY KEY,
                timestamp DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                player_count INT NOT NULL,
                server_slots INT NOT NULL
            )";

        public const string CreateAnalyticsTimestampIndex =
            "CREATE INDEX idx_a2_ts ON a2_server_stats (timestamp)";

        // --- a2_server_analytics (daily rollups; date-based only) ---
        public const string CreateDailyStatsTable = @"
            CREATE TABLE IF NOT EXISTS a2_server_analytics (
                date_utc DATE NOT NULL PRIMARY KEY,
                max_players INT NOT NULL,
                avg_players DECIMAL(5,2) NOT NULL,
                records_count INT NOT NULL,
                total_player_session_time BIGINT NULL,
                avg_session_seconds INT NULL,
                best_map VARCHAR(64) NULL,
                best_map_total_playtime BIGINT NULL,
                best_map_avg_length_seconds INT NULL
            )";

        // Legacy cleanup (safe to attempt and ignore if not present)
        public const string DropLegacyDailyIndex =
            "DROP INDEX idx_a2_stats_year_month ON a2_server_analytics";

        public static readonly string[] DropLegacyDailyColumns =
            { "year", "month", "day" };

        // Daily aggregation (yesterday)
        public const string UpsertDailyStatsYesterday = @"
            INSERT INTO a2_server_analytics (
              date_utc, max_players, avg_players, records_count
            )
            SELECT
              DATE(NOW() - INTERVAL 1 DAY),
              IFNULL(MAX(player_count), 0),
              IFNULL(ROUND(AVG(player_count), 2), 0),
              COUNT(*)
            FROM a2_server_stats
            WHERE timestamp >= DATE(NOW() - INTERVAL 1 DAY)
              AND timestamp <  DATE(NOW())
            ON DUPLICATE KEY UPDATE
              max_players = VALUES(max_players),
              avg_players = VALUES(avg_players),
              records_count = VALUES(records_count);";

        // Daily aggregation (today) used by debug command
        public const string UpsertDailyStatsToday = @"
            INSERT INTO a2_server_analytics (
              date_utc, max_players, avg_players, records_count
            )
            SELECT
              DATE(NOW()),
              IFNULL(MAX(player_count), 0),
              IFNULL(ROUND(AVG(player_count), 2), 0),
              COUNT(*)
            FROM a2_server_stats
            WHERE timestamp >= DATE(NOW())
              AND timestamp <  DATE(NOW()) + INTERVAL 1 DAY
            ON DUPLICATE KEY UPDATE
              max_players = VALUES(max_players),
              avg_players = VALUES(avg_players),
              records_count = VALUES(records_count);";

        // --- a2_map_sessions (per-map session summary) ---
        public const string CreateMapSessionsTable = @"
            CREATE TABLE IF NOT EXISTS a2_map_sessions (
                id BIGINT AUTO_INCREMENT PRIMARY KEY,
                map_name VARCHAR(64) NOT NULL,
                map_start DATETIME NOT NULL,
                map_end DATETIME NULL,
                total_players_seen INT NOT NULL,
                total_playtime BIGINT NOT NULL,
                INDEX idx_map_start (map_start)
            )";

        // --- a2_player_sessions (per-player sessions) ---
        public const string CreatePlayerSessionsTable = @"
            CREATE TABLE IF NOT EXISTS a2_player_sessions (
                id BIGINT AUTO_INCREMENT PRIMARY KEY,
                steam_id BIGINT UNSIGNED NOT NULL UNIQUE,
                connect_time DATETIME NOT NULL,
                disconnect_time DATETIME NULL,
                duration_seconds BIGINT NOT NULL DEFAULT 0,
                session_count INT NOT NULL DEFAULT 0,
                INDEX idx_player (steam_id)
            )";

        // Helper ALTERs to add columns if missing (safe to try and ignore errors as needed)
        public static readonly (string Column, string Sql)[] EnsureDailyStatsExtraColumns = new[]
        {
            ("total_player_session_time", "ALTER TABLE a2_server_analytics ADD COLUMN total_player_session_time BIGINT NULL"),
            ("avg_session_seconds", "ALTER TABLE a2_server_analytics ADD COLUMN avg_session_seconds INT NULL"),
            ("best_map", "ALTER TABLE a2_server_analytics ADD COLUMN best_map VARCHAR(64) NULL"),
            ("best_map_total_playtime", "ALTER TABLE a2_server_analytics ADD COLUMN best_map_total_playtime BIGINT NULL"),
            ("best_map_avg_length_seconds", "ALTER TABLE a2_server_analytics ADD COLUMN best_map_avg_length_seconds INT NULL")
        };
        
    }
