using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TrackBoard.Common;
using TrackBoard.VehicleSpecs;

namespace TrackBoard.Tests.Unit;

/// <summary>
/// The catalogue client against canned API Ninjas answers. The bodies are the samples from the
/// API Ninjas docs, trimmed, so a change in their shape shows up here first.
/// </summary>
public class VehicleSpecsClientTests
{
    /// <summary>Answers by path, records every URL asked for.</summary>
    private sealed class FakeCatalogue(Func<Uri, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            request.Headers.GetValues("X-Api-Key").ShouldBe(["test-key"]);
            var (status, body) = answer(request.RequestUri!);

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static ApiNinjasVehicleSpecsClient Client(
        FakeCatalogue handler, bool carDetails = false, string? key = "test-key") =>
        new(
            new HttpClient(handler),
            Options.Create(new VehicleSpecsOptions { ApiKey = key, CarDetails = carDetails }),
            NullLogger<ApiNinjasVehicleSpecsClient>.Instance);

    private const string Bmw320d = """
        [{
          "id": "car_e71ff3416a", "make": "BMW", "model": "3 Series",
          "generation": "E90/E91/E92/E93", "chassis_codes": ["E90"],
          "year_begin": 2004, "year_end": 2010, "body": "Wagon", "name": "320d AT (163 hp)",
          "badge": "320d", "engine": "2.0 turbo diesel", "power_hp": 163, "power_kw": 120,
          "displacement_l": 2, "cylinders": 4, "transmission": "automatic", "gearbox": "AT",
          "gears": 6, "drive": "rwd", "fuel": "diesel", "spec_count": 45,
          "specs": {
            "Capacity": {"value": 1995, "unit": "cm3"},
            "Max speed": {"value": 220, "unit": "km/h"},
            "Maximum torque": {"value": 340, "unit": "N*m"},
            "Acceleration (0-100 km/h)": {"value": 8.3, "unit": "s"},
            "Curb weight": {"value": 1520, "unit": "kg"},
            "Fuel tank capacity": {"value": 61, "unit": "l"},
            "Front suspension": {"value": "McPherson strut"},
            "Engine type": {"value": "Diesel"}
          }
        }]
        """;

    private const string Fz8 = """
        [{
          "make": "Yamaha", "model": "FZ8 ", "year": "2012", "type": "Naked bike",
          "displacement": "779.0 ccm (47.54 cubic inches)",
          "engine": "In-line four, four-stroke",
          "power": "106.0 HP (77.4 kW)) @ 10000 RPM",
          "torque": "82.0 Nm (8.4 kgf-m or 60.5 ft.lbs) @ 8000 RPM",
          "gearbox": "6-speed", "transmission": "Chain   (final drive)",
          "front_suspension": "43 mm upside-down telescopic fork",
          "rear_suspension": "Link type monocross, adjustable preload",
          "total_weight": "211.0 kg (465.2 pounds)",
          "fuel_capacity": "17.00 litres (4.49 US gallons)"
        }]
        """;

    [Fact]
    public async Task A_full_car_sheet_maps_onto_the_app_vocabulary()
    {
        var handler = new FakeCatalogue(_ => (HttpStatusCode.OK, Bmw320d));

        var variant = (await Client(handler, carDetails: true)
            .GetCarVariantsAsync("BMW", "3-series", 2007, default)).ShouldHaveSingleItem();

        handler.Requests.ShouldHaveSingleItem().PathAndQuery
            .ShouldBe("/v2/cars?make=BMW&model=3-series&year=2007&limit=50");
        variant.Name.ShouldBe("320d AT (163 hp)");
        variant.Generation.ShouldBe("E90/E91/E92/E93");
        variant.PowerHp.ShouldBe(163);
        variant.TorqueNm.ShouldBe(340);
        variant.WeightKg.ShouldBe(1520);
        variant.TopSpeedKmh.ShouldBe(220);
        variant.ZeroToHundredSeconds.ShouldBe(8.3);
        variant.FuelCapacityLitres.ShouldBe(61);
        variant.DisplacementCc.ShouldBe(1995);
        variant.Cylinders.ShouldBe(4);
        variant.Drivetrain.ShouldBe("RWD");
        variant.Fuel.ShouldBe("Diesel");
        variant.Transmission.ShouldBe("Automatic");
        variant.FrontSuspension.ShouldBe("McPherson strut");
        variant.Partial.ShouldBeFalse();
    }

    [Fact]
    public async Task On_the_free_tier_a_car_is_split_by_fuel_and_keeps_only_agreed_facts()
    {
        var handler = new FakeCatalogue(uri => uri.Query switch
        {
            var q when q.Contains("facets=fuel", StringComparison.Ordinal) => (HttpStatusCode.OK,
                """{"fuel": [{"value": "gasoline", "count": 9}, {"value": "diesel", "count": 6}]}"""),
            var q when q.Contains("fuel=diesel", StringComparison.Ordinal) => (HttpStatusCode.OK,
                """
                {"generation": [{"value": "B6", "count": 6}],
                 "transmission": [{"value": "manual", "count": 4}, {"value": "automatic", "count": 2}],
                 "drive": [{"value": "fwd", "count": 6}],
                 "cylinders": [{"value": 4, "count": 6}]}
                """),
            _ => (HttpStatusCode.OK,
                """
                {"generation": [{"value": "B6", "count": 9}],
                 "transmission": [{"value": "manual", "count": 9}],
                 "drive": [{"value": "fwd", "count": 5}, {"value": "awd", "count": 4}],
                 "cylinders": [{"value": 4, "count": 7}, {"value": 6, "count": 2}]}
                """),
        });

        var variants = await Client(handler).GetCarVariantsAsync("Audi", "A4", 2004, default);

        handler.Requests.Count.ShouldBe(3);
        handler.Requests.ShouldAllBe(r => r.AbsolutePath == "/v2/carfacets");

        var petrol = variants.Single(v => v.Fuel == "Petrol");
        petrol.Partial.ShouldBeTrue();
        petrol.Generation.ShouldBe("B6");
        petrol.Transmission.ShouldBe("Manual");
        petrol.Drivetrain.ShouldBeNull();
        petrol.Cylinders.ShouldBeNull();

        var diesel = variants.Single(v => v.Fuel == "Diesel");
        diesel.Transmission.ShouldBeNull();
        diesel.Drivetrain.ShouldBe("FWD");
        diesel.Cylinders.ShouldBe(4);
    }

    [Fact]
    public async Task A_motorcycle_sheet_is_read_out_of_its_unit_strings()
    {
        var handler = new FakeCatalogue(_ => (HttpStatusCode.OK, Fz8));

        var bike = (await Client(handler).SearchMotorcyclesAsync("Yamaha", "FZ8", 2012, default))
            .ShouldHaveSingleItem();

        handler.Requests.ShouldHaveSingleItem().PathAndQuery
            .ShouldBe("/v1/motorcycles?make=Yamaha&model=FZ8&year=2012");
        bike.Model.ShouldBe("FZ8");
        bike.YearFrom.ShouldBe(2012);
        bike.PowerHp.ShouldBe(106);
        bike.TorqueNm.ShouldBe(82);
        bike.WeightKg.ShouldBe(211);
        bike.FuelCapacityLitres.ShouldBe(17);
        bike.DisplacementCc.ShouldBe(779);
        bike.Cylinders.ShouldBe(4);
        bike.Drivetrain.ShouldBe("RWD");
        bike.Fuel.ShouldBe("Petrol");
        bike.Transmission.ShouldBe("Sequential");
        bike.FrontSuspension.ShouldBe("43 mm upside-down telescopic fork");
    }

    [Fact]
    public async Task Makes_fall_back_to_the_make_facet_when_the_list_is_outside_the_plan()
    {
        var handler = new FakeCatalogue(uri => uri.AbsolutePath == "/v2/carmakes"
            ? (HttpStatusCode.BadRequest, """{"error": "This endpoint is for premium users only."}""")
            : (HttpStatusCode.OK,
                """{"make": [{"value": "Skoda", "count": 400}, {"value": "Audi", "count": 900}]}"""));

        var makes = await Client(handler).GetCarMakesAsync(default);

        makes.Select(m => m.Name).ShouldBe(["Audi", "Skoda"]);
        makes.Select(m => m.Key).ShouldBe(["Audi", "Skoda"]);
    }

    [Fact]
    public async Task Without_a_key_nothing_is_called_and_the_lookup_is_unavailable()
    {
        var handler = new FakeCatalogue(_ => throw new InvalidOperationException("must not be called"));

        await Should.ThrowAsync<ServiceUnavailableException>(
            () => Client(handler, key: null).SearchMotorcyclesAsync("Yamaha", "FZ8", null, default));
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_spent_quota_is_unavailable_rather_than_an_error()
    {
        var handler = new FakeCatalogue(_ => (HttpStatusCode.TooManyRequests, "{}"));

        var ex = await Should.ThrowAsync<ServiceUnavailableException>(
            () => Client(handler).GetCarYearsAsync("Lexus", "IS", default));
        ex.ShouldNotBeOfType<VehicleSpecsRefusedException>();
    }
}
