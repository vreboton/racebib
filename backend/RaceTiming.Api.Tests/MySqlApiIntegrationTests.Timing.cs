using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RaceTiming.Api.Controllers;
using RaceTiming.Application;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;
using RaceTiming.Infrastructure.Persistence;
using Xunit;

namespace RaceTiming.Api.Tests;

public sealed partial class MySqlApiIntegrationTests
{
    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task TimingPreservesRepeatedVisitsAndRebuildsAcceptedSlotsUnderConcurrentSubmissions()
    {
        await using var data = new MySqlTestData(Services);
        var director = await data.CreateAccountAsync();
        var operatorAccount = await data.CreateAccountAsync();
        var secondOperator = await data.CreateAccountAsync(systemAdministrator: true);
        var organizer = await data.CreateOrganizerAsync(director, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizer);
        var otherEventId = await data.CreateEventAsync(organizer);
        using var manager = await CheckInClient(director);
        using var station = await CheckInClient(operatorAccount);
        using var second = await CheckInClient(secondOperator);
        var path = $"/api/v1/events/{eventId}/timing";

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var item = await db.Events.SingleAsync(x => x.Id == eventId);
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        item.StartDate = now.UtcDateTime.AddHours(-1);
        item.EndDate = now.UtcDateTime.AddDays(2);
        var course = new Course { EventId = eventId, Name = "Out and back", DistanceKm = 10 };
        var points = Enumerable.Range(1, 4).Select(i => new CourseCheckpoint
        {
            CourseId = course.Id, Code = $"P{i}", Name = $"Visit {i}", Sequence = i, DistanceFromStartKm = (i - 1) * 10m / 3m,
            IsStart = i == 1, IsFinish = i == 4
        }).ToArray();
        var race = new Race { EventId = eventId, CourseId = course.Id, Name = "Timing", DistanceKm = 10, StartAtUtc = now.UtcDateTime.AddMinutes(-30) };
        var runner = new Runner { OrganizerId = organizer, FirstName = "Synthetic", LastName = "Runner" };
        var registration = new Registration
        {
            EventId = eventId, RaceId = race.Id, RunnerId = runner.Id, OrganizerId = organizer,
            BibNumber = "0042", RegistrationStatus = RegistrationStatus.Confirmed
        };
        var wave = new StartWave { RaceId = race.Id, Name = "Wave 1", Sequence = 1, StartAtUtc = now.UtcDateTime.AddMinutes(-25) };
        registration.StartWaveId = wave.Id;
        db.AddRange(course, race, wave, runner, registration);
        db.AddRange(points);
        await db.SaveChangesAsync();

        try
        {
            var gate = await CreatedId(await manager.PostAsJsonAsync(path + "/checkpoints", new { name = "Gate", code = "G" }));
            var aid = await CreatedId(await manager.PostAsJsonAsync(path + "/checkpoints", new { name = "Aid", code = "A" }));
            foreach (var point in points)
            {
                var target = point.IsStart || point.IsFinish ? gate : aid;
                using var mapped = await manager.PutAsJsonAsync(path + $"/courses/{course.Id}/occurrences/{point.Id}/checkpoint", new { checkpointId = target });
                Assert.Equal(HttpStatusCode.NoContent, mapped.StatusCode);
            }

            var registrationsPath = $"/api/v1/events/{eventId}/registrations";
            var bibPath = registrationsPath + $"/{registration.Id}/bib";
            using var beforeArm = await manager.PutAsJsonAsync(bibPath, new { bibNumber = "0043", version = 1 });
            Assert.Equal(HttpStatusCode.NoContent, beforeArm.StatusCode);
            using var restoreBib = await manager.PutAsJsonAsync(bibPath, new { bibNumber = "0042", version = 2 });
            Assert.Equal(HttpStatusCode.NoContent, restoreBib.StatusCode);
            item.Status = EventStatus.Live;
            await db.SaveChangesAsync();
            using var arm = await manager.PostAsJsonAsync(path + $"/races/{race.Id}/arm", new { });
            Assert.Equal(HttpStatusCode.Created, arm.StatusCode);
            var setup = await manager.GetFromJsonAsync<JsonElement>(path + "/setup");
            Assert.Equal(2, setup.GetProperty("locations").GetArrayLength());
            Assert.Equal(4, setup.GetProperty("mappings").GetArrayLength());
            Assert.Single(setup.GetProperty("setups").EnumerateArray());
            var operatorDetails = await manager.GetFromJsonAsync<JsonElement>(path + "/setup/operator?email=" + Uri.EscapeDataString(operatorAccount.Email));
            Assert.Equal(operatorAccount.Id, operatorDetails.GetProperty("id").GetString());
            using var deniedSetup = await station.GetAsync(path + "/setup");
            Assert.Equal(HttpStatusCode.Forbidden, deniedSetup.StatusCode);
            var clockProbe = await station.GetFromJsonAsync<JsonElement>("/api/v1/me/timing-clock");
            Assert.InRange(clockProbe.GetProperty("serverUtc").GetDateTimeOffset(), now.AddSeconds(-10), DateTimeOffset.UtcNow.AddSeconds(1));
            using var frozenChange = await manager.PutAsJsonAsync(bibPath, new { bibNumber = "0043", version = 3 });
            Assert.Equal(HttpStatusCode.Conflict, frozenChange.StatusCode);
            using var frozenWave = await manager.PutAsJsonAsync(registrationsPath + $"/{registration.Id}",
                new { raceId = race.Id, runnerId = runner.Id, bibNumber = "0042", version = 3 });
            Assert.Equal(HttpStatusCode.Conflict, frozenWave.StatusCode);
            using var remap = await manager.PutAsJsonAsync(path + $"/courses/{course.Id}/occurrences/{points[1].Id}/checkpoint", new { checkpointId = gate });
            Assert.Equal(HttpStatusCode.Conflict, remap.StatusCode);
            using var gun = await manager.PostAsJsonAsync(path + $"/races/{race.Id}/gun-starts",
                new { gunAtUtc = now.AddMinutes(-20), reason = "Actual race start" });
            Assert.Equal(HttpStatusCode.Created, gun.StatusCode);

            using var waveGun = await manager.PostAsJsonAsync(path + $"/races/{race.Id}/gun-starts",
                new { waveId = wave.Id, gunAtUtc = now.AddMinutes(-19), reason = "Actual wave start" });
            Assert.Equal(HttpStatusCode.Created, waveGun.StatusCode);

            var lateRunner = new Runner { OrganizerId = organizer, FirstName = "Late", LastName = "Entrant" };
            var lateRegistration = new Registration
            {
                EventId = eventId, RaceId = race.Id, RunnerId = lateRunner.Id, OrganizerId = organizer,
                BibNumber = "0100", RegistrationStatus = RegistrationStatus.Confirmed, StartWaveId = wave.Id
            };
            db.AddRange(lateRunner, lateRegistration);
            await db.SaveChangesAsync();
            var amendmentPath = path + $"/races/{race.Id}/roster/{lateRegistration.Id}";
            var eligible = await manager.GetFromJsonAsync<JsonElement>(path + $"/setup/races/{race.Id}/late-registrations?bib=0100");
            Assert.Equal(lateRegistration.Id, eligible.GetProperty("id").GetGuid());
            using var amendment = await manager.PostAsJsonAsync(amendmentPath, new { reason = "Late entry approved" });
            Assert.Equal(HttpStatusCode.NoContent, amendment.StatusCode);
            using var repeatedAmendment = await manager.PostAsJsonAsync(amendmentPath, new { reason = "Repeat approval" });
            Assert.Equal(HttpStatusCode.Conflict, repeatedAmendment.StatusCode);
            Assert.Contains("Late entry approved", (await db.AuditRecords.SingleAsync(x =>
                x.Action == "Timing.RosterAmended" && x.TargetEntityId == lateRegistration.Id.ToString())).MetadataJson);

            var stationPath = path + $"/checkpoints/{aid}";
            using (var constraints = Services.CreateScope())
            {
                var constrainedDb = constraints.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
                constrainedDb.Add(new CheckpointAssignment
                {
                    EventId = otherEventId, CheckpointId = aid, UserId = operatorAccount.Id,
                    Generation = Guid.NewGuid(), Active = true
                });
                await Assert.ThrowsAsync<DbUpdateException>(() => constrainedDb.SaveChangesAsync());
            }
            var generation = await Grant(manager, stationPath, operatorAccount.Id);
            var device = Guid.NewGuid();
            using var forbidden = await second.GetAsync(stationPath + "/registrations?bib=0042");
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            var secondGeneration = await Grant(manager, stationPath, secondOperator.Id);
            using var otherEvent = await station.GetAsync($"/api/v1/events/{otherEventId}/timing/checkpoints/{aid}/registrations?bib=0042");
            Assert.Equal(HttpStatusCode.Forbidden, otherEvent.StatusCode);
            using var lookup = await station.GetAsync(stationPath + "/registrations?bib=0042");
            Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
            var match = await lookup.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2, match.GetProperty("occurrences").GetArrayLength());
            using var lateLookup = await station.GetAsync(stationPath + "/registrations?bib=0100");
            Assert.Equal(HttpStatusCode.OK, lateLookup.StatusCode);

            var gatePath = path + $"/checkpoints/{gate}";
            var gateGeneration = await Grant(manager, gatePath, operatorAccount.Id);
            var start = new TimingController.ObservationInput(Guid.NewGuid(), registration.Id, race.Id, "0042",
                points[0].Id, now.AddMinutes(-18), device, gateGeneration, TimingSource.Manual);
            var startReceipt = await Send(station, gatePath, Guid.NewGuid(), start);
            Assert.Equal("Accepted", startReceipt.Body.GetProperty("outcome").GetString());

            var lateVisit = new TimingController.ObservationInput(Guid.NewGuid(), registration.Id, race.Id, "0042",
                points[2].Id, now.AddMinutes(-1), device, generation, TimingSource.Scanner);
            var key = Guid.NewGuid();
            var retries = await Task.WhenAll(Send(station, stationPath, key, lateVisit), Send(station, stationPath, key, lateVisit));
            Assert.Contains(retries, x => x.Status == HttpStatusCode.Created);
            Assert.Contains(retries, x => x.Status == HttpStatusCode.OK);
            Assert.All(retries, x => Assert.Equal("Accepted", x.Body.GetProperty("outcome").GetString()));
            Assert.Equal(retries[0].Body.GetProperty("receivedAtUtc").GetString(), retries[1].Body.GetProperty("receivedAtUtc").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await Send(station, stationPath, key, lateVisit with { Source = TimingSource.Manual })).Status);

