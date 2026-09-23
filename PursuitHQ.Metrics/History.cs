using System.Text.Json;
using System.Text.Json.Serialization;

namespace PursuitHQ.Metrics
{
    /// <summary>
    /// One line per run, appended to a file beside the report.
    ///
    /// Some numbers can be reconstructed from the database whenever you ask -
    /// signups per day comes from a CreatedAt column, deletions now come from
    /// the account-event tally. Others cannot: what fraction of people had added
    /// a course as of last Tuesday is a question about a moment that has passed,
    /// and nothing in the database remembers it.
    ///
    /// So this writes down what it saw, each time it looks. History starts the
    /// first time you run the tool and only grows on the days you run it - which
    /// is worth knowing when reading the trend, and is the honest trade for a
    /// tool that holds no write permission on the database.
    ///
    /// The file stays on the machine that ran it. It is in .gitignore, because
    /// aggregate numbers about real people are still numbers about real people.
    /// </summary>
    public static class History
    {
        public const string FileName = "history.jsonl";

        private static readonly JsonSerializerOptions Json = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static string PathFor() => Path.Combine(AppContext.BaseDirectory, FileName);

        public static List<Point> Load()
        {
            var path = PathFor();
            if (!File.Exists(path)) return new List<Point>();

            var points = new List<Point>();

            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var point = JsonSerializer.Deserialize<Point>(line, Json);
                    if (point is not null) points.Add(point);
                }
                catch (JsonException)
                {
                    // One unreadable line - a half-written record from a run that
                    // was interrupted - should not cost the whole history.
                }
            }

            return points;
        }

        /// <summary>
        /// Records today's numbers, replacing any earlier record from today.
        ///
        /// One point per day rather than one per run: running it four times over
        /// lunch should not put four dots on a chart of months.
        /// </summary>
        public static List<Point> Append(Snapshot snapshot)
        {
            var today = DateOnly.FromDateTime(snapshot.GeneratedAt);

            var point = new Point
            {
                Date = today.ToString("yyyy-MM-dd"),
                Accounts = snapshot.Accounts,
                DeletedTotal = snapshot.DeletedTotal,
                WithCourse = snapshot.Activation.Count > 1 ? snapshot.Activation[1].Value : 0,
                ActiveFeatures = snapshot.Adoption.Count(a => a.Value > 0),
                StorageBytes = snapshot.StorageBytes,
                OpenReports = snapshot.OpenReports
            };

            var points = Load().Where(p => p.Date != point.Date).ToList();
            points.Add(point);
            points.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));

            try
            {
                File.WriteAllLines(
                    PathFor(), points.Select(p => JsonSerializer.Serialize(p, Json)));
            }
            catch (IOException)
            {
                // Losing a history point is not worth losing the report over.
            }

            return points;
        }

        public class Point
        {
            public string Date { get; set; } = "";
            public long Accounts { get; set; }
            public long DeletedTotal { get; set; }
            public long WithCourse { get; set; }
            public int ActiveFeatures { get; set; }
            public long StorageBytes { get; set; }
            public long OpenReports { get; set; }
        }
    }
}
