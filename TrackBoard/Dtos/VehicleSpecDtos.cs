namespace TrackBoard.Dtos;

/// <summary>A make or model to pick from. <c>Key</c> is what to send back when narrowing further.</summary>
public record VehicleCatalogOption(string Name, string Key);

/// <summary>
/// One spec sheet the app can pre-fill its car form from. Every field is optional: the source
/// rarely knows everything, and whatever is missing the driver types in.
/// </summary>
/// <remarks>
/// The enumerations are the app's own vocabulary, so it can select them in its dropdowns as-is:
/// <c>Drivetrain</c> FWD, RWD, AWD, 4WD; <c>Fuel</c> Petrol, Diesel, Electric, Hybrid, Ethanol,
/// Hydrogen, LPG, CNG; <c>Transmission</c> Manual, Automatic, Sequential, Dual-Clutch, CVT,
/// Direct-Drive. <c>Partial</c> is true when only a subset of the sheet could be resolved, as on
/// the free tier where car details come from aggregate counts rather than a real spec sheet.
/// </remarks>
public record VehicleSpecVariant(
    string Name,
    string Make,
    string Model,
    string? Generation,
    int? YearFrom,
    int? YearTo,
    string? Engine,
    int? Cylinders,
    int? DisplacementCc,
    int? PowerHp,
    int? TorqueNm,
    double? WeightKg,
    double? TopSpeedKmh,
    double? ZeroToHundredSeconds,
    double? FuelCapacityLitres,
    string? Drivetrain,
    string? Fuel,
    string? Transmission,
    string? FrontSuspension,
    string? RearSuspension,
    bool Partial);
