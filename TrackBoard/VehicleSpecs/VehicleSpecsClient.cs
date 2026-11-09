using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TrackBoard.Common;
using TrackBoard.Dtos;

namespace TrackBoard.VehicleSpecs;

/// <summary>
/// The vehicle catalogue the app pre-fills its car form from: API Ninjas, proxied so its key
/// never ships inside the app. Optional, like photo storage: without a key the API runs and
/// these endpoints answer 503, and the app falls back to manual entry.
/// </summary>
public class VehicleSpecsOptions
{
    public const string SectionName = "VehicleSpecs";

    /// <summary>The API Ninjas key. Server-side only.</summary>
    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://api.api-ninjas.com";

    /// <summary>
    /// Whether the key's plan includes <c>/v2/cars</c> (Developer tier and up). Off, car details
    /// come from <c>/v2/carfacets</c>, which the free tier allows but which only knows fuel,
    /// gearbox, drive and cylinder count. Motorcycles are complete either way.
    /// </summary>
    public bool CarDetails { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

public interface IVehicleSpecsClient
{
    Task<IReadOnlyList<VehicleCatalogOption>> GetCarMakesAsync(CancellationToken ct);

    Task<IReadOnlyList<VehicleCatalogOption>> GetCarModelsAsync(string make, CancellationToken ct);

    Task<IReadOnlyList<int>> GetCarYearsAsync(string make, string model, CancellationToken ct);

    Task<IReadOnlyList<VehicleSpecVariant>> GetCarVariantsAsync(
        string make, string model, int year, CancellationToken ct);

    Task<IReadOnlyList<VehicleSpecVariant>> SearchMotorcyclesAsync(
        string make, string model, int? year, CancellationToken ct);
}

/// <summary>
/// API Ninjas over plain HTTP. Nothing is cached: the free tier's terms forbid storing its data,
/// and a driver adds a car a handful of times a year.
/// </summary>
public sealed partial class ApiNinjasVehicleSpecsClient(
    HttpClient http,
    IOptions<VehicleSpecsOptions> options,
    ILogger<ApiNinjasVehicleSpecsClient> logger) : IVehicleSpecsClient
{
    private readonly VehicleSpecsOptions _options = options.Value;

    /// <summary>Fuel splits asked about one by one on the free tier; more would burn the quota.</summary>
    private const int MaxFacetVariants = 4;

    public async Task<IReadOnlyList<VehicleCatalogOption>> GetCarMakesAsync(CancellationToken ct)
    {
        // /v2/carmakes is the proper list but its docs disagree on whether the free tier gets
        // it, so a refusal falls back to the make facet, which the free tier certainly does.
        var makes = new List<VehicleCatalogOption>();

        for (var offset = 0; offset < 400; offset += 100)
        {
            using var page = await TryGetAsync($"/v2/carmakes?limit=100&offset={offset}", ct);

            if (page is null)
            {
                return await FacetOptionsAsync("make", [], ct);
            }

            var batch = page.RootElement.EnumerateArray()
                .Select(m => new VehicleCatalogOption(Str(m, "name")!, Str(m, "slug") ?? Str(m, "name")!))
                .Where(m => !string.IsNullOrWhiteSpace(m.Name))
                .ToList();
            makes.AddRange(batch);

            if (batch.Count < 100)
            {
                break;
            }
        }

        return [.. makes.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<IReadOnlyList<VehicleCatalogOption>> GetCarModelsAsync(
        string make, CancellationToken ct)
    {
        using var doc = await TryGetAsync($"/v2/carmodels?make={Q(make)}&limit=100", ct);

        if (doc is null)
        {
            return await FacetOptionsAsync("model", [("make", make)], ct);
        }

        return
        [
            .. doc.RootElement.EnumerateArray()
                .Select(m => new VehicleCatalogOption(Str(m, "name")!, Str(m, "slug") ?? Str(m, "name")!))
                .Where(m => !string.IsNullOrWhiteSpace(m.Name))
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    public async Task<IReadOnlyList<int>> GetCarYearsAsync(
        string make, string model, CancellationToken ct)
    {
        var facets = await FacetsAsync("year", [("make", make), ("model", model)], ct);

        return
        [
            .. Values(facets, "year")
                .Select(v => int.TryParse(v, CultureInfo.InvariantCulture, out var y) ? y : 0)
                .Where(y => y > 1900)
                .Distinct()
                .OrderDescending(),
        ];
    }

    public async Task<IReadOnlyList<VehicleSpecVariant>> GetCarVariantsAsync(
        string make, string model, int year, CancellationToken ct)
    {
        if (_options.CarDetails)
        {
            using var doc = await GetAsync(
                $"/v2/cars?make={Q(make)}&model={Q(model)}&year={year}&limit=50", ct);

            return [.. doc.RootElement.EnumerateArray().Select(ApiNinjasParsing.Car)];
        }

        // Free tier: one facet call for the fuels, then one per fuel for what that fuel narrows
        // down to. A field is only filled when every matching car agrees on it.
        IReadOnlyList<(string, string)> scope = [("make", make), ("model", model), ("year", Year(year))];
        var fuels = Values(await FacetsAsync("fuel", scope, ct), "fuel").Take(MaxFacetVariants).ToList();
        var variants = new List<VehicleSpecVariant>();

        foreach (var fuel in fuels)
        {
            var facets = await FacetsAsync(
                "generation,transmission,drive,cylinders", [.. scope, ("fuel", fuel)], ct);
            variants.Add(ApiNinjasParsing.FromFacets(facets, make, model, year, fuel));
        }

        return variants;
    }

    public async Task<IReadOnlyList<VehicleSpecVariant>> SearchMotorcyclesAsync(
        string make, string model, int? year, CancellationToken ct)
    {
        var path = $"/v1/motorcycles?make={Q(make)}&model={Q(model)}";

        if (year is { } y)
        {
            path += $"&year={y}";
        }

        using var doc = await GetAsync(path, ct);

        return
        [
            .. doc.RootElement.EnumerateArray()
                .Select(ApiNinjasParsing.Motorcycle)
                .OrderBy(v => v.Model, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(v => v.YearFrom),
        ];
    }

    private async Task<IReadOnlyList<VehicleCatalogOption>> FacetOptionsAsync(
        string facet, IReadOnlyList<(string, string)> filters, CancellationToken ct) =>
    [
        .. Values(await FacetsAsync(facet, filters, ct), facet)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(v => new VehicleCatalogOption(v, v)),
    ];

    private async Task<JsonDocument> FacetsAsync(
        string facets, IReadOnlyList<(string Key, string Value)> filters, CancellationToken ct)
    {
        var query = string.Concat(filters.Select(f => $"&{f.Key}={Q(f.Value)}"));
        return await GetAsync($"/v2/carfacets?facets={Q(facets)}{query}", ct);
    }

    private static IEnumerable<string> Values(JsonDocument facets, string facet) =>
        facets.RootElement.TryGetProperty(facet, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
                .Select(e => e.TryGetProperty("value", out var v) ? ApiNinjasParsing.Text(v) : null)
                .OfType<string>()
                .Where(v => v.Length > 0)
            : [];

    /// <summary>Like <see cref="GetAsync"/>, but a 4xx (a plan restriction) answers null.</summary>
    private async Task<JsonDocument?> TryGetAsync(string path, CancellationToken ct)
    {
        try
        {
            return await GetAsync(path, ct);
        }
        catch (VehicleSpecsRefusedException)
        {
            return null;
        }
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        if (!_options.IsConfigured)
        {
            throw new ServiceUnavailableException("Vehicle lookup is not configured on this server.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _options.BaseUrl.TrimEnd('/') + path);
        request.Headers.Add("X-Api-Key", _options.ApiKey);

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(logger, ex);
            throw new ServiceUnavailableException("The vehicle catalogue could not be reached.");
        }

        using (response)
        {
            var status = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                LogRefused(logger, status, PathOnly(path));
                throw new ServiceUnavailableException("The vehicle catalogue's quota is used up for now.");
            }

            if (status is >= 400 and < 500)
            {
                LogRefused(logger, status, PathOnly(path));
                throw new VehicleSpecsRefusedException();
            }

            if (!response.IsSuccessStatusCode)
            {
                LogRefused(logger, status, PathOnly(path));
                throw new ServiceUnavailableException("The vehicle catalogue is not answering.");
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(body, cancellationToken: ct);
        }
    }

    private static string Q(string value) => Uri.EscapeDataString(value.Trim());

    private static string Year(int year) => year.ToString(CultureInfo.InvariantCulture);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? ApiNinjasParsing.Text(v) : null;

    private static string PathOnly(string path) => path.Split('?')[0];

    [LoggerMessage(Level = LogLevel.Warning, Message = "The vehicle catalogue answered {Status} on {Path}")]
    private static partial void LogRefused(ILogger logger, int status, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The vehicle catalogue could not be reached")]
    private static partial void LogUnreachable(ILogger logger, Exception exception);
}

/// <summary>A 4xx from the catalogue: a bad parameter or an endpoint outside the key's plan.</summary>
public sealed class VehicleSpecsRefusedException()
    : ServiceUnavailableException("This lookup is not available on the server's catalogue plan.");

/// <summary>
/// Turns API Ninjas' shapes into <see cref="VehicleSpecVariant"/>. Car sheets carry numbers and a
/// free-text <c>specs</c> map; motorcycle sheets carry everything as unit-laden strings
/// ("52.3 HP (38.2 kW)) @ 8000 RPM"), so those are read with patterns.
/// </summary>
public static partial class ApiNinjasParsing
{
    public static VehicleSpecVariant Car(JsonElement car)
    {
        var specs = car.TryGetProperty("specs", out var s) && s.ValueKind == JsonValueKind.Object
            ? s.EnumerateObject().ToDictionary(
                p => p.Name,
                p => p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("value", out var v)
                    ? v
                    : p.Value,
                StringComparer.OrdinalIgnoreCase)
            : [];

        JsonElement? Spec(params string[] names)
        {
            foreach (var name in names)
            {
                if (specs.TryGetValue(name, out var exact))
                {
                    return exact;
                }
            }

            // The labels are free text, so tolerate small differences in wording.
            var loose = specs.FirstOrDefault(kv =>
                names.Any(n => kv.Key.Contains(n, StringComparison.OrdinalIgnoreCase)));
            return loose.Key is null ? null : loose.Value;
        }

        var displacementL = Num(car, "displacement_l");
        var cc = Number(Spec("Capacity")) ?? displacementL * 1000;

        return new VehicleSpecVariant(
            Name: Text(car, "name") ?? Text(car, "badge") ?? Text(car, "model") ?? string.Empty,
            Make: Text(car, "make") ?? string.Empty,
            Model: Text(car, "model") ?? string.Empty,
            Generation: Text(car, "generation"),
            YearFrom: Int(car, "year_begin"),
            YearTo: Int(car, "year_end"),
            Engine: Text(car, "engine"),
            Cylinders: Int(car, "cylinders"),
            DisplacementCc: cc is { } c ? (int)Math.Round(c) : null,
            PowerHp: Int(car, "power_hp"),
            TorqueNm: Number(Spec("Maximum torque", "Torque")) is { } t ? (int)Math.Round(t) : null,
            WeightKg: Number(Spec("Curb weight", "Kerb weight", "Weight")),
            TopSpeedKmh: Number(Spec("Max speed", "Maximum speed", "Top speed")),
            ZeroToHundredSeconds: Number(Spec("Acceleration (0-100 km/h)", "Acceleration 0 - 100", "0-100")),
            FuelCapacityLitres: Number(Spec("Fuel tank capacity", "Fuel tank", "Tank capacity")),
            Drivetrain: Drivetrain(Text(car, "drive")),
            Fuel: Fuel(Text(car, "fuel")),
            Transmission: Transmission(Text(car, "transmission")),
            FrontSuspension: Text(Spec("Front suspension")),
            RearSuspension: Text(Spec("Rear suspension", "Back suspension")),
            Partial: false);
    }

    /// <summary>A free-tier sheet: only what every car matching make, model, year and fuel agrees on.</summary>
    public static VehicleSpecVariant FromFacets(
        JsonDocument facets, string make, string model, int year, string fuel)
    {
        string? Only(string facet)
        {
            if (!facets.RootElement.TryGetProperty(facet, out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var values = list.EnumerateArray()
                .Select(e => e.TryGetProperty("value", out var v) ? Text(v) : null)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return values.Count == 1 ? values[0] : null;
        }

        var generation = Only("generation");

        return new VehicleSpecVariant(
            Name: generation ?? model,
            Make: make,
            Model: model,
            Generation: generation,
            YearFrom: year,
            YearTo: year,
            Engine: null,
            Cylinders: int.TryParse(Only("cylinders"), CultureInfo.InvariantCulture, out var cyl) ? cyl : null,
            DisplacementCc: null,
            PowerHp: null,
            TorqueNm: null,
            WeightKg: null,
            TopSpeedKmh: null,
            ZeroToHundredSeconds: null,
            FuelCapacityLitres: null,
            Drivetrain: Drivetrain(Only("drive")),
            Fuel: Fuel(fuel),
            Transmission: Transmission(Only("transmission")),
            FrontSuspension: null,
            RearSuspension: null,
            Partial: true);
    }

    public static VehicleSpecVariant Motorcycle(JsonElement bike)
    {
        var model = Text(bike, "model") ?? string.Empty;
        var year = int.TryParse(Text(bike, "year"), CultureInfo.InvariantCulture, out var y) ? y : (int?)null;
        var engine = Text(bike, "engine");

        return new VehicleSpecVariant(
            Name: model,
            Make: Text(bike, "make") ?? string.Empty,
            Model: model,
            Generation: null,
            YearFrom: year,
            YearTo: year,
            Engine: engine,
            Cylinders: BikeCylinders(engine),
            DisplacementCc: Match(Text(bike, "displacement"), CcPattern()) is { } cc ? (int)Math.Round(cc) : null,
            PowerHp: Match(Text(bike, "power"), HpPattern()) is { } hp ? (int)Math.Round(hp) : null,
            TorqueNm: Match(Text(bike, "torque"), NmPattern()) is { } nm ? (int)Math.Round(nm) : null,
            WeightKg: Match(Text(bike, "total_weight"), KgPattern())
                ?? Match(Text(bike, "dry_weight"), KgPattern()),
            TopSpeedKmh: Match(Text(bike, "top_speed"), KmhPattern()),
            ZeroToHundredSeconds: null,
            FuelCapacityLitres: Match(Text(bike, "fuel_capacity"), LitrePattern()),
            // The rear wheel drives, whether by chain, belt or shaft.
            Drivetrain: "RWD",
            Fuel: Text(bike, "engine") is { } e && e.Contains("electric", StringComparison.OrdinalIgnoreCase)
                ? "Electric"
                : "Petrol",
            Transmission: BikeTransmission(Text(bike, "gearbox")),
            FrontSuspension: Text(bike, "front_suspension"),
            RearSuspension: Text(bike, "rear_suspension"),
            Partial: false);
    }

    public static string? Drivetrain(string? drive) => drive?.Trim().ToLowerInvariant() switch
    {
        "fwd" or "front" or "front wheel drive" => "FWD",
        "rwd" or "rear" or "rear wheel drive" => "RWD",
        "awd" or "all wheel drive" => "AWD",
        "4wd" or "four wheel drive" => "4WD",
        _ => null,
    };

    public static string? Fuel(string? fuel) => fuel?.Trim().ToLowerInvariant() switch
    {
        "gasoline" or "petrol" => "Petrol",
        "diesel" => "Diesel",
        "electric" => "Electric",
        "hybrid" or "plugin_hybrid" => "Hybrid",
        "ethanol" => "Ethanol",
        "hydrogen" => "Hydrogen",
        "lpg" => "LPG",
        "cng" => "CNG",
        _ => null,
    };

    public static string? Transmission(string? transmission) => transmission?.Trim().ToLowerInvariant() switch
    {
        "manual" => "Manual",
        "automatic" => "Automatic",
        "cvt" => "CVT",
        "dual_clutch" => "Dual-Clutch",
        "automated_manual" => "Sequential",
        "single_speed" => "Direct-Drive",
        _ => null,
    };

    /// <summary>Bikes shift sequentially unless the sheet says otherwise.</summary>
    public static string? BikeTransmission(string? gearbox)
    {
        if (string.IsNullOrWhiteSpace(gearbox))
        {
            return null;
        }

        var g = gearbox.ToLowerInvariant();

        return g switch
        {
            _ when g.Contains("dct", StringComparison.Ordinal) || g.Contains("dual clutch", StringComparison.Ordinal) => "Dual-Clutch",
            _ when g.Contains("cvt", StringComparison.Ordinal) || g.Contains("variomatic", StringComparison.Ordinal) => "CVT",
            _ when g.Contains("automatic", StringComparison.Ordinal) => "Automatic",
            _ when g.Contains("direct", StringComparison.Ordinal) || g.Contains("single", StringComparison.Ordinal) => "Direct-Drive",
            _ => "Sequential",
        };
    }

    /// <summary>"In-line four, four-stroke" -> 4. The first count word wins, before the stroke.</summary>
    public static int? BikeCylinders(string? engine)
    {
        if (string.IsNullOrWhiteSpace(engine))
        {
            return null;
        }

        var layout = engine.Split(',')[0].ToLowerInvariant();

        return layout switch
        {
            _ when layout.Contains("single", StringComparison.Ordinal) => 1,
            _ when layout.Contains("twin", StringComparison.Ordinal) => 2,
            _ when layout.Contains("three", StringComparison.Ordinal) || layout.Contains("triple", StringComparison.Ordinal) => 3,
            _ when layout.Contains("four", StringComparison.Ordinal) || layout.Contains("v4", StringComparison.Ordinal) => 4,
            _ when layout.Contains("six", StringComparison.Ordinal) => 6,
            _ => null,
        };
    }

    public static string? Text(JsonElement? e) => e is not { } v
        ? null
        : v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()?.Trim() is { Length: > 0 } s ? s : null,
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? Text(v) : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? Number(v) : null;

    private static int? Int(JsonElement e, string name) =>
        Num(e, name) is { } d ? (int)Math.Round(d) : null;

    private static double? Number(JsonElement? e) => e is not { } v
        ? null
        : v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => Match(v.GetString(), LeadingNumberPattern()),
            _ => null,
        };

    private static double? Match(string? text, Regex pattern) =>
        text is not null && pattern.Match(text) is { Success: true } m
        && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;

    [GeneratedRegex(@"([\d.]+)\s*HP", RegexOptions.IgnoreCase)]
    private static partial Regex HpPattern();

    [GeneratedRegex(@"([\d.]+)\s*Nm", RegexOptions.IgnoreCase)]
    private static partial Regex NmPattern();

    [GeneratedRegex(@"([\d.]+)\s*kg", RegexOptions.IgnoreCase)]
    private static partial Regex KgPattern();

    [GeneratedRegex(@"([\d.]+)\s*(?:litres|liters|l\b)", RegexOptions.IgnoreCase)]
    private static partial Regex LitrePattern();

    [GeneratedRegex(@"([\d.]+)\s*ccm", RegexOptions.IgnoreCase)]
    private static partial Regex CcPattern();

    [GeneratedRegex(@"([\d.]+)\s*km/h", RegexOptions.IgnoreCase)]
    private static partial Regex KmhPattern();

    [GeneratedRegex(@"^\s*([\d.]+)")]
    private static partial Regex LeadingNumberPattern();
}
