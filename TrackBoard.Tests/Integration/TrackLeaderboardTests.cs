using System.Net;
using System.Net.Http.Json;
using Shouldly;
using TrackBoard.Dtos;
using TrackBoard.Entities;

namespace TrackBoard.Tests.Integration;

public class TrackLeaderboardTests(TrackBoardApiFactory factory) : TrackProTestBase(factory)
{
    [Fact]
    public async Task Each_driver_appears_once_with_their_best_lap_fastest_first()
    {
        var (alice, aliceAuth) = await DriverAsync("Alice");
        var track = await CreateTrackAsync(alice);
        await UploadSessionAsync(alice, SessionBody(track.Id, [Lap(1, 95_000), Lap(2, 92_000)]));
        await UploadSessionAsync(alice, SessionBody(track.Id, [Lap(1, 93_000)]));

        var (bob, bobAuth) = await DriverAsync("Bob");
        await UploadSessionAsync(bob, SessionBody(track.Id, [Lap(1, 91_500), Lap(2, 94_000)]));

        var board = await LeaderboardAsync(CreateClient(), track.Id);

        board.TrackName.ShouldBe(track.Name);
        board.Entries.Select(e => (e.UserId, e.LapTimeMs)).ShouldBe(
        [
            (bobAuth.User.Id, 91_500),
            (aliceAuth.User.Id, 92_000),
        ]);
        board.Entries.Select(e => e.Rank).ShouldBe([1, 2]);
        board.Entries.Select(e => e.GapToLeaderMs).ShouldBe([0, 500]);
    }

    [Fact]
    public async Task Per_car_gives_a_driver_a_line_for_each_car()
    {
        var (client, auth) = await DriverAsync();
        var track = await CreateTrackAsync(client);
        var quick = await CreateVehicleAsync(client);
        var slow = await CreateVehicleAsync(client);

        await UploadSessionAsync(client, SessionBody(track.Id, [Lap(1, 95_000), Lap(2, 94_000)], vehicleId: slow));
        await UploadSessionAsync(client, SessionBody(track.Id, [Lap(1, 90_000)], vehicleId: quick));
        await UploadSessionAsync(client, SessionBody(track.Id, [Lap(1, 99_000)]));

        var response = await client.GetAsync($"/api/v1/tracks/{track.Id}/leaderboard?perCar=true");
        var board = (await response.Content.ReadFromJsonAsync<TrackLeaderboardResponse>(Json))!;

        board.Entries.Select(e => (e.Rank, e.LapTimeMs)).ShouldBe([(1, 90_000), (2, 94_000), (3, 99_000)]);
        board.Entries.ShouldAllBe(e => e.UserId == auth.User.Id);
        board.Entries.Select(e => e.LapId).Distinct().Count().ShouldBe(3);
        board.Me!.LapTimeMs.ShouldBe(90_000);
    }

    [Fact]
    public async Task Without_per_car_a_driver_stays_on_one_line_for_older_apps()
    {
        var (client, auth) = await DriverAsync();
        var track = await CreateTrackAsync(client);

        await UploadSessionAsync(
            client, SessionBody(track.Id, [Lap(1, 91_000)], vehicleId: await CreateVehicleAsync(client)));
        await UploadSessionAsync(
            client, SessionBody(track.Id, [Lap(1, 90_000)], vehicleId: await CreateVehicleAsync(client)));

        var (other, otherAuth) = await DriverAsync();
        await UploadSessionAsync(other, SessionBody(track.Id, [Lap(1, 92_000)]));

        var board = await LeaderboardAsync(CreateClient(), track.Id);

        board.Entries.Select(e => (e.Rank, e.UserId, e.LapTimeMs)).ShouldBe(
        [
            (1, auth.User.Id, 90_000),
            (2, otherAuth.User.Id, 92_000),
        ]);
    }

