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

    private Task<IActionResult> SaveCourse(Guid eventId, Guid? courseId, CourseInput input, CancellationToken ct) =>
        WithEvent(eventId, true, ct, async _ =>
        {
            if (!ValidText(input.Name, 160) || input.DistanceKm <= 0 || !FitsDistance(input.DistanceKm) || input.ElevationGainMeters < 0)
                return Invalid("Provide a course name, a positive distance with at most three decimal places and nonnegative elevation gain.");
            var course = courseId is null ? new Course { EventId = eventId, Name = input.Name.Trim() }
                : await FindCourse(eventId, courseId.Value, ct);
            if (course is null) return NotFound();
            if (courseId.HasValue && await IsImmutable(course, ct))
                return ConflictProblem("This course revision is historical or assigned to a race. Clone a revision to edit it.");
            if (courseId.HasValue && await db.Courses.AnyAsync(x => x.EventId == eventId && x.Name == course.Name && x.Id != course.Id, ct)
                && !string.Equals(course.Name, input.Name.Trim(), StringComparison.Ordinal))
                return ConflictProblem("The name of a versioned course cannot change.");
            if (course.Checkpoints.Any(x => x.DistanceFromStartKm > input.DistanceKm ||
                (x.IsFinish && x.DistanceFromStartKm != input.DistanceKm)))
                return Invalid("Update finish and checkpoint distances together with the course distance.");
            course.Name = input.Name.Trim();
            course.Description = input.Description?.Trim();
            course.DistanceKm = input.DistanceKm;
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
            var revision = new Course
            {
                EventId = eventId, Name = source.Name, Version = lastVersion + 1,
                DistanceKm = source.DistanceKm, ElevationGainMeters = source.ElevationGainMeters,
                Description = source.Description
            };
            foreach (var checkpoint in source.Checkpoints)
            {
                var copy = new CourseCheckpoint { CourseId = revision.Id, Name = checkpoint.Name, Code = checkpoint.Code };
                SetCheckpoint(copy, new(null, checkpoint.Name, checkpoint.Code, checkpoint.Sequence,
                    checkpoint.DistanceFromStartKm, checkpoint.ElevationMeters, checkpoint.IsMandatory,
                    checkpoint.IsStart, checkpoint.IsFinish, checkpoint.HasCutoff, checkpoint.CutoffDurationSeconds,
                    checkpoint.Description));
                revision.Checkpoints.Add(copy);
            }
            db.Courses.Add(revision);
            await db.SaveChangesAsync(ct);
            return Ok(new { revision.Id, revision.Version });
        });

    public sealed record RouteInput(decimal DistanceKm, IReadOnlyList<CheckpointInput> Checkpoints);

    [HttpPut("courses/{courseId:guid}/checkpoints")]
    public Task<IActionResult> SaveCheckpoints(Guid eventId, Guid courseId, RouteInput input, CancellationToken ct) =>
        WithEvent(eventId, true, ct, async _ =>
        {
            var course = await FindCourse(eventId, courseId, ct);
            if (course is null) return NotFound();
            if (await IsImmutable(course, ct))
                return ConflictProblem("Clone a course revision before editing its checkpoints.");
            if (input.Checkpoints is null || input.Checkpoints.Any(x => x is null))
                return Invalid("Provide a checkpoint list without empty entries.");
            var ordered = input.Checkpoints.OrderBy(x => x.Sequence).ToArray();
            if (input.DistanceKm <= 0 || !FitsDistance(input.DistanceKm) ||
                ordered.Any(x => !ValidText(x.Name, 160) || !ValidText(x.Code, 40) ||
                    !FitsDistance(x.DistanceFromStartKm) || x.DistanceFromStartKm < 0 ||
                    x.DistanceFromStartKm > input.DistanceKm || (x.IsStart && x.IsFinish) ||
                    (x.HasCutoff ? x.CutoffDurationSeconds is null or <= 0 : x.CutoffDurationSeconds is not null)) ||
                ordered.Where((x, i) => x.Sequence != i + 1 ||
                    (i > 0 && x.DistanceFromStartKm < ordered[i - 1].DistanceFromStartKm)).Any() ||
                ordered.Select(x => x.Code.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ordered.Length ||
                ordered.Count(x => x.IsStart) > 1 || ordered.Count(x => x.IsFinish) > 1 ||
                ordered.Any(x => (x.IsStart && (x.Sequence != 1 || x.DistanceFromStartKm != 0)) ||
                    (x.IsFinish && (x.Sequence != ordered.Length || x.DistanceFromStartKm != input.DistanceKm))))
                return Invalid("Check checkpoint names, unique codes, contiguous sequence, ordered distances, start and finish flags, and positive cutoffs.");

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

            course.DistanceKm = input.DistanceKm;
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

    private async Task<bool> IsImmutable(Course course, CancellationToken ct) =>
        await db.Races.AnyAsync(x => x.CourseId == course.Id, ct) ||
        await db.Courses.AnyAsync(x => x.EventId == course.EventId && x.Name == course.Name && x.Version > course.Version, ct);

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
