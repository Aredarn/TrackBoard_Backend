namespace TrackBoard.Entities;

/// <summary>
/// The figures a drag board ranks. Fixed in metric whatever units the driver reads in the app,
/// so everyone on a board ran the same thing.
/// </summary>
public enum DragMetric
{
    ZeroToHundred,
    HundredToTwoHundred,
    QuarterMile,
}

/// <summary>Power-to-weight bands, slowest first. See <c>DragRules.ClassOf</c>.</summary>
public enum DragClass
{
    D,
    C,
    B,
    A,
    S,
}

/// <summary>A drag session's best time for one metric, as timed on the device.</summary>
public class DragResult
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid SessionId { get; set; }

    public Session Session { get; set; } = null!;

    public DragMetric Metric { get; set; }

    /// <summary>Milliseconds.</summary>
    public int TimeMs { get; set; }

    /// <summary>km/h at the line. Quarter mile only.</summary>
    public double? TrapSpeedKmh { get; set; }
}