    [Fact]
    public async Task A_tie_goes_to_whoever_set_the_time_first()
    {
        var (early, earlyAuth) = await DriverAsync("Early");
        var track = await CreateTrackAsync(early);
        await UploadSessionAsync(early, SessionBody(track.Id, [Lap(1, 90_000)], startedAt: DateTimeOffset.UtcNow.AddDays(-2)));

        var (late, _) = await DriverAsync("Late");
        await UploadSessionAsync(late, SessionBody(track.Id, [Lap(1, 90_000)], startedAt: DateTimeOffset.UtcNow.AddDays(-1)));

        var board = await LeaderboardAsync(CreateClient(), track.Id);

        board.Entries[0].UserId.ShouldBe(earlyAuth.User.Id);
        board.Entries.Select(e => e.Rank).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Private_voided_and_signal_gap_laps_never_appear()
    {
        var (client, auth) = await DriverAsync();
        var track = await CreateTrackAsync(client);

        // Each of these would beat the one lap that should count.
        await UploadSessionAsync(client, SessionBody(track.Id, [Lap(1, 50_000)], visibility: "Private"));
        await UploadSessionAsync(client, SessionBody(track.Id, [Lap(1, 51_000)], voided: true));
        await UploadSessionAsync(client, SessionBody(track.Id, [Lap(1, 52_000, signalGap: true), Lap(2, 90_000)]));

        var board = await LeaderboardAsync(CreateClient(), track.Id);

        var entry = board.Entries.ShouldHaveSingleItem();
        entry.UserId.ShouldBe(auth.User.Id);
        entry.LapTimeMs.ShouldBe(90_000);
    }

    [Fact]
    public async Task Phone_and_esp32_laps_share_one_board_and_show_their_source()
    {
        var (esp, _) = await DriverAsync("Esp");
        var track = await CreateTrackAsync(esp);
        await UploadSessionAsync(esp, SessionBody(track.Id, [Lap(1, 90_000)], gpsSource: "Bluetooth"));

        var (phone, _) = await DriverAsync("Phone");
        await UploadSessionAsync(phone, SessionBody(track.Id, [Lap(1, 91_000)], gpsSource: "PhoneGps"));

        var board = await LeaderboardAsync(CreateClient(), track.Id);

        board.Entries.Select(e => e.GpsSource).ShouldBe([GpsSource.Bluetooth, GpsSource.PhoneGps]);
    }

    [Fact]
    public async Task Entries_carry_the_best_laps_sectors_and_vehicle()
    {
        var (client, _) = await DriverAsync();
        var track = await CreateTrackAsync(client);
        var vehicleId = await CreateVehicleAsync(client);
        await UploadSessionAsync(client, SessionBody(
            track.Id,
            [Lap(1, 95_000, sectorMs: [45_000, 50_000]), Lap(2, 90_000, sectorMs: [42_000, 48_000])],
            vehicleId));

        var entry = (await LeaderboardAsync(CreateClient(), track.Id)).Entries.ShouldHaveSingleItem();

        entry.Sectors.Select(s => s.SplitMs).ShouldBe([42_000, 48_000]);
        entry.Vehicle.ShouldBe(new LeaderboardVehicleResponse("Porsche", "911 GT3", 2023));
    }

    [Fact]
    public async Task The_leaderboard_is_public_and_me_needs_a_token()
    {
        var (client, auth) = await DriverAsync();
        var track = await CreateTrackAsync(client);
        await UploadSessionAsync(client, SessionBody(track.Id, [Lap(1, 90_000)]));

        var anonymous = await LeaderboardAsync(CreateClient(), track.Id);
        anonymous.Me.ShouldBeNull();

        var signedIn = await LeaderboardAsync(client, track.Id);
        signedIn.Me.ShouldNotBeNull().UserId.ShouldBe(auth.User.Id);
    }

    [Fact]
    public async Task Me_is_returned_even_outside_the_limit()
    {
        var (leader, _) = await DriverAsync("Leader");
        var track = await CreateTrackAsync(leader);
        await UploadSessionAsync(leader, SessionBody(track.Id, [Lap(1, 80_000)]));

        var (slow, slowAuth) = await DriverAsync("Slow");
        await UploadSessionAsync(slow, SessionBody(track.Id, [Lap(1, 99_000)]));

        var board = await LeaderboardAsync(slow, track.Id, limit: 1);

        board.Entries.ShouldHaveSingleItem().Rank.ShouldBe(1);
        board.Me.ShouldNotBeNull();
        board.Me.UserId.ShouldBe(slowAuth.User.Id);
        board.Me.Rank.ShouldBe(2);
    }

    [Fact]
    public async Task An_invalid_token_is_401_rather_than_silently_anonymous()
    {
        var (client, _) = await DriverAsync();
        var track = await CreateTrackAsync(client);

        var stale = CreateClient();
        Authenticate(stale, "expired.or.garbage");

        (await stale.GetAsync($"/api/v1/tracks/{track.Id}/leaderboard"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unpublished_or_unknown_track_has_no_leaderboard()
    {
        var (client, _) = await DriverAsync();
        var hidden = await CreateTrackAsync(client, TrackBody(visibility: "Private"));

        (await client.GetAsync($"/api/v1/tracks/{hidden.Id}/leaderboard")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/v1/tracks/{Guid.NewGuid()}/leaderboard")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_limit_outside_1_to_100_is_rejected()
    {
        var (client, _) = await DriverAsync();
        var track = await CreateTrackAsync(client);

        (await client.GetAsync($"/api/v1/tracks/{track.Id}/leaderboard?limit=0")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync($"/api/v1/tracks/{track.Id}/leaderboard?limit=101")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_faster_upload_shows_immediately_despite_the_cache()
    {
        var (client, _) = await DriverAsync();
        var track = await CreateTrackAsync(client);
        var sessionId = Guid.NewGuid();
        await UploadSessionAsync(client, SessionBody(track.Id, [Lap(1, 95_000)]), sessionId);

        // Populate the cache, then change the data underneath it.
        (await LeaderboardAsync(CreateClient(), track.Id)).Entries[0].LapTimeMs.ShouldBe(95_000);

        await UploadSessionAsync(client, SessionBody(track.Id, [Lap(1, 95_000), Lap(2, 91_000)]), sessionId);

        (await LeaderboardAsync(CreateClient(), track.Id)).Entries[0].LapTimeMs.ShouldBe(91_000);
    }

    [Fact]
    public async Task Voiding_or_deleting_a_session_removes_it_from_the_cached_board()
    {
        var (client, _) = await DriverAsync();
        var track = await CreateTrackAsync(client);
        var voidedId = Guid.NewGuid();
        var deletedId = Guid.NewGuid();
        await UploadSessionAsync(client, SessionBody(track.Id, [Lap(1, 90_000)]), voidedId);
        await UploadSessionAsync(client, SessionBody(track.Id, [Lap(1, 95_000)]), deletedId);

        (await LeaderboardAsync(CreateClient(), track.Id)).Entries[0].LapTimeMs.ShouldBe(90_000);

        await UploadSessionAsync(client, SessionBody(track.Id, [Lap(1, 90_000)], voided: true), voidedId);
        (await LeaderboardAsync(CreateClient(), track.Id)).Entries[0].LapTimeMs.ShouldBe(95_000);

        (await client.DeleteAsync($"/api/v1/sessions/{deletedId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await LeaderboardAsync(CreateClient(), track.Id)).Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Renaming_a_vehicle_updates_the_cached_board()
    {
        var (client, _) = await DriverAsync();
        var track = await CreateTrackAsync(client);
        var vehicleId = await CreateVehicleAsync(client);
        await UploadSessionAsync(client, SessionBody(track.Id, [Lap(1, 90_000)], vehicleId));

        (await LeaderboardAsync(CreateClient(), track.Id)).Entries[0].Vehicle!.Model.ShouldBe("911 GT3");

        var renamed = new
        {
            manufacturer = "Porsche",
            model = "911 GT3 RS",
            year = 2023,
            engineType = "F6",
            horsepower = 525,
            weight = 1450.0,
            drivetrain = "RWD",
            fuelType = "Petrol",
            tireType = "Slick",
            transmission = "PDK",
        };
        (await client.PutAsJsonAsync($"/api/v1/vehicles/{vehicleId}", renamed, Json)).EnsureSuccessStatusCode();

        (await LeaderboardAsync(CreateClient(), track.Id)).Entries[0].Vehicle!.Model.ShouldBe("911 GT3 RS");
    }

    [Fact]
    public async Task A_session_reports_the_rank_of_the_drivers_best_lap()
    {
        var (fast, _) = await DriverAsync("Fast");
        var track = await CreateTrackAsync(fast);
        await UploadSessionAsync(fast, SessionBody(track.Id, [Lap(1, 85_000)]));

        var (client, _) = await DriverAsync("Second");
        var session = await UploadSessionAsync(client, SessionBody(track.Id, [Lap(1, 95_000), Lap(2, 90_000)]));

        session.Laps.Single(l => l.LapNumber == 2).LeaderboardRank.ShouldBe(2);
        // Only the driver's best lap holds a rank; slower laps still count but are not ranked.
        var slower = session.Laps.Single(l => l.LapNumber == 1);
        slower.CountsForLeaderboard.ShouldBeTrue();
        slower.LeaderboardRank.ShouldBeNull();
    }

    [Fact]
    public async Task An_entry_carries_the_top_speed_of_that_lap_not_the_sessions()
    {
        var (client, _) = await DriverAsync();
        var track = await CreateTrackAsync(client);
        await UploadSessionAsync(client, SessionBody(track.Id,
        [
            new { lapNumber = 1, timeMs = 95_000, topSpeedKmh = 214.6 },
            new { lapNumber = 2, timeMs = 90_000, topSpeedKmh = 208.3 },
        ]));

        var (other, _) = await DriverAsync("No speed");
        await UploadSessionAsync(other, SessionBody(track.Id, [Lap(1, 91_000)]));

        var board = await LeaderboardAsync(CreateClient(), track.Id);

        board.Entries.Select(e => e.TopSpeedKmh).ShouldBe([208.3, null]);
    }

    [Fact]
    public async Task A_top_speed_beyond_500_kmh_is_rejected()
    {
        var (client, _) = await DriverAsync();
        var track = await CreateTrackAsync(client);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/sessions/{Guid.NewGuid()}",
            SessionBody(track.Id, [new { lapNumber = 1, timeMs = 90_000, topSpeedKmh = 612.0 }]),
            Json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
