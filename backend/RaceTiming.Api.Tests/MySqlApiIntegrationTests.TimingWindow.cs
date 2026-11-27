using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Persistence;
using Xunit;

namespace RaceTiming.Api.Tests;

public sealed partial class MySqlApiIntegrationTests
{
    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task TimingWindowArmingRejectsInvalidSchedulesAndPreservesTheFrozenWindowOnEdit()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizer = await data.CreateOrganizerAsync(account);
        var eventId = await data.CreateEventAsync(organizer);
        using var manager = await CheckInClient(account);
        var raceId = await SeedRegistrationRace(manager, eventId);
        var events = $"/api/v1/events/{eventId}";
        var timing = events + "/timing";
        var arm = timing + $"/races/{raceId}/arm";
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var item = await db.Events.SingleAsync(x => x.Id == eventId);
        var race = await db.Races.Include(x => x.Course).ThenInclude(x => x.Checkpoints).SingleAsync(x => x.Id == raceId);
        var station = await CreatedId(await manager.PostAsJsonAsync(timing + "/checkpoints", new { name = "Gate", code = "G" }));
        try
        {
            foreach (var visit in race.Course.Checkpoints)
            {
                using var mapped = await manager.PutAsJsonAsync(timing + $"/courses/{race.CourseId}/occurrences/{visit.Id}/checkpoint",
                    new { checkpointId = station });
                Assert.Equal(HttpStatusCode.NoContent, mapped.StatusCode);
            }

            var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime;
            item.Status = EventStatus.Ready;
            item.TimeZoneId = "Asia/Manila";
            item.StartDate = now.AddHours(-2);
            item.EndDate = now.AddHours(-1);
            await ExpectRejection("expired_timing_window");
            item.StartDate = now;
            item.EndDate = now;
            await ExpectRejection("invalid_timing_window");
            item.EndDate = now.AddMinutes(-1);
            await ExpectRejection("invalid_timing_window");
            var start = now.AddHours(1);
            var end = start.AddHours(4);
            item.StartDate = start;
            item.EndDate = end;
            race.StartAtUtc = start.AddSeconds(-1);
            await ExpectRejection("race_outside_timing_window");
            race.StartAtUtc = start;
            var wave = new StartWave { RaceId = race.Id, Name = "Wave A", Sequence = 1, StartAtUtc = end.AddSeconds(1) };
            db.Add(wave);
            await ExpectRejection("wave_outside_timing_window");
            wave.StartAtUtc = end;
            await db.SaveChangesAsync();
            using var armed = await manager.PostAsJsonAsync(arm, new { });
            Assert.Equal(HttpStatusCode.Created, armed.StatusCode);
            var initial = await manager.GetFromJsonAsync<JsonElement>(timing + "/setup");
            var initialWindow = Assert.Single(initial.GetProperty("setups").EnumerateArray());
            Assert.Equal("Upcoming", initialWindow.GetProperty("windowStatus").GetString());
            Assert.False(initialWindow.GetProperty("differsFromEventSchedule").GetBoolean());

            using var edited = await manager.PutAsJsonAsync(events, new
            {
                item.Name, item.Slug, item.OrganizerName, item.TimeZoneId,
                startDateUtc = start.AddMinutes(-30), endDateUtc = end.AddMinutes(30)
            });
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
            Assert.True((await edited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasArmedTiming").GetBoolean());
            var updated = await manager.GetFromJsonAsync<JsonElement>(timing + "/setup");
            var window = Assert.Single(updated.GetProperty("setups").EnumerateArray());
            Assert.Equal(start, window.GetProperty("windowStartUtc").GetDateTime());
            Assert.Equal(end, window.GetProperty("windowEndUtc").GetDateTime());
            Assert.Equal("Asia/Manila", updated.GetProperty("eventSchedule").GetProperty("timeZoneId").GetString());
            Assert.True(window.GetProperty("differsFromEventSchedule").GetBoolean());
        }
        finally
        {
            await db.Set<TimingWave>().Where(x => x.RaceId == raceId).ExecuteDeleteAsync();
            await db.Set<TimingVisit>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<RaceTimingSetup>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<CheckpointMapping>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<PhysicalCheckpoint>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
        }

        async Task ExpectRejection(string code)
        {
            await db.SaveChangesAsync();
            using var response = await manager.PostAsJsonAsync(arm, new { });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            Assert.False(await db.Set<RaceTimingSetup>().AnyAsync(x => x.RaceId == raceId));
        }
    }
}
