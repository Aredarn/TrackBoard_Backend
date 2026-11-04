using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Shouldly;
using TrackBoard.Dtos;
using TrackBoard.Entities;

namespace TrackBoard.Tests.Integration;

public class DragLeaderboardTests(TrackBoardApiFactory factory) : TrackProTestBase(factory)
{
    private static object Result(string metric, int timeMs, double? trapSpeedKmh = null) =>
        new { metric, timeMs, trapSpeedKmh };

    private static object DragSession(
        Guid? vehicleId,
        object[] results,
        string visibility = "Ranked",
        string gpsSource = "Wifi",
        bool voided = false,
        DateTimeOffset? startedAt = null) => new
        {
            name = "Drag session",
            startedAt = (startedAt ?? DateTimeOffset.UtcNow.AddHours(-1)).ToString("O", CultureInfo.InvariantCulture),
            vehicleId,
            gpsSource,
            visibility,
            voided,
            laps = Array.Empty<object>(),
            dragResults = results,
        };

    private static async Task<Guid> CarAsync(HttpClient client, int horsepower = 510, double weight = 1418.0)
    {
        var id = Guid.NewGuid();
        var (status, _) = await PutAsync<VehicleResponse>(client, $"/api/v1/vehicles/{id}", Car(horsepower, weight));
        status.ShouldBe(HttpStatusCode.Created);
        return id;
    }

    private static object Car(int horsepower, double weight) => new
    {
        manufacturer = "Porsche",
        model = "911 GT3",
        year = 2023,
        engineType = "F6",
        horsepower,
        weight,
        drivetrain = "RWD",
        fuelType = "Petrol",
        tireType = "Slick",
        transmission = "PDK",
    };

