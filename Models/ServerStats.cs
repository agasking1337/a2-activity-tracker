namespace SlotTracker.Models;

public class ServerStats
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public int CurrentPlayers { get; set; }
    public int MaxSlots { get; set; }
}
