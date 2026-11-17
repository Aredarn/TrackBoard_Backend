using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using TrackBoard.Common;
using TrackBoard.Dtos;
using TrackBoard.Entities;

namespace TrackBoard.Tests.Integration;

public class TrackTests(TrackBoardApiFactory factory) : TrackProTestBase(factory)
{
    [Fact]
    public async Task A_new_track_is_created_with_its_points_in_order()
    {
        var (client, auth) = await DriverAsync("Builder");
        var id = Guid.NewGuid();

        var track = await CreateTrackAsync(client, TrackBody("Hungaroring"), id);

        track.Id.ShouldBe(id);
        track.Name.ShouldBe("Hungaroring");
        track.Type.ShouldBe(TrackType.Circuit);
        track.OwnerDisplayName.ShouldBe("Builder");
        track.Points.Select(p => p.Seq).ShouldBe([0, 1, 2, 3]);
        track.StartLatitude.ShouldBe(HungaroringLat);
        track.SectorCount.ShouldBe(1);
        track.GeometryLocked.ShouldBeFalse();
    }

    [Fact]
    public async Task Points_sent_out_of_order_are_stored_in_seq_order()
    {
        var (client, _) = await DriverAsync();
        var shuffled = Loop(HungaroringLat, HungaroringLon).Reverse().ToArray();

        var track = await CreateTrackAsync(client, TrackBody(points: shuffled));

        track.Points.Select(p => p.Seq).ShouldBe([0, 1, 2, 3]);
    }

    [Fact]
    public async Task Putting_the_same_id_again_replaces_rather_than_duplicates()
    {
        var (client, _) = await DriverAsync();
        var id = Guid.NewGuid();
        await CreateTrackAsync(client, TrackBody("Old name", visibility: "Private"), id);

        var (status, track) = await PutAsync<TrackResponse>(
            client, $"/api/v1/tracks/{id}", TrackBody("New name", visibility: "Private"));

        status.ShouldBe(HttpStatusCode.OK);
        track!.Name.ShouldBe("New name");

        var mine = await client.GetFromJsonAsync<PagedResult<TrackSummaryResponse>>("/api/v1/tracks?mine=true", Json);
        mine!.Items.Count(t => t.Id == id).ShouldBe(1);
    }

    [Fact]
    public async Task Another_driver_cannot_overwrite_a_track_by_reusing_its_id()
    {
        var (owner, _) = await DriverAsync();
        var id = Guid.NewGuid();
        await CreateTrackAsync(owner, TrackBody("Mine"), id);

        var (intruder, _) = await DriverAsync();
        var (status, _) = await PutAsync<TrackResponse>(intruder, $"/api/v1/tracks/{id}", TrackBody("Stolen"));

        status.ShouldBe(HttpStatusCode.Forbidden);

        var track = await CreateClient().GetFromJsonAsync<TrackResponse>($"/api/v1/tracks/{id}", Json);
        track!.Name.ShouldBe("Mine");
    }

