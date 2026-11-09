using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Shouldly;
using TrackBoard.Dtos;
using TrackBoard.Entities;

namespace TrackBoard.Tests.Integration;

public class CarTests(TrackBoardApiFactory factory) : TrackProTestBase(factory)
{
    private static object DragSession(Guid vehicleId, string metric, int timeMs, string visibility = "Ranked") => new
    {
        name = "Drag session",
        startedAt = DateTimeOffset.UtcNow.AddHours(-1).ToString("O", CultureInfo.InvariantCulture),
        vehicleId,
        gpsSource = "Wifi",
        visibility,
        laps = Array.Empty<object>(),
        dragResults = new[] { new { metric, timeMs } },
    };

    private async Task<PublicCarResponse> CarAsync(Guid id)
    {
        var response = await CreateClient().GetAsync($"/api/v1/cars/{id}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PublicCarResponse>(Json))!;
    }

    [Fact]
    public async Task A_car_on_a_track_board_has_a_public_page_with_its_specs_and_place()
    {
        var (fast, _) = await DriverAsync("Fast");
        var track = await CreateTrackAsync(fast);
        await UploadSessionAsync(fast, SessionBody(track.Id, [Lap(1, 85_000)], vehicleId: await CreateVehicleAsync(fast)));

        var (owner, auth) = await DriverAsync("Owner");
        var car = await CreateVehicleAsync(owner);
        await UploadSessionAsync(owner, SessionBody(track.Id,
        [
            new { lapNumber = 1, timeMs = 92_000, topSpeedKmh = 201.5 },
            new { lapNumber = 2, timeMs = 90_000, topSpeedKmh = 198.0 },
        ], vehicleId: car));

        var page = await CarAsync(car);

        page.OwnerId.ShouldBe(auth.User.Id);
        page.OwnerDisplayName.ShouldBe("Owner");
        (page.Manufacturer, page.Model, page.Year, page.Horsepower).ShouldBe(("Porsche", "911 GT3", 2023, 510));
        page.CarClass.ShouldNotBeNull();
        var best = page.TrackBests.ShouldHaveSingleItem();
        (best.TrackId, best.Rank, best.FieldSize, best.LapTimeMs, best.GapToLeaderMs, best.TopSpeedKmh)
            .ShouldBe((track.Id, 2, 2, 90_000, 5_000, 198.0));
        page.DragBests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cars_drag_bests_show_their_overall_place()
    {
        var (client, _) = await DriverAsync();
        var quick = await CreateVehicleAsync(client);
        var slow = await CreateVehicleAsync(client);
        await UploadSessionAsync(client, DragSession(quick, "ZeroToHundred", 3_900));
        await UploadSessionAsync(client, DragSession(slow, "ZeroToHundred", 4_400));
        await UploadSessionAsync(client, DragSession(slow, "QuarterMile", 12_100));

        var page = await CarAsync(slow);

        page.TrackBests.ShouldBeEmpty();
        page.DragBests.Select(b => (b.Metric, b.TimeMs)).ShouldBe(
            [(DragMetric.ZeroToHundred, 4_400), (DragMetric.QuarterMile, 12_100)]);

        // The board is shared by every test, so the place is checked against the board itself.
        var board = (await CreateClient().GetFromJsonAsync<DragLeaderboardResponse>(
            "/api/v1/drag/leaderboard?metric=ZeroToHundred&limit=100", Json))!;
        var onBoard = board.Entries.Single(e => e.Vehicle.Id == slow);
        var zeroToHundred = page.DragBests[0];
        (zeroToHundred.Rank, zeroToHundred.GapToLeaderMs).ShouldBe((onBoard.Rank, onBoard.GapToLeaderMs));
        zeroToHundred.FieldSize.ShouldBe(board.Entries.Count);
        board.Entries.Single(e => e.Vehicle.Id == quick).Rank.ShouldBeLessThan(onBoard.Rank);
    }

    [Fact]
    public async Task A_car_kept_to_private_sessions_or_unknown_has_no_page()
    {
        var (client, _) = await DriverAsync();
        var track = await CreateTrackAsync(client);
        var car = await CreateVehicleAsync(client);
        await UploadSessionAsync(client, SessionBody(track.Id, [Lap(1, 90_000)], vehicleId: car, visibility: "Private"));
        await UploadSessionAsync(client, DragSession(car, "ZeroToHundred", 4_000, visibility: "Private"));
        var unused = await CreateVehicleAsync(client);

        (await CreateClient().GetAsync($"/api/v1/cars/{car}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CreateClient().GetAsync($"/api/v1/cars/{unused}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CreateClient().GetAsync($"/api/v1/cars/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Track_and_drag_board_entries_link_to_their_car()
    {
        var (client, _) = await DriverAsync();
        var track = await CreateTrackAsync(client);
        var car = await CreateVehicleAsync(client);
        await UploadSessionAsync(client, SessionBody(track.Id, [Lap(1, 90_000)], vehicleId: car));
        await UploadSessionAsync(client, DragSession(car, "ZeroToHundred", 4_000));

        (await LeaderboardAsync(CreateClient(), track.Id)).Entries.Single().Vehicle!.Id.ShouldBe(car);

        var drag = (await CreateClient().GetFromJsonAsync<DragLeaderboardResponse>(
            "/api/v1/drag/leaderboard?metric=ZeroToHundred", Json))!;
        drag.Entries.Single().Vehicle.Id.ShouldBe(car);
    }
}
