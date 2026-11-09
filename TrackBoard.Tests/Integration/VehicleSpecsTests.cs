using System.Net;
using Shouldly;

namespace TrackBoard.Tests.Integration;

/// <summary>The lookup's routing and gating. What it answers is covered by the client's unit tests.</summary>
public class VehicleSpecsTests(TrackBoardApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task The_lookup_needs_a_signed_in_driver()
    {
        var response = await CreateClient().GetAsync("/api/v1/vehicle-specs/cars/makes");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Without_a_catalogue_key_the_lookup_says_it_is_unavailable()
    {
        var client = CreateClient();
        Authenticate(client, (await RegisterAsync(client)).AccessToken);

        var response = await client.GetAsync("/api/v1/vehicle-specs/motorcycles?make=Yamaha&model=FZ8");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task A_lookup_without_a_make_is_rejected()
    {
        var client = CreateClient();
        Authenticate(client, (await RegisterAsync(client)).AccessToken);

        var response = await client.GetAsync("/api/v1/vehicle-specs/cars/models");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
