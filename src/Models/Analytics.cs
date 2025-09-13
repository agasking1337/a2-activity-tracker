using MySqlConnector;
using System;
using System.Collections.Generic;
using A2ActivityTracker.Models;

namespace A2ActivityTracker
{
    public static class Analytics
    {
        // Ensure any analytics-related schema (extra columns/tables) exists
        public static void EnsureAnalyticsSchema(MySqlConnection conn, Action<string>? log = null)
        {
            // Ensure extra columns on a2_server_stats
            foreach (var (column, sql) in DbSql.EnsureDailyStatsExtraColumns)
            {
                try
                {
                    using var cmd = new MySqlCommand(sql, conn);
                    cmd.ExecuteNonQuery();
                }
                catch (MySqlException ex)
                {
                    // 1060: Duplicate column name
                    if (ex.Number != 1060)
                    {
                        log?.Invoke($"[Analytics] Ensure column '{column}' warning: {ex.Message}");
                    }
                }
            }

            // No separate per-map daily table required.
        }

        // Generate analytics for a specific UTC date (00:00..24:00 of that date)
        public static void GenerateDailyAnalytics(MySqlConnection conn, DateTime dateUtc, Action<string>? log = null)
        {
            var dayStart = new DateTime(dateUtc.Year, dateUtc.Month, dateUtc.Day, 0, 0, 0, DateTimeKind.Utc);
            var dayEnd = dayStart.AddDays(1);

            // 1) Aggregate from a2_server_stats (raw samples: max/avg/count)
            int maxPlayers = 0;
            decimal avgPlayers = 0;
            int recordsCount = 0;
            using (var cmd = new MySqlCommand(@"SELECT IFNULL(MAX(player_count),0), IFNULL(ROUND(AVG(player_count),2),0), COUNT(*)
                                               FROM a2_server_stats
                                               WHERE timestamp >= @from AND timestamp < @to", conn))
            {
                cmd.Parameters.AddWithValue("@from", dayStart);
                cmd.Parameters.AddWithValue("@to", dayEnd);
                using var rdr = cmd.ExecuteReader();
                if (rdr.Read())
                {
                    maxPlayers = rdr.GetInt32(0);
                    avgPlayers = rdr.GetDecimal(1);
                    recordsCount = rdr.GetInt32(2);
                }
            }

            // 2) Aggregate from a2_player_sessions (clamped to day window)
            // Total player session time within day and average session duration (over sessions)
            long totalPlayerSessionSeconds = 0;
            int avgSessionSeconds = 0;
            using (var cmd = new MySqlCommand(@"SELECT 
                        IFNULL(SUM(TIMESTAMPDIFF(SECOND, 
                            GREATEST(connect_time, @from), 
                            LEAST(IFNULL(disconnect_time, @to), @to)
                        )), 0) AS total_secs,
                        IFNULL(AVG(TIMESTAMPDIFF(SECOND, 
                            GREATEST(connect_time, @from), 
                            LEAST(IFNULL(disconnect_time, @to), @to)
                        )), 0) AS avg_secs
                    FROM a2_player_sessions
                    WHERE connect_time < @to AND IFNULL(disconnect_time, @from) >= @from", conn))
            {
                cmd.Parameters.AddWithValue("@from", dayStart);
                cmd.Parameters.AddWithValue("@to", dayEnd);
                using var rdr = cmd.ExecuteReader();
                if (rdr.Read())
                {
                    totalPlayerSessionSeconds = rdr.IsDBNull(0) ? 0 : rdr.GetInt64(0);
                    avgSessionSeconds = rdr.IsDBNull(1) ? 0 : Convert.ToInt32(Math.Round(rdr.GetDouble(1)));
                }
            }

            // 3) Per-map stats from a2_map_sessions, clamped to day window
            // total_playtime: sum of clamped total_playtime if stored; otherwise compute using session length * avg players
            // We'll compute based on time span overlap using TIMESTAMPDIFF and assume TotalPlaytime already represents accumulated player-seconds.
            var perMap = new List<(string map, long totalPlaytime, int avgLenSec, int sessions)>();
            using (var cmd = new MySqlCommand(@"SELECT 
                        map_name,
                        IFNULL(SUM(
                            -- Prefer stored total_playtime if the whole session is within the day window
                            CASE 
                                WHEN map_start >= @from AND IFNULL(map_end, @to) <= @to THEN total_playtime
                                ELSE TIMESTAMPDIFF(SECOND, GREATEST(map_start, @from), LEAST(IFNULL(map_end, @to), @to))
                            END
                        ), 0) AS total_playtime,
                        IFNULL(AVG(TIMESTAMPDIFF(SECOND, GREATEST(map_start, @from), LEAST(IFNULL(map_end, @to), @to))), 0) AS avg_len_sec,
                        COUNT(*) AS sessions_count
                    FROM a2_map_sessions
                    WHERE map_start < @to AND IFNULL(map_end, @from) >= @from
                    GROUP BY map_name", conn))
            {
                cmd.Parameters.AddWithValue("@from", dayStart);
                cmd.Parameters.AddWithValue("@to", dayEnd);
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    var map = rdr.GetString(0);
                    var totalPlaytime = rdr.IsDBNull(1) ? 0L : rdr.GetInt64(1);
                    var avgLen = rdr.IsDBNull(2) ? 0 : Convert.ToInt32(Math.Round(rdr.GetDouble(2)));
                    var sessions = rdr.GetInt32(3);
                    perMap.Add((map, totalPlaytime, avgLen, sessions));
                }
            }

            // Find best map by total playtime
            string? bestMap = null;
            long bestMapTotal = 0;
            int bestMapAvgLen = 0;
            foreach (var m in perMap)
            {
                if (m.totalPlaytime > bestMapTotal)
                {
                    bestMap = m.map;
                    bestMapTotal = m.totalPlaytime;
                    bestMapAvgLen = m.avgLenSec;
                }
            }

            // 4) Upsert a2_server_stats with extended fields
            using (var upsert = new MySqlCommand(@"INSERT INTO a2_server_analytics
                        (date_utc, max_players, avg_players, records_count, total_player_session_time, avg_session_seconds, best_map, best_map_total_playtime, best_map_avg_length_seconds)
                        VALUES (@date, @maxp, @avgp, @rc, @tps, @avgss, @bmap, @bmaptot, @bmapavg)
                        ON DUPLICATE KEY UPDATE
                            max_players = VALUES(max_players),
                            avg_players = VALUES(avg_players),
                            records_count = VALUES(records_count),
                            total_player_session_time = VALUES(total_player_session_time),
                            avg_session_seconds = VALUES(avg_session_seconds),
                            best_map = VALUES(best_map),
                            best_map_total_playtime = VALUES(best_map_total_playtime),
                            best_map_avg_length_seconds = VALUES(best_map_avg_length_seconds)", conn))
            {
                upsert.Parameters.AddWithValue("@date", dayStart.Date);
                upsert.Parameters.AddWithValue("@maxp", maxPlayers);
                upsert.Parameters.AddWithValue("@avgp", avgPlayers);
                upsert.Parameters.AddWithValue("@rc", recordsCount);
                upsert.Parameters.AddWithValue("@tps", totalPlayerSessionSeconds);
                upsert.Parameters.AddWithValue("@avgss", avgSessionSeconds);
                upsert.Parameters.AddWithValue("@bmap", (object?)bestMap ?? DBNull.Value);
                upsert.Parameters.AddWithValue("@bmaptot", bestMapTotal);
                upsert.Parameters.AddWithValue("@bmapavg", bestMapAvgLen);
                var affected = upsert.ExecuteNonQuery();
                log?.Invoke($"[Analytics] Upsert server_stats affected: {affected}");
            }

            // No per-map daily upserts; best-map values are stored in a2_server_stats only.
        }
    }
}
