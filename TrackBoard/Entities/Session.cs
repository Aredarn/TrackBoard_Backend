namespace TrackBoard.Entities;

/// <summary>Mirrors TrackPro's <c>GpsProviderType</c> at the time the session was recorded.</summary>
public enum GpsSource
{
    Wifi,
    Bluetooth,
    PhoneGps,
}

public enum SessionVisibility
{
    Private,
    Ranked,
}

/// <summary>
/// A recorded session uploaded from TrackPro. The phone is the source of truth: the server
/// holds a mirror, replaced wholesale on every upload.
/// </summary>
public class Session : BaseEntity, IOwnedResource
{
    public Guid OwnerId { get; set; }

    public User Owner { get; set; } = null!;

    /// <summary>The app's <c>eventType</c>, e.g. "Hungaroring - 2026-09-28".</summary>
    public string Name { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public Guid? VehicleId { get; set; }

    public Vehicle? Vehicle { get; set; }

    public Guid? TrackId { get; set; }

    public Track? Track { get; set; }

    public GpsSource GpsSource { get; set; }

    public SessionVisibility Visibility { get; set; } = SessionVisibility.Private;

    public bool Voided { get; set; }

    /// <summary>
    /// The app version that timed the session. Gates are derived on the device, so this is
    /// how a change in that derivation can be traced after the fact.
    /// </summary>
    public string? AppVersion { get; set; }

    public double? WeatherTempC { get; set; }

    public int? WeatherHumidityPct { get; set; }

    public double? WeatherPrecipitationMm { get; set; }

    /// <summary>WMO weather code.</summary>
    public int? WeatherCode { get; set; }

    public double? WeatherWindKph { get; set; }

    public int? WeatherWindDirDeg { get; set; }

    public double? WeatherPressureHpa { get; set; }

    public ICollection<Lap> Laps { get; set; } = [];

    /// <summary>Set on a drag session, which has no track and no laps.</summary>
    public ICollection<DragResult> DragResults { get; set; } = [];
}

/// <summary>A completed lap, or a completed run on a sprint track.</summary>
public class Lap
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid SessionId { get; set; }

    public Session Session { get; set; } = null!;

    public int LapNumber { get; set; }

    /// <summary>Milliseconds.</summary>
    public int TimeMs { get; set; }

    /// <summary>GPS dropped out during the lap. Such laps never rank.</summary>
    public bool SignalGap { get; set; }

    /// <summary>
    /// The highest GPS speed of the lap, km/h, as the app measured it. Null for laps uploaded
    /// before the app sent it, or recorded without speed in the fixes.
    /// </summary>
    public double? TopSpeedKmh { get; set; }

    public ICollection<LapSector> Sectors { get; set; } = [];
}

/// <summary>One sector split of a lap: the time for that sector alone, not cumulative.</summary>
public class LapSector
{
    public Guid LapId { get; set; }

    public int SectorIndex { get; set; }

    /// <summary>Milliseconds.</summary>
    public int SplitMs { get; set; }
}
