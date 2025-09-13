namespace A2ActivityTracker.Models;

public class ServerStats
{
    public DateTime DateUtc { get; set; }
    public int MaxPlayers { get; set; }
    public decimal AvgPlayers { get; set; }
    public int RecordsCount { get; set; }
}
