# CS2 Slots Tracker Plugin

A Counter-Strike 2 plugin built with CounterStrikeSharp that tracks server player counts and stores the data in a MySQL database. This plugin helps server administrators monitor server population over time.

## Features

- Real-time tracking of player connections and disconnections
- Automatic server slot detection
- MySQL database integration for data storage
- Excludes bots and HLTV from player counts
- Handles player kicks and bans appropriately
- Detailed logging for troubleshooting

## Prerequisites

- Counter-Strike 2 Server
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) installed on your server
- MySQL Server (version 5.7 or higher recommended)
- .NET 7.0 Runtime

## Installation

1. Download the latest release from the releases page
2. Extract the contents to your CS2 server's plugin directory:
   ```
   addons/counterstrikesharp/plugins/cs2-slots-tracker/
   ```
3. Ensure the following files are present in your plugin directory:
   - `cs2-slots-tracker.dll`
   - `Dapper.dll`
   - `MySqlConnector.dll`
   - `config.json`

## Configuration

1. Edit the `config.json` file with your MySQL database credentials:
   ```json
   {
       "Host": "YOUR_DATABASE_HOST",
       "Port": 3306,
       "Database": "YOUR_DATABASE_NAME",
       "User": "YOUR_DATABASE_USERNAME",
       "Password": "YOUR_DATABASE_PASSWORD"
   }
   ```

2. Create the required database table using the following SQL:
   ```sql
   CREATE TABLE IF NOT EXISTS server_stats (
       id BIGINT AUTO_INCREMENT PRIMARY KEY,
       timestamp DATETIME,
       player_count INT,
       server_slots INT
   );
   ```

## Building from Source

1. Clone the repository
2. Make sure you have .NET 7.0 SDK installed
3. Run the following commands:
   ```bash
   dotnet build -c Release
   ```
   Or use the included `publish.bat` script.

## Troubleshooting

### Common Issues

1. **Database Connection Errors**
   - Verify your MySQL credentials in `config.json`
   - Ensure your MySQL server is accessible from your CS2 server
   - Check if the specified database exists

2. **Plugin Not Loading**
   - Verify all required DLLs are present in the plugin directory
   - Check the server console for error messages
   - Ensure CounterStrikeSharp is properly installed

3. **Missing Data**
   - Check the server console for any error messages
   - Verify the database table structure matches the required schema
   - Ensure the MySQL user has proper permissions

## Logs

The plugin logs important events to the server console with the `[SlotTracker]` prefix. Monitor these logs for:
- Plugin initialization
- Database connection status
- Player connect/disconnect events
- Any errors that occur

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Contributing

Contributions are welcome! Please feel free to submit pull requests.

## Support

If you encounter any issues or need help, please:
1. Check the troubleshooting section
2. Look through existing issues
3. Create a new issue with detailed information about your problem

## Credits

Built with:
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
- [Dapper](https://github.com/DapperLib/Dapper)
- [MySqlConnector](https://mysqlconnector.net/)