    private static async Task<DragLeaderboardResponse> BoardAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"/api/v1/drag/leaderboard{query}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<DragLeaderboardResponse>(Json))!;
    }

    /// <summary>The board is shared by every test in the class, so each reads only its own drivers.</summary>
    private static List<DragLeaderboardEntryResponse> Only(DragLeaderboardResponse board, params Guid[] users) =>
        board.Entries.Where(e => users.Contains(e.UserId)).ToList();

    [Fact]
    public async Task Each_driver_appears_once_with_their_best_time_fastest_first()
    {
        var (alice, aliceAuth) = await DriverAsync("Alice");
        var aliceCar = await CarAsync(alice);
        await UploadSessionAsync(alice, DragSession(aliceCar, [Result("ZeroToHundred", 5_100)]));
        await UploadSessionAsync(alice, DragSession(aliceCar, [Result("ZeroToHundred", 4_900)]));

        var (bob, bobAuth) = await DriverAsync("Bob");
        await UploadSessionAsync(bob, DragSession(await CarAsync(bob), [Result("ZeroToHundred", 4_500)]));

        var board = await BoardAsync(CreateClient(), "?metric=ZeroToHundred&limit=100");

        var entries = Only(board, aliceAuth.User.Id, bobAuth.User.Id);
        entries.Select(e => (e.UserId, e.TimeMs)).ShouldBe([(bobAuth.User.Id, 4_500), (aliceAuth.User.Id, 4_900)]);
        (entries[1].TimeMs - entries[0].TimeMs).ShouldBe(entries[1].GapToLeaderMs - entries[0].GapToLeaderMs);
        board.Entries.Select(e => e.Rank).ShouldBe(Enumerable.Range(1, board.Entries.Count));
    }

    [Fact]
    public async Task The_quarter_mile_keeps_its_trap_speed_and_metrics_stay_apart()
    {
        var (client, auth) = await DriverAsync();
        await UploadSessionAsync(client, DragSession(
            await CarAsync(client),
            [Result("QuarterMile", 12_400, 186.5), Result("HundredToTwoHundred", 9_800)]));

        var mine = Only(await BoardAsync(CreateClient(), "?metric=QuarterMile&limit=100"), auth.User.Id).Single();
        mine.TimeMs.ShouldBe(12_400);
        mine.TrapSpeedKmh.ShouldBe(186.5);

        Only(await BoardAsync(CreateClient(), "?metric=HundredToTwoHundred&limit=100"), auth.User.Id)
            .Single().TimeMs.ShouldBe(9_800);
        Only(await BoardAsync(CreateClient(), "?metric=ZeroToHundred&limit=100"), auth.User.Id).ShouldBeEmpty();
    }

    [Fact]
    public async Task Private_voided_and_carless_results_never_appear()
    {
        var (client, auth) = await DriverAsync();
        var car = await CarAsync(client);

        await UploadSessionAsync(client, DragSession(car, [Result("ZeroToHundred", 2_000)], visibility: "Private"));
        await UploadSessionAsync(client, DragSession(car, [Result("ZeroToHundred", 2_100)], voided: true));
        await UploadSessionAsync(client, DragSession(null, [Result("ZeroToHundred", 2_200)]));
        await UploadSessionAsync(client, DragSession(car, [Result("ZeroToHundred", 6_000)]));

        var mine = Only(await BoardAsync(CreateClient(), "?limit=100"), auth.User.Id).Single();
        mine.TimeMs.ShouldBe(6_000);
    }

    [Fact]
    public async Task A_class_filter_finds_a_drivers_slower_car_in_its_own_class()
    {
        var (client, auth) = await DriverAsync();
        var fast = await CarAsync(client, horsepower: 700, weight: 1500); // 467 hp/t: S
        var slow = await CarAsync(client, horsepower: 120, weight: 1200); // 100 hp/t: C

        await UploadSessionAsync(client, DragSession(fast, [Result("ZeroToHundred", 3_000)]));
        await UploadSessionAsync(client, DragSession(slow, [Result("ZeroToHundred", 9_500)]));

        var overall = Only(await BoardAsync(CreateClient(), "?limit=100"), auth.User.Id).Single();
        overall.TimeMs.ShouldBe(3_000);
        overall.CarClass.ShouldBe(DragClass.S);

        var classC = await BoardAsync(CreateClient(), "?class=C&limit=100");
        classC.CarClass.ShouldBe(DragClass.C);
        var mine = Only(classC, auth.User.Id).Single();
        mine.TimeMs.ShouldBe(9_500);
        classC.Entries.ShouldAllBe(e => e.CarClass == DragClass.C);
    }

    [Fact]
    public async Task Editing_a_car_moves_it_between_classes_straight_away()
    {
        var (client, auth) = await DriverAsync();
        var car = await CarAsync(client, horsepower: 110, weight: 1200); // D
        await UploadSessionAsync(client, DragSession(car, [Result("ZeroToHundred", 11_000)]));

        Only(await BoardAsync(CreateClient(), "?class=D&limit=100"), auth.User.Id).ShouldHaveSingleItem();

        var (status, _) = await PutAsync<VehicleResponse>(client, $"/api/v1/vehicles/{car}", Car(300, 1200));
        status.ShouldBe(HttpStatusCode.OK);

        Only(await BoardAsync(CreateClient(), "?class=D&limit=100"), auth.User.Id).ShouldBeEmpty();
        Only(await BoardAsync(CreateClient(), "?class=B&limit=100"), auth.User.Id).ShouldBeEmpty();
        Only(await BoardAsync(CreateClient(), "?class=A&limit=100"), auth.User.Id).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Signing_in_adds_your_own_entry_even_outside_the_limit()
    {
        var (other, _) = await DriverAsync();
        await UploadSessionAsync(other, DragSession(await CarAsync(other), [Result("ZeroToHundred", 1_500)]));

        var (client, auth) = await DriverAsync();
        await UploadSessionAsync(client, DragSession(await CarAsync(client), [Result("ZeroToHundred", 30_000)]));

        var board = await BoardAsync(client, "?limit=1");

        board.Entries.Count.ShouldBe(1);
        board.Me.ShouldNotBeNull();
        board.Me.UserId.ShouldBe(auth.User.Id);
        (await BoardAsync(CreateClient(), "?limit=1")).Me.ShouldBeNull();
    }

    [Fact]
    public async Task Reuploading_replaces_results_and_deleting_withdraws_them()
    {
        var (client, auth) = await DriverAsync();
        var car = await CarAsync(client);
        var id = Guid.NewGuid();

        await UploadSessionAsync(client, DragSession(car, [Result("ZeroToHundred", 5_000)]), id);
        Only(await BoardAsync(CreateClient(), "?limit=100"), auth.User.Id).Single().TimeMs.ShouldBe(5_000);

        await UploadSessionAsync(client, DragSession(car, [Result("ZeroToHundred", 4_800)]), id);
        Only(await BoardAsync(CreateClient(), "?limit=100"), auth.User.Id).Single().TimeMs.ShouldBe(4_800);

        (await client.DeleteAsync($"/api/v1/sessions/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Only(await BoardAsync(CreateClient(), "?limit=100"), auth.User.Id).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_session_reports_its_results_and_their_rank()
    {
        var (client, _) = await DriverAsync();
        var session = await UploadSessionAsync(
            client,
            DragSession(await CarAsync(client), [Result("ZeroToHundred", 1_200)]));

        session.DragResultCount.ShouldBe(1);
        var result = session.DragResults.Single();
        result.CountsForLeaderboard.ShouldBeTrue();
        result.LeaderboardRank.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("ZeroToHundred", 900)]
    [InlineData("HundredToTwoHundred", 1_400)]
    [InlineData("QuarterMile", 4_900)]
    public async Task Impossible_times_are_refused(string metric, int timeMs)
    {
        var (client, _) = await DriverAsync();

        var (status, _) = await PutAsync<SessionResponse>(
            client,
            $"/api/v1/sessions/{Guid.NewGuid()}",
            DragSession(await CarAsync(client), [Result(metric, timeMs)]));

        status.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Malformed_result_lists_are_refused()
    {
        var (client, _) = await DriverAsync();
        var car = await CarAsync(client);
        var track = await CreateTrackAsync(client);

        async Task<HttpStatusCode> Put(object body) =>
            (await PutAsync<SessionResponse>(client, $"/api/v1/sessions/{Guid.NewGuid()}", body)).Status;

        (await Put(DragSession(car, [Result("ZeroToHundred", 5_000), Result("ZeroToHundred", 5_100)])))
            .ShouldBe(HttpStatusCode.BadRequest);
        (await Put(DragSession(car, [Result("ZeroToHundred", 5_000, 150.0)])))
            .ShouldBe(HttpStatusCode.BadRequest);

        var onTrack = new
        {
            name = "Both",
            startedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            trackId = track.Id,
            gpsSource = "Wifi",
            laps = Array.Empty<object>(),
            dragResults = new[] { Result("ZeroToHundred", 5_000) },
        };
        (await Put(onTrack)).ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unknown_metric_is_a_bad_request()
    {
        var response = await CreateClient().GetAsync("/api/v1/drag/leaderboard?metric=Nonsense");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Deleting_the_account_takes_its_results_off_the_board()
    {
        var (client, auth) = await DriverAsync();
        await UploadSessionAsync(client, DragSession(await CarAsync(client), [Result("ZeroToHundred", 7_000)]));
        Only(await BoardAsync(CreateClient(), "?limit=100"), auth.User.Id).ShouldHaveSingleItem();

        (await DeleteAccountAsync(client)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        Only(await BoardAsync(CreateClient(), "?limit=100"), auth.User.Id).ShouldBeEmpty();
    }
}
