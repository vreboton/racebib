using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RaceTiming.Domain.Entities;
using RaceTiming.Domain.Enums;

namespace RaceTiming.Api.Controllers;

public sealed partial class RaceConfigurationController
{
    public sealed record CourseInput(string Name, string? Description, decimal DistanceKm, int? ElevationGainMeters);
    public sealed record CheckpointInput(Guid? Id, string Name, string Code, int Sequence, decimal DistanceFromStartKm,
        int? ElevationMeters, bool IsMandatory, bool IsStart, bool IsFinish, bool HasCutoff,
        int? CutoffDurationSeconds, string? Description);

    [HttpPost("courses")]
    public Task<IActionResult> CreateCourse(Guid eventId, CourseInput input, CancellationToken ct) =>
        SaveCourse(eventId, null, input, null, ct);

    [HttpPut("courses/{courseId:guid}")]
    public Task<IActionResult> UpdateCourse(Guid eventId, Guid courseId, CourseInput input, CancellationToken ct,
        [FromQuery] string? registrationConfirmation = null) =>
        SaveCourse(eventId, courseId, input, registrationConfirmation, ct);

    [HttpDelete("courses/{courseId:guid}")]
    public Task<IActionResult> DeleteCourse(Guid eventId, Guid courseId, CancellationToken ct) =>
        WithEvent(eventId, true, ct, async item =>
        {
            var course = await FindCourse(eventId, courseId, ct);
            if (course is null) return NotFound();
            if (course.Status != RecordStatus.Inactive || await HistoricalCourses(eventId).ContainsAsync(courseId, ct))
                return ConflictProblem("Retire the latest Route Revision before permanently deleting the Route.");
            if (await ProtectedCourseFamilies(eventId).ContainsAsync(courseId, ct))
                return ConflictProblem("A Category references this Route or one of its revisions. The Route must be kept to preserve those records.");

            var revisions = await CourseFamily(course).Include(x => x.Checkpoints).ToListAsync(ct);
            db.CourseCheckpoints.RemoveRange(revisions.SelectMany(x => x.Checkpoints));
            db.Courses.RemoveRange(revisions);
            audit?.Record(User, "Course.Deleted", "Course", courseId.ToString(), item.OrganizerId);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException error) when (error.GetBaseException() is MySqlConnector.MySqlException { Number: 1451 })
            {
                return ConflictProblem("This Route, a revision, or a Route Checkpoint is still referenced and cannot be permanently deleted.");
            }
            return NoContent();
        }, allowLocked: true);

    public sealed record CourseStatusInput(RecordStatus? Status);

    [HttpPatch("courses/{courseId:guid}/status")]
    public Task<IActionResult> ChangeCourseStatus(Guid eventId, Guid courseId, CourseStatusInput input, CancellationToken ct) =>
        WithEvent(eventId, true, ct, async _ =>
        {
            var course = await db.Courses.SingleOrDefaultAsync(x => x.EventId == eventId && x.Id == courseId, ct);
            if (course is null) return NotFound();
            if (input.Status is not (RecordStatus.Active or RecordStatus.Inactive))
                return Invalid("Choose Active or Inactive for the Route status.");
            if (course.Status == input.Status) return NoContent();
            if (input.Status == RecordStatus.Inactive && await ActiveCourseRaces(eventId).AnyAsync(x => x.CourseId == courseId, ct))
                return ConflictProblem("An active Category uses this Route. Retire the Category or switch it to an eligible Route Revision before retiring this revision.");

            course.Status = input.Status.Value;
            await db.SaveChangesAsync(ct);
            return NoContent();
        });

    private Task<IActionResult> SaveCourse(Guid eventId, Guid? courseId, CourseInput input,
        string? confirmation, CancellationToken ct) =>
        WithEvent(eventId, true, ct, async item =>
        {
            if (!ValidText(input.Name, 160) || input.DistanceKm <= 0 || !FitsDistance(input.DistanceKm) || input.ElevationGainMeters < 0)
                return Invalid("Provide a Route name, a positive distance with at most three decimal places and nonnegative elevation gain.");
            var course = courseId is null ? new Course { EventId = eventId, Name = input.Name.Trim() }
                : await FindCourse(eventId, courseId.Value, ct);
            if (course is null) return NotFound();
            var races = courseId.HasValue ? await ReferencingRaces(course, ct) : [];
            var historical = courseId.HasValue && await HistoricalCourses(eventId).ContainsAsync(course.Id, ct);
            if (EditBlock(course, historical, races.Any(IsActiveRace), item.Status) is { } block)
                return ConflictProblem(CourseEditMessage(block));
            var name = input.Name.Trim();
            var description = input.Description?.Trim();
            if (courseId.HasValue && course.Name == name && course.Description == description &&
                course.DistanceKm == input.DistanceKm && course.ElevationGainMeters == input.ElevationGainMeters)
                return Ok(new { course.Id });
            var registrations = await RegistrationSnapshots(AffectedRegistrations(course)).ToListAsync(ct);
            if (CheckConfirmation(course.Id, registrations, confirmation) is { } conflict) return conflict;
            var revise = NeedsRevision(historical, races, registrations.Count);
            if (courseId.HasValue && course.Name != name &&
                (revise || course.Version > 1 || await db.Courses.AnyAsync(x =>
                    x.EventId == eventId && x.Id != course.Id && x.Name == course.Name, ct)))
                return ConflictProblem("Keep the Route name to save this revision. Duplicate the Route to use a different name.");
            if ((courseId is null || course.Name != name) && await db.Courses.AnyAsync(x =>
                x.EventId == eventId && x.Id != course.Id && x.Name == name, ct))
                return ConflictProblem("A Route with this name already exists in the event.");
            var distanceChanged = course.DistanceKm != input.DistanceKm;
            if (distanceChanged || races.Count > 0)
            {
                var route = course.Checkpoints.OrderBy(x => x.Sequence).Select(CheckpointValue)
                    .Select(x => distanceChanged && x.IsFinish ? x with { DistanceFromStartKm = input.DistanceKm } : x)
                    .ToArray();
                if (ValidateRoute(input.DistanceKm, route, races.Count > 0) is { } error)
                    return Invalid(error);
            }

            if (revise)
            {
                if (await ValidateDraftAssignments(item, course, ct) is { } invalid) return Invalid(invalid);
                course = await CreateNextRevision(course, races, ct);
            }
            if (distanceChanged)
            {
                foreach (var finish in course.Checkpoints.Where(x => x.IsFinish))
                    finish.DistanceFromStartKm = input.DistanceKm;
            }
            course.Name = name;
            course.Description = description;
            SetCourseDistance(course, races.Where(IsActiveRace), input.DistanceKm);
            course.ElevationGainMeters = input.ElevationGainMeters;
            if (courseId is null) db.Courses.Add(course);
            await db.SaveChangesAsync(ct);
            return Ok(new { course.Id });
        });

    [HttpPost("courses/{courseId:guid}/revisions")]
    public Task<IActionResult> CloneCourse(Guid eventId, Guid courseId, CancellationToken ct) =>
        WithEvent(eventId, true, ct, async _ =>
        {
            var source = await FindCourse(eventId, courseId, ct);
            if (source is null) return NotFound();
            var lastVersion = await CourseFamily(source)
                .MaxAsync(x => x.Version, ct);
            var revision = CopyCourse(source, source.Name, lastVersion + 1);
            db.Courses.Add(revision);
            await db.SaveChangesAsync(ct);
            return Ok(new { revision.Id, revision.Version });
        });

    [HttpPost("courses/{courseId:guid}/duplicate")]
    public Task<IActionResult> DuplicateCourse(Guid eventId, Guid courseId, CancellationToken ct) =>
        WithEvent(eventId, true, ct, async _ =>
        {
            var source = await FindCourse(eventId, courseId, ct);
            if (source is null) return NotFound();

            // WithEvent holds the parent row lock through name allocation and insertion.
            var copyNumber = 1;
            string name;
            do
            {
                var suffix = copyNumber == 1 ? " (Copy)" : FormattableString.Invariant($" (Copy {copyNumber})");
                var prefixLength = Math.Min(source.Name.Length, 160 - suffix.Length);
                if (prefixLength < source.Name.Length && char.IsHighSurrogate(source.Name[prefixLength - 1]))
                    prefixLength--;
                name = source.Name[..prefixLength].TrimEnd() + suffix;
                copyNumber++;
            }
            while (await db.Courses.AnyAsync(x => x.EventId == eventId && x.Name == name, ct));

            var duplicate = CopyCourse(source, name, 1);
            db.Courses.Add(duplicate);
            await db.SaveChangesAsync(ct);
            return Ok(new { duplicate.Id, duplicate.Name, duplicate.Version });
        });

    private static Course CopyCourse(Course source, string name, int version, bool copyCheckpoints = true)
    {
        var copy = new Course
        {
            EventId = source.EventId, Name = name, Version = version, DistanceKm = source.DistanceKm,
            ElevationGainMeters = source.ElevationGainMeters, Description = source.Description
        };
        if (!copyCheckpoints) return copy;
        foreach (var checkpoint in source.Checkpoints)
        {
            copy.Checkpoints.Add(new CourseCheckpoint
            {
                CourseId = copy.Id, Name = checkpoint.Name, Code = checkpoint.Code, Sequence = checkpoint.Sequence,
                DistanceFromStartKm = checkpoint.DistanceFromStartKm, ElevationMeters = checkpoint.ElevationMeters,
                IsMandatory = checkpoint.IsMandatory, IsStart = checkpoint.IsStart, IsFinish = checkpoint.IsFinish,
                HasCutoff = checkpoint.HasCutoff, CutoffDurationSeconds = checkpoint.CutoffDurationSeconds,
                Description = checkpoint.Description
            });
        }
        return copy;
    }

    public sealed record RouteInput(decimal DistanceKm, IReadOnlyList<CheckpointInput> Checkpoints);

    [HttpPut("courses/{courseId:guid}/checkpoints")]
    public Task<IActionResult> SaveCheckpoints(Guid eventId, Guid courseId, RouteInput input, CancellationToken ct,
        [FromQuery] string? registrationConfirmation = null) =>
        WithEvent(eventId, true, ct, async item =>
        {
            var course = await FindCourse(eventId, courseId, ct);
            if (course is null) return NotFound();
            var races = await ReferencingRaces(course, ct);
            var historical = await HistoricalCourses(eventId).ContainsAsync(courseId, ct);
            if (EditBlock(course, historical, races.Any(IsActiveRace), item.Status) is { } block)
                return ConflictProblem(CourseEditMessage(block));
            if (input.Checkpoints is null || input.Checkpoints.Any(x => x is null))
                return Invalid("Provide a checkpoint list without empty entries.");
            var ordered = input.Checkpoints.OrderBy(x => x.Sequence).ToArray();
            if (ValidateRoute(input.DistanceKm, ordered, races.Count > 0) is { } error)
                return Invalid(error);

            var existing = course.Checkpoints.ToDictionary(x => x.Id);
            var submitted = ordered.Where(x => x.Id.HasValue).Select(x => x.Id!.Value).ToArray();
            if (submitted.Distinct().Count() != submitted.Length || submitted.Any(x => !existing.ContainsKey(x)))
                return Invalid("Checkpoint IDs must be unique and belong to this Route Revision.");
            if (course.DistanceKm == input.DistanceKm &&
                ordered.SequenceEqual(course.Checkpoints.OrderBy(x => x.Sequence).Select(CheckpointValue)))
                return NoContent();

            var registrations = await RegistrationSnapshots(AffectedRegistrations(course)).ToListAsync(ct);
            if (CheckConfirmation(course.Id, registrations, registrationConfirmation) is { } conflict) return conflict;
            if (NeedsRevision(historical, races, registrations.Count))
            {
                if (await ValidateDraftAssignments(item, course, ct) is { } invalid) return Invalid(invalid);
                var revision = await CreateNextRevision(course, races, ct, copyCheckpoints: false);
                SetCourseDistance(revision, races.Where(IsActiveRace), input.DistanceKm);
                foreach (var value in ordered)
                {
                    var checkpoint = new CourseCheckpoint { CourseId = revision.Id, Name = value.Name, Code = value.Code };
                    SetCheckpoint(checkpoint, value);
                    revision.Checkpoints.Add(checkpoint);
                }
                await db.SaveChangesAsync(ct);
                return NoContent();
            }

            db.CourseCheckpoints.RemoveRange(existing.Values.Where(x => !submitted.Contains(x.Id)));
            var temporarySequence = -1;
            foreach (var id in submitted)
            {
                existing[id].Sequence = temporarySequence--;
                existing[id].Code = Guid.NewGuid().ToString("N");
            }
            await db.SaveChangesAsync(ct);

            SetCourseDistance(course, races, input.DistanceKm);
            foreach (var value in ordered)
            {
                var checkpoint = value.Id is Guid id ? existing[id] :
                    new CourseCheckpoint { CourseId = course.Id, Name = value.Name, Code = value.Code };
                SetCheckpoint(checkpoint, value);
                if (!value.Id.HasValue) db.CourseCheckpoints.Add(checkpoint);
            }
            await db.SaveChangesAsync(ct);
            return NoContent();
        });

    private Task<Course?> FindCourse(Guid eventId, Guid courseId, CancellationToken ct) =>
        db.Courses.Include(x => x.Checkpoints).SingleOrDefaultAsync(x => x.EventId == eventId && x.Id == courseId, ct);

    private IQueryable<Course> CourseFamily(Course course) =>
        db.Courses.Where(x => x.EventId == course.EventId && x.Name == course.Name);

    // The current schema links registrations through Categories. The restrictive Category FK also
    // protects the family if another transaction inserts a reference before the delete obtains its locks.
    private IQueryable<Guid> ProtectedCourseFamilies(Guid eventId) =>
        db.Courses.Where(x => x.EventId == eventId &&
            db.Races.Any(race => race.EventId == eventId && race.Course.Name == x.Name))
            .Select(x => x.Id);

    private IQueryable<Guid> HistoricalCourses(Guid eventId) =>
        db.Courses.Where(x => x.EventId == eventId &&
            db.Courses.Any(other => other.EventId == eventId && other.Name == x.Name && other.Version > x.Version))
            .Select(x => x.Id);

    private IQueryable<Registration> AffectedRegistrations(Course course) =>
        db.Registrations.Where(x => x.EventId == course.EventId && x.Race.CourseId == course.Id &&
            x.Race.Status == RecordStatus.Active);

    private static string? EditBlock(Course course, bool historical, bool hasActiveRaces, EventStatus eventStatus)
    {
        if (historical && !(hasActiveRaces && eventStatus == EventStatus.Draft)) return "historical";
        if (course.Status != RecordStatus.Active) return "inactive";
        return hasActiveRaces && eventStatus != EventStatus.Draft ? "lifecycle" : null;
    }

    private static bool IsActiveRace(Race race) => race.Status == RecordStatus.Active;

    private static bool NeedsRevision(bool historical, List<Race> races, int registrations) =>
        historical || registrations > 0 || races.Any(race => !IsActiveRace(race));

    private async Task<Course> CreateNextRevision(Course source, List<Race> races, CancellationToken ct,
        bool copyCheckpoints = true)
    {
        var version = await CourseFamily(source).MaxAsync(x => x.Version, ct) + 1;
        var revision = CopyCourse(source, source.Name, version, copyCheckpoints);
        db.Courses.Add(revision);
        foreach (var race in races.Where(IsActiveRace)) race.CourseId = revision.Id;
        return revision;
    }

    private sealed record RegistrationSnapshot(Guid Id, Guid RaceId, Guid RunnerId, Guid? RaceCategoryId,
        Guid? StartWaveId, string? BibNumber, RegistrationStatus Status);

    private static IQueryable<RegistrationSnapshot> RegistrationSnapshots(IQueryable<Registration> registrations) =>
        registrations.Select(x => new RegistrationSnapshot(x.Id, x.RaceId, x.RunnerId, x.RaceCategoryId,
            x.StartWaveId, x.BibNumber, x.RegistrationStatus));

    private static string? RegistrationConfirmation(Guid courseId, IEnumerable<RegistrationSnapshot> registrations)
    {
        var snapshot = registrations.OrderBy(x => x.Id).ToArray();
        return snapshot.Length == 0 ? null : Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new { courseId, assignments = snapshot })));
    }

    private IActionResult? CheckConfirmation(Guid courseId, List<RegistrationSnapshot> registrations, string? confirmation) =>
        RegistrationConfirmation(courseId, registrations) == confirmation ? null :
            ConflictProblem("The affected registrations require confirmation. Reload Race Configuration, review the impact, and confirm the Route change.");

    private async Task<string?> ValidateDraftAssignments(Event item, Course course, CancellationToken ct)
    {
        var assignments = await AffectedRegistrations(course).Include(x => x.Runner)
            .Include(x => x.Race).ThenInclude(x => x.Categories)
            .Include(x => x.Race).ThenInclude(x => x.StartWaves).AsSplitQuery().ToListAsync(ct);
        foreach (var registration in assignments)
        {
            var error = RegistrationsController.ValidateAssignments(item, registration.Race, registration.Runner,
                registration.RaceCategoryId, registration.StartWaveId, registration);
            if (error is not null)
                return $"Review existing registrations for Category “{registration.Race.Name}” before changing the Route. {error}";
        }
        return null;
    }

    private static string CourseEditMessage(string block) => block switch
    {
        "historical" => "This historical Route Revision is read-only. Duplicate the Route to make an editable copy.",
        "inactive" => "Reactivate this Route before editing it.",
        _ => "This Route is assigned to a Category outside Draft. Duplicate the Route to make an editable copy."
    };

    private IQueryable<Race> ActiveCourseRaces(Guid eventId) =>
        db.Races.Where(x => x.EventId == eventId && x.Status == RecordStatus.Active);

    private Task<List<Race>> ReferencingRaces(Course course, CancellationToken ct) =>
        db.Races.Where(x => x.EventId == course.EventId && x.CourseId == course.Id).ToListAsync(ct);

    private static void SetCourseDistance(Course course, IEnumerable<Race> races, decimal distance)
    {
        course.DistanceKm = distance;
        foreach (var race in races) race.DistanceKm = distance;
    }

    private static CheckpointInput CheckpointValue(CourseCheckpoint x) => new(x.Id, x.Name, x.Code, x.Sequence,
        x.DistanceFromStartKm, x.ElevationMeters, x.IsMandatory, x.IsStart, x.IsFinish,
        x.HasCutoff, x.CutoffDurationSeconds, x.Description);

    private static string? ValidateRoute(decimal distance, CheckpointInput[] points, bool requireEndpoints)
    {
        if (distance <= 0 || !FitsDistance(distance))
            return "Enter a positive Route distance with up to three decimal places.";
        if (points.Any(x => !ValidText(x.Name, 160) || !ValidText(x.Code, 40)))
            return "Enter a name and code for every checkpoint within their length limits.";
        if (points.Select(x => x.Code.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != points.Length)
            return "Use a unique code for every checkpoint.";
        var starts = points.Count(x => x.IsStart);
        var finishes = points.Count(x => x.IsFinish);
        if (requireEndpoints && (starts == 0 || finishes == 0))
            return "A Route assigned to a Category needs a Start and a Finish checkpoint.";
        if (starts > 1 || finishes > 1 || points.Any(x => x.IsStart && x.IsFinish))
            return "Mark at most one Start and one Finish on separate checkpoints.";
        if (points.Any(x => x.IsStart && (x.Sequence != 1 || x.DistanceFromStartKm != 0)))
            return "Put Start first and set its distance to 0 km.";
        if (points.Any(x => x.IsFinish && (x.Sequence != points.Length || x.DistanceFromStartKm != distance)))
            return FormattableString.Invariant($"Put Finish last and set its distance to the Route distance ({distance:0.###} km).");
        if (points.Any(x => !x.IsStart && !x.IsFinish && x.DistanceFromStartKm > distance))
            return "The Route distance cannot be shorter than an intermediate checkpoint's distance from start.";
        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if (point.Sequence != i + 1)
                return "Checkpoint positions must start at 1 and have no gaps or duplicates.";
            if (!FitsDistance(point.DistanceFromStartKm) || point.DistanceFromStartKm < 0 || point.DistanceFromStartKm > distance ||
                (i > 0 && point.DistanceFromStartKm < points[i - 1].DistanceFromStartKm))
                return "Put checkpoints in distance order, within the Route distance and with up to three decimal places.";
            if (point.HasCutoff ? point.CutoffDurationSeconds is null or <= 0 : point.CutoffDurationSeconds is not null)
                return "Enter a positive cutoff duration for each checkpoint with Has cutoff selected.";
        }
        return null;
    }

    private static bool FitsDistance(decimal distance) => distance <= 99999.999m && decimal.Round(distance, 3) == distance;

    internal static bool IsUsable(Course course)
    {
        var points = course.Checkpoints.OrderBy(x => x.Sequence).ToArray();
        return course.Status == RecordStatus.Active && course.DistanceKm > 0 && points.Length >= 2 &&
            points[0].IsStart && points[0].DistanceFromStartKm == 0 &&
            points[^1].IsFinish && points[^1].DistanceFromStartKm == course.DistanceKm &&
            points.Count(x => x.IsStart) == 1 && points.Count(x => x.IsFinish) == 1 &&
            !points.Where((x, i) => x.Sequence != i + 1 || x.DistanceFromStartKm < 0 ||
                (i > 0 && x.DistanceFromStartKm < points[i - 1].DistanceFromStartKm)).Any();
    }

    private static void SetCheckpoint(CourseCheckpoint row, CheckpointInput value)
    {
        row.Name = value.Name.Trim();
        row.Code = value.Code.Trim();
        row.Sequence = value.Sequence;
        row.DistanceFromStartKm = value.DistanceFromStartKm;
        row.ElevationMeters = value.ElevationMeters;
        row.IsMandatory = value.IsMandatory;
        row.IsStart = value.IsStart;
        row.IsFinish = value.IsFinish;
        row.HasCutoff = value.HasCutoff;
        row.CutoffDurationSeconds = value.CutoffDurationSeconds;
        row.Description = value.Description?.Trim();
    }
}
