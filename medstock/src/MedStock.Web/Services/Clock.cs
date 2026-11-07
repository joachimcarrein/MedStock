namespace MedStock.Web.Services;

/// <summary>Single source of "now" and "today" (local time of the container).</summary>
public interface IClock
{
    DateTime Now { get; }
    DateOnly Today => DateOnly.FromDateTime(Now);
}

public sealed class SystemClock : IClock
{
    public DateTime Now => DateTime.Now;
}

public sealed class FixedClock(DateTime now) : IClock
{
    public DateTime Now { get; set; } = now;
}
