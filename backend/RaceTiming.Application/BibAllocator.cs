using System.Globalization;

namespace RaceTiming.Application;

public enum BibAssignmentMode { Existing, Automatic, Mixed }

public sealed record BibAssignment(BibAssignmentMode Mode = BibAssignmentMode.Existing,
    int? Start = null, int? End = null, int Width = 0);

public static class BibAllocator
{
    public static string? Allocate(BibAssignment options, IEnumerable<string?> occupied,
        IReadOnlyList<string?> supplied, out string?[] assigned)
    {
        assigned = new string?[supplied.Count];
        if (!Enum.IsDefined(options.Mode))
            return "Select a valid bib assignment mode.";

        var used = new HashSet<string>(occupied.OfType<string>(), StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < supplied.Count; i++)
        {
            var bib = supplied[i]?.Trim();
            if (string.IsNullOrEmpty(bib)) continue;
            if (options.Mode == BibAssignmentMode.Automatic)
                return "Automatic assignment requires blank supplied bibs.";
            if (bib.Length > 32 || bib.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
                return "Bib numbers must contain 1–32 ASCII letters, digits, hyphens or underscores.";
            if (!used.Add(bib)) return "A supplied bib is already assigned or repeated in the batch.";
            assigned[i] = bib;
        }

        if (options.Mode == BibAssignmentMode.Existing) return null;
        if (options.Start is not int start || options.End is not int end ||
            start < 0 || end < start || end - start > 100_000 || options.Width is < 0 or > 10)
            return "Enter a valid nonnegative bib range spanning at most 100,001 numbers and a width from 0 to 10.";

        long candidate = start;
        for (var i = 0; i < assigned.Length; i++)
        {
            if (assigned[i] is not null) continue;
            while (candidate <= end)
            {
                var bib = (candidate++).ToString($"D{options.Width}", CultureInfo.InvariantCulture);
                if (!used.Add(bib)) continue;
                assigned[i] = bib;
                break;
            }

            if (assigned[i] is null) return "The bib range has no available numbers for all registrations.";
        }

        return null;
    }
}
