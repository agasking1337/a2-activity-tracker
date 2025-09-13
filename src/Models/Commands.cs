using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using MySqlConnector;
using A2ActivityTracker.Models;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API;

namespace A2ActivityTracker;

public partial class SlotTracker
{
    private void CommandServerStats(CCSPlayerController? player, CommandInfo command)
    {
        // Only allow when DebugMode is enabled
        if (_config == null || !_config.DebugMode)
        {
            player?.PrintToChat(" \x02[SlotTracker]\x01 Command disabled. Enable DebugMode in config to use.");
            return;
        }

        try
        {
            var connectionString = GetConnectionString();
            if (string.IsNullOrEmpty(connectionString))
            {
                player?.PrintToChat(" \x02[SlotTracker]\x01 Invalid DB connection.");
                return;
            }

            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            // Generate analytics for today (UTC day window)
            var todayUtc = DateTime.UtcNow.Date;
            Analytics.GenerateDailyAnalytics(conn, todayUtc, Log);
            Log("[SlotTracker] css_serverstats executed. Daily analytics generated.");

            player?.PrintToChat(" \x04[SlotTracker]\x01 Daily analytics generated for today.");
        }
        catch (Exception ex)
        {
            Log($"[SlotTracker] css_serverstats error: {ex.Message}");
            player?.PrintToChat(" \x02[SlotTracker]\x01 Error running aggregation. Check server logs.");
        }
    }

    // Debug-only: force a snapshot write to a2_server_analytics
    private void CommandStatsNow(CCSPlayerController? player, CommandInfo command)
    {
        if (_config == null || !_config.DebugMode)
        {
            player?.PrintToChat(" \x02[SlotTracker]\x01 Command disabled. Enable DebugMode in config to use.");
            return;
        }

        var connectionString = GetConnectionString();
        if (string.IsNullOrEmpty(connectionString))
        {
            player?.PrintToChat(" \x02[SlotTracker]\x01 Invalid DB connection.");
            return;
        }

        try
        {
            var current = GetConnectedPlayersCount();
            InsertStats(current);
            player?.PrintToChat($" \x04[SlotTracker]\x01 Snapshot written: {current}/{_serverSlots} players.");
        }
        catch (Exception ex)
        {
            Log($"[SlotTracker] css_stats_now error: {ex.Message}");
            player?.PrintToChat(" \x02[SlotTracker]\x01 Error writing snapshot. Check server logs.");
        }
    }


    // Show current non-bot players and map info
    private void CommandWho(CCSPlayerController? player, CommandInfo command)
    {
        try
        {
            var map = string.Empty;
            try { map = Server.MapName ?? string.Empty; } catch { }
            var players = Utilities.GetPlayers();
            var list = players
                .Where(p => p != null && p.Connected == PlayerConnectedState.PlayerConnected && !p.IsBot && !p.IsHLTV)
                .Select(p => $"{p.PlayerName} ({p.SteamID})")
                .ToList();

            player?.PrintToChat($" \x04[SlotTracker]\x01 Map: {map}, Players: {list.Count}/{_serverSlots}");
            if (list.Count > 0)
            {
                foreach (var line in list)
                {
                    player?.PrintToChat("  \x10" + line);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[SlotTracker] css_who error: {ex.Message}");
            player?.PrintToChat(" \x02[SlotTracker]\x01 Error during css_who. Check logs.");
        }
    }
    
}