            var earlier = lateVisit with { ObservationId = Guid.NewGuid(), OccurrenceId = null, ObservedAtUtc = now.AddMinutes(-3) };
            var ambiguous = await Send(station, stationPath, Guid.NewGuid(), earlier);
            Assert.Equal("AmbiguousOccurrence", ambiguous.Body.GetProperty("outcome").GetString());
            var exceptions = await manager.GetFromJsonAsync<JsonElement>(path + "/control/exceptions?limit=1");
            Assert.Equal(1, exceptions.GetProperty("total").GetInt32());
            Assert.Equal(earlier.ObservationId, exceptions.GetProperty("items")[0].GetProperty("observationId").GetGuid());
            var emptyExceptions = await manager.GetFromJsonAsync<JsonElement>(path + "/control/exceptions?limit=1&offset=1");
            Assert.Equal(0, emptyExceptions.GetProperty("items").GetArrayLength());
            var preview = await manager.GetFromJsonAsync<JsonElement>(path + $"/control/observations/{earlier.ObservationId}/resolution");
            Assert.Equal(2, preview.GetProperty("choices").GetArrayLength());
            Assert.Equal(1, preview.GetProperty("decisions")[0].GetProperty("revision").GetInt32());
            var competing = earlier with { ObservationId = Guid.NewGuid(), OccurrenceId = points[1].Id,
                AssignmentGeneration = secondGeneration, DeviceId = Guid.NewGuid() };
            var confirmation = station.PostAsJsonAsync(stationPath + $"/observations/{earlier.ObservationId}/confirm",
                new { occurrenceId = points[1].Id, assignmentGeneration = generation, expectedRevision = 1 });
            var competition = Send(second, stationPath, Guid.NewGuid(), competing);
            using var confirmed = await confirmation;
            var competitor = await competition;
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
            Assert.Equal(HttpStatusCode.Created, competitor.Status);
            var confirmedBody = await confirmed.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(new[] { "Accepted", "SlotConflict" },
                new[] { confirmedBody.GetProperty("outcome").GetString(), competitor.Body.GetProperty("outcome").GetString() }.Order());

