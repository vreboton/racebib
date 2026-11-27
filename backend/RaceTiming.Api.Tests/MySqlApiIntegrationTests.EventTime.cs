using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace RaceTiming.Api.Tests;

public sealed partial class MySqlApiIntegrationTests
{
    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task EventScheduleRoundTripsThroughMySqlWithoutChangingTheInstant()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account);
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, account));

        var payload = new
        {
            organizerId, name = "Trail rehearsal", slug = "trail-rehearsal", organizerName = "Test organizer",
            startDateUtc = "2026-11-27T12:00:00.123456+08:00", endDateUtc = "2026-11-27T17:00:00+08:00",
            timeZoneId = "Asia/Manila"
        };
        using var created = await client.PostAsJsonAsync("/api/v1/events", payload);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var path = "/api/v1/events/" + body.GetProperty("id").GetGuid();
        var saved = await client.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal("2026-11-27T04:00:00.123456Z", saved.GetProperty("startDate").GetString());
        Assert.Equal("2026-11-27T09:00:00Z", saved.GetProperty("endDate").GetString());

        // Changing display zone while resupplying the same instants retains the original stored precision.
        using var edited = await client.PutAsJsonAsync(path, payload with
        {
            startDateUtc = saved.GetProperty("startDate").GetString()!,
            endDateUtc = saved.GetProperty("endDate").GetString()!,
            timeZoneId = "America/New_York"
        });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/events?search=Trail%20rehearsal");
        var row = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("America/New_York", row.GetProperty("timeZoneId").GetString());
        Assert.Equal("2026-11-27T04:00:00.123456Z", row.GetProperty("startDate").GetString());
        Assert.Equal("2026-11-27T09:00:00Z", row.GetProperty("endDate").GetString());
    }
}
