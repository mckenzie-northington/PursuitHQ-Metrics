using Npgsql;

namespace PursuitHQ.Metrics
{
    /// <summary>
    /// A metrics report for whoever runs PursuitHQ.
    ///
    /// Runs on a laptop, reads the production database, writes one HTML file and
    /// opens it. Nothing is deployed and nothing listens on a port, which is the
    /// point: an operator dashboard is the most sensitive surface an app has,
    /// and the safest version of it is one that does not exist on the internet.
    ///
    /// Two rules hold this to metrics and nothing else, and both are structural
    /// rather than intentions:
    ///
    ///   1. It connects as a read-only database role. Not "we do not write" -
    ///      cannot write. See README.md for creating that role.
    ///   2. Every query is an aggregate. No statement here selects a name, an
    ///      email, a message, a file name, or any row belonging to one person.
    ///      Counts, sums and percentiles only. A "most active students" list is
    ///      exactly the thing that creeps in later; it is left out on purpose.
    /// </summary>
    public static class Program
    {
        private const string ConnectionVariable = "PURSUITHQ_METRICS_DB";

        public static async Task<int> Main()
        {
            var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Console.Error.WriteLine(
                    $"Set {ConnectionVariable} to the READ-ONLY connection string first.\n" +
                    "See README.md in this folder - it should be the metrics_reader role,\n" +
                    "never the owner role the API uses.");
                return 1;
            }

            Console.WriteLine("Reading...");

            Snapshot snapshot;

            try
            {
                await using var db = new NpgsqlDataSourceBuilder(connectionString).Build();
                snapshot = await GatherAsync(db);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not read the database: {ex.Message}");
                return 1;
            }

            var path = Path.Combine(AppContext.BaseDirectory, "metrics.html");
            await File.WriteAllTextAsync(path, Report.Render(snapshot));

            Console.WriteLine($"Wrote {path}");
            Open(path);

            return 0;
        }