            var clock = lateVisit with { ObservationId = Guid.NewGuid(), ObservedAtUtc = now.AddHours(1) };
            var suspect = await Send(station, stationPath, Guid.NewGuid(), clock);
            Assert.Equal("SuspiciousClock", suspect.Body.GetProperty("outcome").GetString());
            Assert.Equal(clock.ObservedAtUtc, suspect.Body.GetProperty("observedAtUtc").GetDateTimeOffset());

            using var denyCorrection = await station.PostAsJsonAsync(path + $"/observations/{lateVisit.ObservationId}/resolve",
                new { expectedRevision = 1, reason = "Wrong visit" });
            Assert.Equal(HttpStatusCode.Forbidden, denyCorrection.StatusCode);
            using var invalidate = await manager.PostAsJsonAsync(path + $"/observations/{lateVisit.ObservationId}/resolve",
                new { expectedRevision = 1, reason = "Wrong visit" });
            Assert.Equal(HttpStatusCode.OK, invalidate.StatusCode);

            var observations = await db.Set<TimingObservation>().Where(x => x.EventId == eventId).ToListAsync();
            var observationIds = observations.Select(x => x.Id).ToArray();
            var decisions = await db.Set<TimingDecision>().Where(x => observationIds.Contains(x.ObservationId)).ToListAsync();
            var existing = await db.Set<AcceptedTiming>().Where(x => x.EventId == eventId).ToListAsync();
            var rebuilt = TimingProjection.Reconstruct(observations.OrderByDescending(x => x.Id), decisions.OrderByDescending(x => x.Revision));
            Assert.Equal(existing.OrderBy(x => x.OccurrenceId).Select(x => (x.OccurrenceId, x.ObservationId)),
                rebuilt.OrderBy(x => x.OccurrenceId).Select(x => (x.OccurrenceId, x.ObservationId)));
            Assert.Equal(2, existing.Count);
            Assert.Contains(existing, x => x.OccurrenceId == points[0].Id && x.ObservationId == start.ObservationId);
            Assert.Contains(existing, x => x.OccurrenceId == points[1].Id);
            Assert.Equal(5, observations.Count);
            var unresolved = confirmedBody.GetProperty("outcome").GetString() == "SlotConflict" ? earlier : competing;
            using var blockedSlot = await manager.PostAsJsonAsync(path + $"/observations/{unresolved.ObservationId}/resolve",
                new { occurrenceId = points[1].Id, expectedRevision = unresolved == earlier ? 2 : 1, reason = "Check occupied visit" });
            Assert.Equal(HttpStatusCode.Conflict, blockedSlot.StatusCode);