    [Fact]
    public async Task A_published_track_is_readable_without_signing_in()
    {
        var (client, _) = await DriverAsync();
        var track = await CreateTrackAsync(client, TrackBody(visibility: "Published"));

        var response = await CreateClient().GetAsync($"/api/v1/tracks/{track.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_private_track_is_404_to_everyone_but_its_owner()
    {
        var (owner, _) = await DriverAsync();
        var track = await CreateTrackAsync(owner, TrackBody(visibility: "Private"));

        (await owner.GetAsync($"/api/v1/tracks/{track.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var (stranger, _) = await DriverAsync();
        (await stranger.GetAsync($"/api/v1/tracks/{track.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CreateClient().GetAsync($"/api/v1/tracks/{track.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_invalid_token_on_a_public_endpoint_is_401_not_anonymous()
    {
        var (client, _) = await DriverAsync();
        var track = await CreateTrackAsync(client);

        var stale = CreateClient();
        Authenticate(stale, "not.a.valid-token");

        (await stale.GetAsync($"/api/v1/tracks/{track.Id}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Writing_a_track_requires_signing_in()
    {
        var (status, _) = await PutAsync<TrackResponse>(
            CreateClient(), $"/api/v1/tracks/{Guid.NewGuid()}", TrackBody());

        status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Duplicate_point_positions_are_rejected()
    {
        var (client, _) = await DriverAsync();
        object[] points =
        [
            new { seq = 0, latitude = 47.0, longitude = 19.0, isStartPoint = true },
            new { seq = 0, latitude = 47.1, longitude = 19.1 },
        ];

        var (status, _) = await PutAsync<TrackResponse>(
            client, $"/api/v1/tracks/{Guid.NewGuid()}", TrackBody(points: points));

        status.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_track_needs_at_least_two_points()
    {
        var (client, _) = await DriverAsync();
        object[] points = [new { seq = 0, latitude = 47.0, longitude = 19.0 }];

        var (status, _) = await PutAsync<TrackResponse>(
            client, $"/api/v1/tracks/{Guid.NewGuid()}", TrackBody(points: points));

        status.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_near_a_point_finds_close_tracks_nearest_first()
    {
        var (client, _) = await DriverAsync();
        // Far from every other test's tracks, so the result set is exactly these.
        const double lat = -33.0;
        const double lon = 151.0;

        var near = await CreateTrackAsync(client, TrackBody("Near", lat + 0.01, lon));
        var nearer = await CreateTrackAsync(client, TrackBody("Nearer", lat + 0.001, lon));
        await CreateTrackAsync(client, TrackBody("Far", lat + 2, lon));
        await CreateTrackAsync(client, TrackBody("Private nearby", lat, lon, visibility: "Private"));

        var result = await CreateClient().GetFromJsonAsync<PagedResult<TrackSummaryResponse>>(
            $"/api/v1/tracks?near={lat},{lon}&radiusKm=10", Json);

        result!.Items.Select(t => t.Id).ShouldBe([nearer.Id, near.Id]);
        result.Items[0].DistanceKm!.Value.ShouldBeLessThan(0.2);
        result.Items[1].DistanceKm!.Value.ShouldBeInRange(1.0, 1.2);
        result.TotalCount.ShouldBe(2);
    }

    [Theory]
    [InlineData("not-a-point")]
    [InlineData("95,10")]
    [InlineData("10,190")]
    public async Task A_malformed_or_out_of_range_near_is_400(string near)
    {
        var response = await CreateClient().GetAsync($"/api/v1/tracks?near={near}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task My_tracks_need_a_token_and_include_private_ones()
    {
        (await CreateClient().GetAsync("/api/v1/tracks?mine=true"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var (client, _) = await DriverAsync();
        var hidden = await CreateTrackAsync(client, TrackBody(visibility: "Private"));

        var mine = await client.GetFromJsonAsync<PagedResult<TrackSummaryResponse>>("/api/v1/tracks?mine=true", Json);

        mine!.Items.ShouldHaveSingleItem().Id.ShouldBe(hidden.Id);
    }

    [Fact]
    public async Task A_private_track_stays_out_of_every_view_another_signed_in_driver_has()
    {
        // The app backs up every track a driver made, unpublished ones included, as Private.
        const double lat = 61.5;
        const double lon = 23.7;
        var (owner, _) = await DriverAsync("Owner");
        var hidden = await CreateTrackAsync(owner, TrackBody("Backed-up only", lat, lon, visibility: "Private"));

        var (stranger, _) = await DriverAsync("Stranger");

        var listed = await stranger.GetFromJsonAsync<PagedResult<TrackSummaryResponse>>("/api/v1/tracks?pageSize=100", Json);
        listed!.Items.ShouldNotContain(t => t.Id == hidden.Id);

        var near = await stranger.GetFromJsonAsync<PagedResult<TrackSummaryResponse>>(
            "/api/v1/tracks?near=61.5,23.7&radiusKm=5", Json);
        near!.Items.ShouldNotContain(t => t.Id == hidden.Id);

        var theirs = await stranger.GetFromJsonAsync<PagedResult<TrackSummaryResponse>>("/api/v1/tracks?mine=true", Json);
        theirs!.Items.ShouldNotContain(t => t.Id == hidden.Id);

        (await stranger.GetAsync($"/api/v1/tracks/{hidden.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.GetAsync($"/api/v1/tracks/{hidden.Id}/leaderboard")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var ownList = await owner.GetFromJsonAsync<PagedResult<TrackSummaryResponse>>("/api/v1/tracks?mine=true", Json);
        ownList!.Items.ShouldContain(t => t.Id == hidden.Id);
    }

    [Fact]
    public async Task Geometry_is_locked_once_the_track_has_a_ranked_lap()
    {
        var (client, _) = await DriverAsync();
        var id = Guid.NewGuid();
        await CreateTrackAsync(client, TrackBody("Locked", visibility: "Published"), id);
        await UploadSessionAsync(client, SessionBody(id, [Lap(1, 100_000)]));

        // Moving a point would change the timing gates under a lap already on the board.
        var moved = Loop(HungaroringLat + 0.5, HungaroringLon);
        var response = await client.PutAsJsonAsync(
            $"/api/v1/tracks/{id}", TrackBody("Locked", points: moved), Json);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        problem.GetProperty("code").GetString().ShouldBe("TrackGeometryLocked");

        // Renaming, with the geometry untouched, is still allowed.
        var (status, renamed) = await PutAsync<TrackResponse>(
            client, $"/api/v1/tracks/{id}", TrackBody("Renamed"));

        status.ShouldBe(HttpStatusCode.OK);
        renamed!.Name.ShouldBe("Renamed");
        renamed.GeometryLocked.ShouldBeTrue();
        renamed.RankedLapCount.ShouldBe(1);
    }

    [Fact]
    public async Task Re_slicing_sectors_is_allowed_after_laps_rank()
    {
        // The app lets a driver re-slice sectors on any saved track. Sector gates do not move
        // the start/finish line, so they must not trip the geometry lock.
        var (client, _) = await DriverAsync();
        var id = Guid.NewGuid();
        await CreateTrackAsync(client, TrackBody("Sliced", visibility: "Published"), id);
        await UploadSessionAsync(client, SessionBody(id, [Lap(1, 100_000)]));

        object[] resliced =
        [
            new { seq = 0, latitude = HungaroringLat, longitude = HungaroringLon, altitude = 200.0, isStartPoint = true },
            new { seq = 1, latitude = HungaroringLat + 0.001, longitude = HungaroringLon },
            new { seq = 2, latitude = HungaroringLat + 0.001, longitude = HungaroringLon + 0.001, isSectorPoint = true, sectorIndex = 0 },
            new { seq = 3, latitude = HungaroringLat, longitude = HungaroringLon + 0.001, isSectorPoint = true, sectorIndex = 1 },
        ];

        var (status, track) = await PutAsync<TrackResponse>(
            client, $"/api/v1/tracks/{id}", TrackBody("Sliced", points: resliced));

        status.ShouldBe(HttpStatusCode.OK);
        track!.SectorCount.ShouldBe(2);
        track.Points.Count(p => p.IsSectorPoint).ShouldBe(2);
    }

    [Fact]
    public async Task Moving_the_start_line_is_refused_after_laps_rank()
    {
        var (client, _) = await DriverAsync();
        var id = Guid.NewGuid();
        await CreateTrackAsync(client, TrackBody("Start", visibility: "Published"), id);
        await UploadSessionAsync(client, SessionBody(id, [Lap(1, 100_000)]));

        object[] newStart =
        [
            new { seq = 0, latitude = HungaroringLat, longitude = HungaroringLon, altitude = 200.0 },
            new { seq = 1, latitude = HungaroringLat + 0.001, longitude = HungaroringLon, isStartPoint = true, isSectorPoint = true, sectorIndex = 0 },
            new { seq = 2, latitude = HungaroringLat + 0.001, longitude = HungaroringLon + 0.001 },
            new { seq = 3, latitude = HungaroringLat, longitude = HungaroringLon + 0.001 },
        ];

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tracks/{id}", TrackBody("Start", points: newStart), Json);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Geometry_can_change_while_no_lap_ranks_on_it()
    {
        var (client, _) = await DriverAsync();
        var id = Guid.NewGuid();
        await CreateTrackAsync(client, TrackBody(visibility: "Published"), id);
        // A private session does not rank, so it does not lock anything.
        await UploadSessionAsync(client, SessionBody(id, [Lap(1, 100_000)], visibility: "Private"));

        var (status, track) = await PutAsync<TrackResponse>(
            client, $"/api/v1/tracks/{id}", TrackBody(lat: 47.9, points: Loop(47.9, 19.0)));

        status.ShouldBe(HttpStatusCode.OK);
        track!.StartLatitude.ShouldBe(47.9);
    }

    [Fact]
    public async Task A_track_other_drivers_use_cannot_be_deleted_or_made_private()
    {
        var (owner, _) = await DriverAsync();
        var id = Guid.NewGuid();
        await CreateTrackAsync(owner, TrackBody(visibility: "Published"), id);

        var (other, _) = await DriverAsync();
        await UploadSessionAsync(other, SessionBody(id, [Lap(1, 99_000)], visibility: "Private"));

        (await owner.DeleteAsync($"/api/v1/tracks/{id}")).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var (status, _) = await PutAsync<TrackResponse>(
            owner, $"/api/v1/tracks/{id}", TrackBody(visibility: "Private"));

        status.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Deleting_your_own_track_keeps_your_sessions_but_unlinks_them()
    {
        var (client, _) = await DriverAsync();
        var trackId = Guid.NewGuid();
        await CreateTrackAsync(client, TrackBody(visibility: "Private"), trackId);
        var session = await UploadSessionAsync(client, SessionBody(trackId, [Lap(1, 90_000)], visibility: "Private"));

        (await client.DeleteAsync($"/api/v1/tracks/{trackId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var after = await client.GetFromJsonAsync<SessionResponse>($"/api/v1/sessions/{session.Id}", Json);
        after!.TrackId.ShouldBeNull();
        after.Laps.ShouldHaveSingleItem();
    }
}
