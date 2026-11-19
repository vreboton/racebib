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
        SaveCourse(eventId, null, input, ct);

    [HttpPut("courses/{courseId:guid}")]
    public Task<IActionResult> UpdateCourse(Guid eventId, Guid courseId, CourseInput input, CancellationToken ct) =>
        SaveCourse(eventId, courseId, input, ct);

    public sealed record CourseStatusInput(RecordStatus? Status);

    [HttpPatch("courses/{courseId:guid}/status")]
    public Task<IActionResult> ChangeCourseStatus(Guid eventId, Guid courseId, CourseStatusInput input, CancellationToken ct) =>
        WithEvent(eventId, true, ct, async _ =>
        {
            var course = await db.Courses.SingleOrDefaultAsync(x => x.EventId == eventId && x.Id == courseId, ct);
            if (course is null) return NotFound();
            if (input.Status is not (RecordStatus.Active or RecordStatus.Inactive))
                return Invalid("Choose Active or Inactive for the course status.");
            if (course.Status == input.Status) return NoContent();
            if (input.Status == RecordStatus.Inactive && await ActiveCourseRaces(eventId).AnyAsync(x => x.CourseId == courseId, ct))
                return ConflictProblem("This course is used by an active race. Retire the race or switch it to an eligible course before retiring this course.");

            course.Status = input.Status.Value;
            await db.SaveChangesAsync(ct);
            return NoContent();
        });

    private Task<IActionResult> SaveCourse(Guid eventId, Guid? courseId, CourseInput input, CancellationToken ct) =>
        WithEvent(eventId, true, ct, async _ =>
        {
            if (!ValidText(input.Name, 160) || input.DistanceKm <= 0 || !FitsDistance(input.DistanceKm) || input.ElevationGainMeters < 0)
                return Invalid("Provide a course name, a positive distance with at most three decimal places and nonnegative elevation gain.");
            var course = courseId is null ? new Course { EventId = eventId, Name = input.Name.Trim() }
                : await FindCourse(eventId, courseId.Value, ct);
            if (course is null) return NotFound();
            if (courseId.HasValue && await CourseEditBlock(eventId, course.Id, ct) is { } block)
                return ConflictProblem(CourseEditMessage(block));
            var races = courseId.HasValue ? await ReferencingRaces(course, ct) : [];
            var name = input.Name.Trim();
            if (courseId.HasValue && course.Name != name &&
                (course.Version > 1 || await db.Courses.AnyAsync(x =>
                    x.EventId == eventId && x.Id != course.Id && x.Name == course.Name, ct)))
                return ConflictProblem("The spelling and capitalization of a versioned course name cannot change.");
            if ((courseId is null || course.Name != name) && await db.Courses.AnyAsync(x =>
                x.EventId == eventId && x.Id != course.Id && x.Name == name, ct))
                return ConflictProblem("A course with this name already exists in the event.");
            if (course.Checkpoints.Any(x => x.DistanceFromStartKm > input.DistanceKm ||
                (x.IsFinish && x.DistanceFromStartKm != input.DistanceKm)))
                return Invalid("Update finish and checkpoint distances together with the course distance.");
            if (course.Status != RecordStatus.Active && races.Any(x => x.Status == RecordStatus.Active))
                return Invalid("A course used by an active race must be active.");
            if (races.Count > 0 &&
                ValidateRoute(input.DistanceKm, course.Checkpoints.OrderBy(x => x.Sequence).Select(CheckpointValue).ToArray(), true) is not null)
                return Invalid("An assigned course needs a valid Start, Finish, and checkpoint layout. Edit the checkpoints to correct the route.");
            course.Name = name;
            course.Description = input.Description?.Trim();
            SetCourseDistance(course, races, input.DistanceKm);
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
            var lastVersion = await db.Courses.Where(x => x.EventId == eventId && x.Name == source.Name)
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

    private static Course CopyCourse(Course source, string name, int version)
    {
        var copy = new Course
        {
            EventId = source.EventId, Name = name, Version = version, DistanceKm = source.DistanceKm,
            ElevationGainMeters = source.ElevationGainMeters, Description = source.Description
        };
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
    public Task<IActionResult> SaveCheckpoints(Guid eventId, Guid courseId, RouteInput input, CancellationToken ct) =>
        WithEvent(eventId, true, ct, async _ =>
        {
            var course = await FindCourse(eventId, courseId, ct);
            if (course is null) return NotFound();
            if (await CourseEditBlock(eventId, course.Id, ct) is { } block)
                return ConflictProblem(CourseEditMessage(block));
            var races = await ReferencingRaces(course, ct);
            if (input.Checkpoints is null || input.Checkpoints.Any(x => x is null))
                return Invalid("Provide a checkpoint list without empty entries.");
            var ordered = input.Checkpoints.OrderBy(x => x.Sequence).ToArray();
            if (course.Status != RecordStatus.Active && races.Any(x => x.Status == RecordStatus.Active))
                return Invalid("A course used by an active race must be active.");
            if (ValidateRoute(input.DistanceKm, ordered, races.Count > 0) is { } error)
                return Invalid(error);

            var existing = course.Checkpoints.ToDictionary(x => x.Id);
            var submitted = ordered.Where(x => x.Id.HasValue).Select(x => x.Id!.Value).ToArray();
            if (submitted.Distinct().Count() != submitted.Length || submitted.Any(x => !existing.ContainsKey(x)))
                return Invalid("Checkpoint IDs must be unique and belong to this course revision.");

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

    private IQueryable<Guid> HistoricalCourses(Guid eventId) =>
        db.Courses.Where(x => x.EventId == eventId &&
            db.Courses.Any(other => other.EventId == eventId && other.Name == x.Name && other.Version > x.Version))
            .Select(x => x.Id);

    private IQueryable<Guid> CoursesWithRegistrations(Guid eventId) =>
        db.Races.Where(x => x.EventId == eventId && x.Registrations.Any()).Select(x => x.CourseId).Distinct();

    private async Task<string?> CourseEditBlock(Guid eventId, Guid courseId, CancellationToken ct)
    {
        if (await HistoricalCourses(eventId).ContainsAsync(courseId, ct)) return "historical";
        return await CoursesWithRegistrations(eventId).ContainsAsync(courseId, ct) ? "registrations" : null;
    }

    private static string CourseEditMessage(string block) => block == "historical"
        ? "This historical course revision is read-only. Duplicate the course to make an editable copy."
        : "This course is used by a race with registrations. Duplicate the course to make an editable copy.";

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
            return "Enter a positive course distance with up to three decimal places.";
        if (points.Any(x => !ValidText(x.Name, 160) || !ValidText(x.Code, 40)))
            return "Enter a name and code for every checkpoint within their length limits.";
        if (points.Select(x => x.Code.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != points.Length)
            return "Use a unique code for every checkpoint.";
        var starts = points.Count(x => x.IsStart);
        var finishes = points.Count(x => x.IsFinish);
        if (requireEndpoints && (starts == 0 || finishes == 0))
            return "A course assigned to a race needs a Start and a Finish checkpoint.";
        if (starts > 1 || finishes > 1 || points.Any(x => x.IsStart && x.IsFinish))
            return "Mark at most one Start and one Finish on separate checkpoints.";
        if (points.Any(x => x.IsStart && (x.Sequence != 1 || x.DistanceFromStartKm != 0)))
            return "Put Start first and set its distance to 0 km.";
        if (points.Any(x => x.IsFinish && (x.Sequence != points.Length || x.DistanceFromStartKm != distance)))
            return FormattableString.Invariant($"Put Finish last and set its distance to the course distance ({distance:0.###} km).");
        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if (point.Sequence != i + 1)
                return "Checkpoint positions must start at 1 and have no gaps or duplicates.";
            if (!FitsDistance(point.DistanceFromStartKm) || point.DistanceFromStartKm < 0 || point.DistanceFromStartKm > distance ||
                (i > 0 && point.DistanceFromStartKm < points[i - 1].DistanceFromStartKm))
                return "Put checkpoints in distance order, within the course distance and with up to three decimal places.";
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