            var finishCandidate = start with { ObservationId = Guid.NewGuid(), OccurrenceId = null, ObservedAtUtc = now };
            var pendingFinish = await Send(station, gatePath, Guid.NewGuid(), finishCandidate);
            Assert.Equal("AmbiguousOccurrence", pendingFinish.Body.GetProperty("outcome").GetString());
            var finishAction = new { occurrenceId = points[3].Id, expectedRevision = 1, reason = "Confirmed finish visit" };
            var resolutions = await Task.WhenAll(
                manager.PostAsJsonAsync(path + $"/observations/{finishCandidate.ObservationId}/resolve", finishAction),
                manager.PostAsJsonAsync(path + $"/observations/{finishCandidate.ObservationId}/resolve", finishAction));
            try
            {
                Assert.Single(resolutions, x => x.StatusCode == HttpStatusCode.OK);
                var stale = Assert.Single(resolutions, x => x.StatusCode == HttpStatusCode.Conflict);
                Assert.Equal("stale_decision", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            }
            finally { foreach (var response in resolutions) response.Dispose(); }

            using var revoke = await manager.PutAsJsonAsync(stationPath + $"/assignments/{operatorAccount.Id}", new { active = false });
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(station, stationPath, key, lateVisit)).Status);
            Assert.NotEqual(generation, await Grant(manager, stationPath, operatorAccount.Id));
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(station, stationPath, key, lateVisit)).Status);
        }
        finally
        {
            await db.Set<AcceptedTiming>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<TimingDecision>().Where(x => db.Set<TimingObservation>().Any(o => o.EventId == eventId && o.Id == x.ObservationId)).ExecuteDeleteAsync();
            await db.Set<TimingObservation>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<OfficialGunDeclaration>().Where(x => x.RaceId == race.Id).ExecuteDeleteAsync();
            await db.Set<TimingRosterEntry>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<TimingWave>().Where(x => x.RaceId == race.Id).ExecuteDeleteAsync();
            await db.Set<TimingVisit>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<RaceTimingSetup>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<CheckpointAssignment>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<CheckpointMapping>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<PhysicalCheckpoint>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
        }
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task FrozenBibOverridesLegacyRosterDriftAndStaleAllocationPreviews()
    {
        await using var data = new MySqlTestData(Services);
        var account = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(account, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizerId);
        using var manager = await CheckInClient(account);
        using var second = await CheckInClient(account);
        var raceId = await SeedRegistrationRace(manager, eventId);
        var path = $"/api/v1/events/{eventId}/registrations";
        var bulk = path + "/bibs";
        var timing = $"/api/v1/events/{eventId}/timing";
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var race = await db.Races.SingleAsync(x => x.Id == raceId);
        var item = await db.Events.SingleAsync(x => x.Id == eventId);
        var original = BulkRunner(eventId, organizerId, raceId, "Original", 1, RegistrationStatus.Confirmed, "0043");
        var candidate = BulkRunner(eventId, organizerId, raceId, "Candidate", 2, RegistrationStatus.Confirmed);
        var unregistered = new Runner { OrganizerId = organizerId, FirstName = "Unregistered", LastName = "Candidate" };
        item.Status = EventStatus.Live;
        db.AddRange(original, candidate, unregistered);
        await db.SaveChangesAsync();

        const string csv = "First Name,Last Name,Name on Bib,Email,Race,Bib Number\nCsv,Candidate,,frozen-csv@example.test,,0042\n";
        var preview = await PreviewRegistrationCsv(manager, path, raceId, csv);
        Assert.True(preview.GetProperty("valid").GetBoolean());
        var allocation = new { start = 42, end = 42, width = 4, raceId };
        var bulkPreview = await BulkPreview(manager, bulk, allocation);
        Assert.Equal("0042", bulkPreview.GetProperty("rows").EnumerateArray()
            .Single(x => x.GetProperty("registrationId").GetGuid() == candidate.Id).GetProperty("proposedBib").GetString());

        // Reproduce data possible before this guard: the registration desk and timing roster disagree.
        db.Add(new RaceTimingSetup
        {
            RaceId = raceId, EventId = eventId, CourseId = race.CourseId, TimeZoneId = item.TimeZoneId,
            WindowStartUtc = item.StartDate, WindowEndUtc = item.EndDate,
            ScheduledGunUtc = race.StartAtUtc, ArmedAtUtc = DateTime.UtcNow
        });
        db.Add(new TimingRosterEntry
        {
            EventId = eventId, RaceId = raceId, RegistrationId = original.Id, BibNumber = "0042"
        });
        await db.SaveChangesAsync();
        try
        {
            var before = await BulkState(db, eventId);
            using var form = ImportForm(raceId, csv, token: preview.GetProperty("token").GetString());
            var attempts = await Task.WhenAll(
                manager.PostAsync(path + "/import/confirm", form),
                ConfirmBulk(second, bulk, bulkPreview),
                manager.PutAsJsonAsync(path + $"/{candidate.Id}/bib", new { bibNumber = "0042", version = 1 }),
                second.PostAsJsonAsync(path, new { raceId, runnerId = unregistered.Id, bibNumber = "0042" }));
            foreach (var attempt in attempts)
            {
                using (attempt) Assert.Equal(HttpStatusCode.Conflict, attempt.StatusCode);
            }
            Assert.Equal(before, await BulkState(db, eventId));
            Assert.Empty(await db.Set<TimingObservation>().Where(x => x.EventId == eventId).ToListAsync());
            Assert.False((await PreviewRegistrationCsv(manager, path, raceId, csv)).GetProperty("valid").GetBoolean());

            // Simulate an already-issued duplicate on the old code to exercise amendment independently.
            candidate.BibNumber = "0042";
            await db.SaveChangesAsync();
            var amendmentPath = timing + $"/races/{raceId}/roster/{candidate.Id}";
            using var denied = await manager.PostAsJsonAsync(amendmentPath, new { reason = "Late entry" });
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
            Assert.Equal(original.Id, (await db.Set<TimingRosterEntry>().SingleAsync(x => x.EventId == eventId)).RegistrationId);

            candidate.BibNumber = "0050";
            await db.SaveChangesAsync();
            var amendmentTask = manager.PostAsJsonAsync(amendmentPath, new { reason = "Late entry" });
            var bibTask = second.PutAsJsonAsync(path + $"/{candidate.Id}/bib", new { bibNumber = "0051", version = 1 });
            using var amendment = await amendmentTask;
            using var bibChange = await bibTask;
            Assert.Equal(HttpStatusCode.NoContent, amendment.StatusCode);
            Assert.Contains(bibChange.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict });
            var reserved = await db.Set<TimingRosterEntry>().SingleAsync(x => x.EventId == eventId && x.RegistrationId == candidate.Id);
            var current = await db.Registrations.AsNoTracking().SingleAsync(x => x.Id == candidate.Id);
            Assert.Equal(current.BibNumber, reserved.BibNumber);
        }
        finally
        {
            await db.Set<TimingRosterEntry>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<RaceTimingSetup>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
        }
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task TimingLookupAndCaptureRejectConflictingLiveBibOwnership()
    {
        await using var data = new MySqlTestData(Services);
        var director = await data.CreateAccountAsync();
        var operatorAccount = await data.CreateAccountAsync();
        var organizerId = await data.CreateOrganizerAsync(director, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizerId);
        var otherEventId = await data.CreateEventAsync(organizerId);
        using var manager = await CheckInClient(director);
        using var station = await CheckInClient(operatorAccount);
        var raceId = await SeedRegistrationRace(manager, eventId);
        var otherRaceId = await SeedRegistrationRace(manager, otherEventId);
        var path = $"/api/v1/events/{eventId}";
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var item = await db.Events.SingleAsync(x => x.Id == eventId);
        var race = await db.Races.Include(x => x.Course).ThenInclude(x => x.Checkpoints).SingleAsync(x => x.Id == raceId);
        var frozenRunner = new Runner { OrganizerId = organizerId, FirstName = "Frozen", LastName = "Owner" };
        var liveRunner = new Runner { OrganizerId = organizerId, FirstName = "Different", LastName = "Owner" };
        db.AddRange(frozenRunner, liveRunner);
        await db.SaveChangesAsync();

        var owner = await CreatedId(await manager.PostAsJsonAsync(path + "/registrations",
            new { raceId, runnerId = frozenRunner.Id, bibNumber = "0042", status = RegistrationStatus.Confirmed }));
        var otherRegistration = await CreatedId(await manager.PostAsJsonAsync(path + "/registrations",
            new { raceId, runnerId = liveRunner.Id, bibNumber = "0050", status = RegistrationStatus.Confirmed }));
        await CreatedId(await manager.PostAsJsonAsync($"/api/v1/events/{otherEventId}/registrations",
            new { raceId = otherRaceId, runnerId = liveRunner.Id, bibNumber = "0042", status = RegistrationStatus.Confirmed }));

        var timing = path + "/timing";
        try
        {
            var location = await CreatedId(await manager.PostAsJsonAsync(timing + "/checkpoints", new { name = "Gate", code = "G" }));
            foreach (var point in race.Course.Checkpoints)
            {
                using var mapping = await manager.PutAsJsonAsync(timing + $"/courses/{race.CourseId}/occurrences/{point.Id}/checkpoint",
                    new { checkpointId = location });
                Assert.Equal(HttpStatusCode.NoContent, mapping.StatusCode);
            }

            var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            item.StartDate = now.UtcDateTime.AddHours(-1);
            item.EndDate = now.UtcDateTime.AddHours(1);
            item.Status = EventStatus.Live;
            await db.SaveChangesAsync();
            using var arm = await manager.PostAsJsonAsync(timing + $"/races/{raceId}/arm", new { });
            Assert.Equal(HttpStatusCode.Created, arm.StatusCode);

            var stationPath = timing + $"/checkpoints/{location}";
            var generation = await Grant(manager, stationPath, operatorAccount.Id);
            var lookupPath = stationPath + "/registrations?bib=0042";
            using var firstLookup = await station.GetAsync(lookupPath);
            Assert.Equal(HttpStatusCode.OK, firstLookup.StatusCode);
            Assert.Equal(owner, (await firstLookup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("registrationId").GetGuid());

            var input = new TimingController.ObservationInput(Guid.NewGuid(), owner, raceId, "0042",
                race.Course.Checkpoints.Single(x => x.IsStart).Id, now, Guid.NewGuid(), generation, TimingSource.Manual);
            var key = Guid.NewGuid();

            // Reproduce legacy drift after the successful lookup. The public writes now prevent this state.
            var liveOwner = await db.Registrations.SingleAsync(x => x.Id == owner);
            var conflictingOwner = await db.Registrations.SingleAsync(x => x.Id == otherRegistration);
            liveOwner.BibNumber = null;
            await db.SaveChangesAsync();
            conflictingOwner.BibNumber = "0042";
            await db.SaveChangesAsync();

            using var conflictLookup = await station.GetAsync(lookupPath);
            Assert.Equal(HttpStatusCode.Conflict, conflictLookup.StatusCode);
            Assert.Equal("timing_bib_identity_conflict",
                (await conflictLookup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            var conflict = await Send(station, stationPath, key, input);
            Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
            var bibExceptions = await manager.GetFromJsonAsync<JsonElement>(timing + "/control/exceptions?kind=bib");
            Assert.Equal(1, bibExceptions.GetProperty("total").GetInt32());
            Assert.Equal(owner, bibExceptions.GetProperty("items")[0].GetProperty("registrationId").GetGuid());
            Assert.Equal("timing_bib_identity_conflict", conflict.Body.GetProperty("code").GetString());
            Assert.False(await db.Set<TimingObservation>().AnyAsync(x => x.EventId == eventId));
            Assert.False(await db.Set<TimingDecision>().AnyAsync(x => x.ObservationId == input.ObservationId));
            Assert.False(await db.Set<AcceptedTiming>().AnyAsync(x => x.EventId == eventId));

            conflictingOwner.BibNumber = "0050";
            await db.SaveChangesAsync();
            using var historicalLookup = await station.GetAsync(lookupPath);
            Assert.Equal(HttpStatusCode.OK, historicalLookup.StatusCode);
            Assert.Equal(owner, (await historicalLookup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("registrationId").GetGuid());
            var accepted = await Send(station, stationPath, key, input);
            Assert.Equal(HttpStatusCode.Created, accepted.Status);
            Assert.Equal("Accepted", accepted.Body.GetProperty("outcome").GetString());
            Assert.Equal(owner, accepted.Body.GetProperty("registrationId").GetGuid());

            // A replay still returns the immutable receipt while new evidence fails closed.
            conflictingOwner.BibNumber = "0042";
            await db.SaveChangesAsync();
            var replay = await Send(station, stationPath, key, input);
            Assert.Equal(HttpStatusCode.OK, replay.Status);
            Assert.Equal(accepted.Body.GetProperty("receivedAtUtc").GetString(), replay.Body.GetProperty("receivedAtUtc").GetString());
            using var revoke = await manager.PutAsJsonAsync(stationPath + $"/assignments/{operatorAccount.Id}", new { active = false });
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(station, stationPath, key, input)).Status);
        }
        finally
        {
            await db.Set<AcceptedTiming>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<TimingDecision>().Where(x => db.Set<TimingObservation>().Any(o => o.EventId == eventId && o.Id == x.ObservationId)).ExecuteDeleteAsync();
            await db.Set<TimingObservation>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<TimingRosterEntry>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<TimingVisit>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<RaceTimingSetup>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<CheckpointAssignment>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<CheckpointMapping>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<PhysicalCheckpoint>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
        }
    }

    [MySqlIntegrationFact]
    [Trait("Category", "MySqlIntegration")]
    public async Task TimingResultsUseScopedSnapshotsAndRecalculateFromCurrentEvidence()
    {
        await using var data = new MySqlTestData(Services);
        var director = await data.CreateAccountAsync();
        var operatorAccount = await data.CreateAccountAsync(systemAdministrator: true);
        var outside = await data.CreateAccountAsync();
        var organizer = await data.CreateOrganizerAsync(director, OrganizerRole.RaceDirector);
        await data.CreateOrganizerAsync(outside, OrganizerRole.RaceDirector);
        var eventId = await data.CreateEventAsync(organizer);
        var otherEventId = await data.CreateEventAsync(organizer);
        using var manager = await CheckInClient(director);
        using var station = await CheckInClient(operatorAccount);
        using var outsider = await CheckInClient(outside);
        var raceId = await SeedRegistrationRace(manager, eventId);
        var path = $"/api/v1/events/{eventId}/timing";
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaceTimingDbContext>();
        var item = await db.Events.SingleAsync(x => x.Id == eventId);
        var race = await db.Races.Include(x => x.Course).ThenInclude(x => x.Checkpoints).SingleAsync(x => x.Id == raceId);
        var runner = BulkRunner(eventId, organizer, raceId, "Result", 1, RegistrationStatus.Confirmed, "0042");
        db.Add(runner);
        var finishAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        item.StartDate = finishAt.UtcDateTime.AddDays(-2);
        item.EndDate = finishAt.UtcDateTime.AddDays(1);
        item.Status = EventStatus.Live;
        race.CutoffDurationSeconds = 300;
        await db.SaveChangesAsync();

        try
        {
            var stationId = await CreatedId(await manager.PostAsJsonAsync(path + "/checkpoints", new { name = "Gate", code = "G" }));
            foreach (var visit in race.Course.Checkpoints)
            {
                using var mapped = await manager.PutAsJsonAsync(path + $"/courses/{race.CourseId}/occurrences/{visit.Id}/checkpoint",
                    new { checkpointId = stationId });
                Assert.Equal(HttpStatusCode.NoContent, mapped.StatusCode);
            }

            using var armed = await manager.PostAsJsonAsync(path + $"/races/{raceId}/arm", new { });
            Assert.Equal(HttpStatusCode.Created, armed.StatusCode);
            var stationPath = path + $"/checkpoints/{stationId}";
            var generation = await Grant(manager, stationPath, operatorAccount.Id);
            var raceResults = path + $"/races/{raceId}/results";
            var individual = raceResults + $"/{runner.Id}";
            var control = path + $"/control/races/{raceId}/runners";
            var classify = control + $"/{runner.Id}/classification";
            using var invalidFinish = await manager.PostAsJsonAsync(classify,
                new { classification = OfficialClassification.Finished, expectedRevision = 0, reason = "Finish not recorded" });
            Assert.Equal(HttpStatusCode.Conflict, invalidFinish.StatusCode);
            using var noReason = await manager.PostAsJsonAsync(classify,
                new { classification = OfficialClassification.Dnf, expectedRevision = 0, reason = "" });
            Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
            using var operatorDenied = await station.GetAsync(path + "/control");
            Assert.Equal(HttpStatusCode.Forbidden, operatorDenied.StatusCode);
            using var outsideDenied = await outsider.GetAsync(path + "/control/exceptions");
            Assert.Equal(HttpStatusCode.Forbidden, outsideDenied.StatusCode);
            using var wrongClassify = await manager.PostAsJsonAsync($"/api/v1/events/{otherEventId}/timing/control/races/{raceId}/runners/{runner.Id}/classification",
                new { classification = OfficialClassification.Dnf, expectedRevision = 0, reason = "Wrong event" });
            Assert.Equal(HttpStatusCode.NotFound, wrongClassify.StatusCode);

            using var deniedOperator = await station.GetAsync(individual);
            Assert.Equal(HttpStatusCode.Forbidden, deniedOperator.StatusCode);
            using var deniedOutsider = await outsider.GetAsync(raceResults);
            Assert.Equal(HttpStatusCode.Forbidden, deniedOutsider.StatusCode);
            using var wrongEvent = await manager.GetAsync($"/api/v1/events/{otherEventId}/timing/races/{raceId}/results");
            Assert.Equal(HttpStatusCode.NotFound, wrongEvent.StatusCode);
            using var badPage = await manager.GetAsync(raceResults + "?limit=101");
            Assert.Equal(HttpStatusCode.BadRequest, badPage.StatusCode);

            var finish = new TimingController.ObservationInput(Guid.NewGuid(), runner.Id, raceId, "0042",
                race.Course.Checkpoints.Single(x => x.IsFinish).Id, finishAt, Guid.NewGuid(), generation, TimingSource.Manual);
            var finishReceipt = await Send(station, stationPath, Guid.NewGuid(), finish);
            Assert.Equal(HttpStatusCode.Created, finishReceipt.Status);
            Assert.Equal("Accepted", finishReceipt.Body.GetProperty("outcome").GetString());
            async Task<JsonElement> ReadIndividual() =>
                (await manager.GetFromJsonAsync<JsonElement>(individual)).GetProperty("result");

            var initial = await ReadIndividual();
            Assert.True(initial.GetProperty("readiness").GetProperty("missingOfficialGun").GetBoolean());
            Assert.True(initial.GetProperty("readiness").GetProperty("missingIndividualStart").GetBoolean());
            Assert.Equal("unknown", initial.GetProperty("raceCutoff").GetProperty("status").GetString());
            Assert.Equal("missing_official_gun", initial.GetProperty("raceCutoff").GetProperty("unavailableReason").GetString());
            Assert.Equal("missing_individual_start",
                initial.GetProperty("provisionalFinish").GetProperty("netElapsedUnavailableReason").GetString());

            var gunPath = path + $"/races/{raceId}/gun-starts";
            var gun = await CreatedId(await manager.PostAsJsonAsync(gunPath,
                new { gunAtUtc = finishAt.AddSeconds(-300), reason = "Observed race gun" }));
            Assert.Equal("within_limit", (await ReadIndividual()).GetProperty("raceCutoff").GetProperty("status").GetString());

            var start = finish with
            {
                ObservationId = Guid.NewGuid(), OccurrenceId = race.Course.Checkpoints.Single(x => x.IsStart).Id,
                ObservedAtUtc = finishAt.AddSeconds(-120).AddTicks(10)
            };
            var lateWrite = Send(station, stationPath, Guid.NewGuid(), start);
            var reads = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ReadIndividual()));
            Assert.Equal(HttpStatusCode.Created, (await lateWrite).Status);
            foreach (var read in reads)
                AssertTimingSnapshot(read, read.GetProperty("individualStartAtUtc").ValueKind == JsonValueKind.String);
            AssertTimingSnapshot(await ReadIndividual(), true);
            var commands = await Task.WhenAll(
                manager.PostAsJsonAsync(classify, new { classification = OfficialClassification.Finished, expectedRevision = 0, reason = "Finish reviewed" }),
                manager.PostAsJsonAsync(classify, new { classification = OfficialClassification.Finished, expectedRevision = 0, reason = "Concurrent review" }));
            try
            {
                Assert.Single(commands, x => x.StatusCode == HttpStatusCode.Created);
                Assert.Single(commands, x => x.StatusCode == HttpStatusCode.Conflict);
            }
            finally { foreach (var command in commands) command.Dispose(); }
            var classified = (await manager.GetFromJsonAsync<JsonElement>(control)).GetProperty("items")[0];
            Assert.Equal("Finished", classified.GetProperty("classification").GetString());
            Assert.False(classified.GetProperty("needsReview").GetBoolean());

            await CreatedId(await manager.PostAsJsonAsync(gunPath,
                new { gunAtUtc = finishAt.AddSeconds(-300).AddTicks(-10), expectedDeclarationId = gun, reason = "Corrected gun" }));
            var corrected = await ReadIndividual();
            Assert.Equal(300_000_001L, corrected.GetProperty("provisionalFinish").GetProperty("gunElapsedMicroseconds").GetInt64());
            Assert.Equal("over_limit", corrected.GetProperty("raceCutoff").GetProperty("status").GetString());
            var cutoffWarnings = (await manager.GetFromJsonAsync<JsonElement>(control)).GetProperty("items")[0];
            Assert.Contains(cutoffWarnings.GetProperty("warnings").EnumerateArray(), x => x.GetString() == "calculated_cutoff_exceeded");
            Assert.Equal("Finished", cutoffWarnings.GetProperty("classification").GetString());

            var invalidate = manager.PostAsJsonAsync(path + $"/observations/{start.ObservationId}/resolve",
                new { expectedRevision = 1, reason = "Invalid individual start" });
            var duringCorrection = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ReadIndividual()));
            using var invalidated = await invalidate;
            Assert.Equal(HttpStatusCode.OK, invalidated.StatusCode);
            foreach (var read in duringCorrection)
                AssertTimingSnapshot(read, read.GetProperty("individualStartAtUtc").ValueKind == JsonValueKind.String);
            var page = await manager.GetFromJsonAsync<JsonElement>(raceResults + "?offset=0&limit=1");
            Assert.Equal(1, page.GetProperty("total").GetInt32());
            AssertTimingSnapshot(page.GetProperty("items")[0], false);

            var replacement = start with { ObservationId = Guid.NewGuid(), ObservedAtUtc = finishAt.AddSeconds(-119) };
            var replacementReceipt = await Send(station, stationPath, Guid.NewGuid(), replacement);
            Assert.Equal(HttpStatusCode.Created, replacementReceipt.Status);
            Assert.Equal("Accepted", replacementReceipt.Body.GetProperty("outcome").GetString());
            var afterReplacement = await ReadIndividual();
            Assert.Equal(replacement.ObservationId, afterReplacement.GetProperty("visits")[0].GetProperty("observationId").GetGuid());
            Assert.Equal(119_000_000L, afterReplacement.GetProperty("provisionalFinish").GetProperty("netElapsedMicroseconds").GetInt64());
            var history = await manager.GetFromJsonAsync<JsonElement>(path + $"/observations/{start.ObservationId}/history");
            Assert.Equal(2, history.GetProperty("decisions").GetArrayLength());
            var changed = (await manager.GetFromJsonAsync<JsonElement>(control)).GetProperty("items")[0];
            Assert.Equal("Finished", changed.GetProperty("classification").GetString());
            Assert.True(changed.GetProperty("needsReview").GetBoolean());
            using var reversed = await manager.PostAsJsonAsync(classify,
                new { classification = OfficialClassification.Pending, expectedRevision = 1, reason = "Reopen for review" });
            Assert.Equal(HttpStatusCode.Created, reversed.StatusCode);
            var auditPage = await manager.GetFromJsonAsync<JsonElement>(path + "/control/audit?limit=1");
            Assert.Equal(2, auditPage.GetProperty("total").GetInt32());
            var reversal = auditPage.GetProperty("items")[0];
            Assert.Equal("Finished", reversal.GetProperty("previousClassification").GetString());
            Assert.Equal("Pending", reversal.GetProperty("classification").GetString());
        }
        finally
        {
            await db.Set<OfficialRunnerDecision>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<AcceptedTiming>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<TimingDecision>().Where(x => db.Set<TimingObservation>().Any(o => o.EventId == eventId && o.Id == x.ObservationId)).ExecuteDeleteAsync();
            await db.Set<TimingObservation>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<OfficialGunDeclaration>().Where(x => x.RaceId == raceId && x.SupersedesId != null).ExecuteDeleteAsync();
            await db.Set<OfficialGunDeclaration>().Where(x => x.RaceId == raceId).ExecuteDeleteAsync();
            await db.Set<TimingRosterEntry>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<TimingVisit>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<RaceTimingSetup>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<CheckpointAssignment>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<CheckpointMapping>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
            await db.Set<PhysicalCheckpoint>().Where(x => x.EventId == eventId).ExecuteDeleteAsync();
        }
    }

    private static void AssertTimingSnapshot(JsonElement result, bool hasStart)
    {
        Assert.Equal(!hasStart, result.GetProperty("readiness").GetProperty("missingIndividualStart").GetBoolean());
        var visits = result.GetProperty("visits");
        Assert.Equal(hasStart, visits[0].GetProperty("crossingAtUtc").ValueKind == JsonValueKind.String);
        var segment = visits[1].GetProperty("segmentMicroseconds");
        var net = result.GetProperty("provisionalFinish").GetProperty("netElapsedMicroseconds");
        if (hasStart)
        {
            Assert.Equal(119_999_999L, net.GetInt64());
            Assert.Equal(net.GetInt64(), segment.GetInt64());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, net.ValueKind);
            Assert.Equal(JsonValueKind.Null, segment.ValueKind);
        }
    }

    private static async Task<Guid> Grant(HttpClient client, string path, string userId)
    {
        using var response = await client.PutAsJsonAsync(path + $"/assignments/{userId}", new { active = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("generation").GetGuid();
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Send(
        HttpClient client, string path, Guid key, TimingController.ObservationInput input)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path + "/observations") { Content = JsonContent.Create(input) };
        request.Headers.Add("Idempotency-Key", key.ToString());
        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        var body = string.IsNullOrWhiteSpace(payload) ? default : JsonSerializer.Deserialize<JsonElement>(payload);
        return (response.StatusCode, body);
    }
}
