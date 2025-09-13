# CS2 Slots Tracker (A2ActivityTracker)

Simple CS2 plugin that logs live player count vs server slots to MySQL and auto-creates daily stats.

- Module: `A2ActivityTracker`
- Framework: .NET 8
- DB: MySQL (MySqlConnector)
- Plugin entry: `A2ActivityTracker.SlotTracker`

## Features
- Live tracking of non-bot, non-HLTV player count and server slot capacity.
- Writes timestamped samples to MySQL (`a2_server_analytics`).
- Automatic daily rollup at 00:05 into `a2_server_stats` (max, avg, samples).
- Debounced writes on connect/disconnect to avoid spam.
- Optional debug command `css_serverstats` (enable `DebugMode`) to aggregate today on demand.

## Quick setup
1) Requirements
- CS2 server with CounterStrikeSharp.
- .NET 8 SDK (to build from source).
- MySQL accessible by the server.

2) Build (from `src/`)
```bash
dotnet restore
dotnet build -c Release
```
Output directory:
```
src/build/counterstrikesharp/plugins/a2-activity-tracker
```

3) Deploy
Copy the built plugin folder to:
```
<cs2>/game/csgo/addons/counterstrikesharp/plugins/a2-activity-tracker
```

## Configuration
On first run, a default `config.json` with placeholders is created. Fill in:
- `Host`, `Port` (3306), `Database`, `User`, `Password`
- `DebugMode` (true enables `css_serverstats`)

Preferred path for the config:
```
.../addons/counterstrikesharp/configs/plugins/a2-activity-tracker/config.json
```
If placeholders remain, DB init and writes are skipped (see `[SlotTracker]` logs).


## Usage
- Automatic:
  - Samples inserted on player connect/disconnect.
  - Daily aggregation at 00:05 (server time) for yesterday.
- Manual (debug):
  - Set `DebugMode = true`, then run:
    ```
    css_serverstats
    ```
  - Aggregates/updates today’s stats.