        private static async Task<Snapshot> GatherAsync(NpgsqlDataSource db)
        {
            var accounts = await CountAsync(db, @"SELECT count(*) FROM ""AspNetUsers""");

            // ---------- growth ----------
            var signupsToday = await CountAsync(db,
                @"SELECT count(*) FROM ""AspNetUsers"" WHERE ""CreatedAt"" >= current_date");

            var signups7 = await CountAsync(db,
                @"SELECT count(*) FROM ""AspNetUsers"" WHERE ""CreatedAt"" >= now() - interval '7 days'");

            var signups30 = await CountAsync(db,
                @"SELECT count(*) FROM ""AspNetUsers"" WHERE ""CreatedAt"" >= now() - interval '30 days'");

            // A row per day whether or not anybody signed up, so a quiet week
            // reads as a flat line rather than disappearing from the chart.
            var signupsByDay = await SeriesAsync(db, @"
                SELECT d::date, count(u.""Id"")
                FROM generate_series(
                        current_date - interval '29 days', current_date, interval '1 day') d
                LEFT JOIN ""AspNetUsers"" u
                       ON u.""CreatedAt"" >= d AND u.""CreatedAt"" < d + interval '1 day'
                GROUP BY d
                ORDER BY d");

            // ---------- activation ----------
            // Assignments hang off a course rather than a user, so ownership is
            // reached through the join rather than a column.
            var withCourse = await CountAsync(db,
                @"SELECT count(DISTINCT ""UserId"") FROM ""Courses""");

            var withAssignment = await CountAsync(db, @"
                SELECT count(DISTINCT c.""UserId"")
                FROM ""Assignments"" a
                JOIN ""Courses"" c ON c.""Id"" = a.""CourseId""");

            var withMaterial = await CountAsync(db,
                @"SELECT count(DISTINCT ""UserId"") FROM ""StudyMaterials""");

            // Status 1 is Accepted. Both sides of an accepted connection count,
            // hence the UNION rather than counting rows.
            var withConnection = await CountAsync(db, @"
                SELECT count(*) FROM (
                    SELECT ""RequesterId"" AS uid FROM ""Connections"" WHERE ""Status"" = 1
                    UNION
                    SELECT ""AddresseeId""      FROM ""Connections"" WHERE ""Status"" = 1
                ) both_sides");

            // Kind 0 is an ordinary message; the others are the app narrating
            // group changes and were never typed by anybody.
            var withMessage = await CountAsync(db, @"
                SELECT count(DISTINCT ""SenderId"") FROM ""Messages""
                WHERE ""Kind"" = 0 AND ""SenderId"" IS NOT NULL");

            var medianHoursToFirstCourse = await MaybeDoubleAsync(db, @"
                SELECT percentile_cont(0.5) WITHIN GROUP (
                           ORDER BY EXTRACT(EPOCH FROM (f.first_course - u.""CreatedAt"")) / 3600.0)
                FROM ""AspNetUsers"" u
                JOIN (SELECT ""UserId"", min(""CreatedAt"") AS first_course
                      FROM ""Courses"" GROUP BY ""UserId"") f
                  ON f.""UserId"" = u.""Id""");

            // ---------- adoption, last 30 days ----------
            var adoption = new List<Bar>
            {
                new("Courses",   await CountAsync(db, Distinct30(@"""Courses""",           @"""UserId""",   @"""CreatedAt"""))),
                new("Materials", await CountAsync(db, Distinct30(@"""StudyMaterials""",    @"""UserId""",   @"""UploadedAt"""))),
                new("Flashcards",await CountAsync(db, Distinct30(@"""FlashcardDecks""",    @"""UserId""",   @"""CreatedAt"""))),
                new("Quizzes",   await CountAsync(db, Distinct30(@"""Quizzes""",           @"""UserId""",   @"""CreatedAt"""))),
                new("Notes",     await CountAsync(db, Distinct30(@"""Notes""",             @"""UserId""",   @"""CreatedAt"""))),
                new("Study chat",await CountAsync(db, Distinct30(@"""StudyConversations""",@"""UserId""",   @"""CreatedAt"""))),
                new("Calendar",  await CountAsync(db, Distinct30(@"""CalendarEvents""",    @"""UserId""",   @"""CreatedAt"""))),
                new("Resumes",   await CountAsync(db, Distinct30(@"""Resumes""",           @"""UserId""",   @"""CreatedAt"""))),
                new("Messages",  await CountAsync(db, @"
                    SELECT count(DISTINCT ""SenderId"") FROM ""Messages""
                    WHERE ""Kind"" = 0 AND ""SenderId"" IS NOT NULL
                      AND ""SentAt"" >= now() - interval '30 days'")),
                new("Assignments", await CountAsync(db, @"
                    SELECT count(DISTINCT c.""UserId"")
                    FROM ""Assignments"" a
                    JOIN ""Courses"" c ON c.""Id"" = a.""CourseId""
                    WHERE a.""CreatedAt"" >= now() - interval '30 days'"))
            };

            adoption.Sort((a, b) => b.Value.CompareTo(a.Value));

            // ---------- things made, last 7 days ----------
            var created = new List<Bar>
            {
                new("Courses",     await CountAsync(db, Created7(@"""Courses""",            @"""CreatedAt"""))),
                new("Assignments", await CountAsync(db, Created7(@"""Assignments""",        @"""CreatedAt"""))),
                new("Messages",    await CountAsync(db, @"
                    SELECT count(*) FROM ""Messages""
                    WHERE ""Kind"" = 0 AND ""DeletedAt"" IS NULL
                      AND ""SentAt"" >= now() - interval '7 days'")),
                new("Materials",   await CountAsync(db, Created7(@"""StudyMaterials""",     @"""UploadedAt"""))),
                new("Flashcards",  await CountAsync(db, Created7(@"""FlashcardDecks""",     @"""CreatedAt"""))),
                new("Quizzes",     await CountAsync(db, Created7(@"""Quizzes""",            @"""CreatedAt"""))),
                new("Events",      await CountAsync(db, Created7(@"""CalendarEvents""",     @"""CreatedAt"""))),
                new("Study chats", await CountAsync(db, Created7(@"""StudyConversations""", @"""CreatedAt""")))
            };

            // ---------- safety and capacity ----------
            var openReports = await CountAsync(db,
                @"SELECT count(*) FROM ""Reports"" WHERE ""Status"" = 0");

            var reports7 = await CountAsync(db,
                @"SELECT count(*) FROM ""Reports"" WHERE ""CreatedAt"" >= now() - interval '7 days'");

            var storageBytes = await CountAsync(db,
                @"SELECT coalesce(sum(""SizeBytes""), 0) FROM ""StudyMaterials""");

            return new Snapshot(
                GeneratedAt: DateTime.Now,
                Accounts: accounts,
                SignupsToday: signupsToday,
                Signups7: signups7,
                Signups30: signups30,
                SignupsByDay: signupsByDay,
                Activation: new List<Bar>
                {
                    new("Signed up", accounts),
                    new("Added a course", withCourse),
                    new("Added an assignment", withAssignment),
                    new("Uploaded material", withMaterial),
                    new("Connected with someone", withConnection),
                    new("Sent a message", withMessage)
                },
                MedianHoursToFirstCourse: medianHoursToFirstCourse,
                Adoption: adoption,
                Created7Days: created,
                OpenReports: openReports,
                Reports7Days: reports7,
                StorageBytes: storageBytes);
        }

        private static string Distinct30(string table, string userColumn, string dateColumn) =>
            $@"SELECT count(DISTINCT {userColumn}) FROM {table}
               WHERE {dateColumn} >= now() - interval '30 days'";

        private static string Created7(string table, string dateColumn) =>
            $@"SELECT count(*) FROM {table}
               WHERE {dateColumn} >= now() - interval '7 days'";

        private static async Task<long> CountAsync(NpgsqlDataSource db, string sql)
        {
            await using var command = db.CreateCommand(sql);
            var value = await command.ExecuteScalarAsync();

            return value is null || value is DBNull ? 0L : Convert.ToInt64(value);
        }

        private static async Task<double?> MaybeDoubleAsync(NpgsqlDataSource db, string sql)
        {
            await using var command = db.CreateCommand(sql);
            var value = await command.ExecuteScalarAsync();

            return value is null || value is DBNull ? null : Convert.ToDouble(value);
        }

        private static async Task<List<DayCount>> SeriesAsync(NpgsqlDataSource db, string sql)
        {
            var days = new List<DayCount>();

            await using var command = db.CreateCommand(sql);
            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                days.Add(new DayCount(reader.GetDateTime(0), reader.GetInt64(1)));
            }

            return days;
        }

        /// <summary>Opens the report in the default browser, on Windows or macOS.</summary>
        private static void Open(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                // Not worth failing over. The path was printed a line ago.
            }
        }
    }

    public record DayCount(DateTime Day, long Count);

    public record Bar(string Label, long Value);

    public record Snapshot(
        DateTime GeneratedAt,
        long Accounts,
        long SignupsToday,
        long Signups7,
        long Signups30,
        List<DayCount> SignupsByDay,
        List<Bar> Activation,
        double? MedianHoursToFirstCourse,
        List<Bar> Adoption,
        List<Bar> Created7Days,
        long OpenReports,
        long Reports7Days,
        long StorageBytes);
}
