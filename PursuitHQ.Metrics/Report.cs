using System.Globalization;
using System.Net;
using System.Text;

namespace PursuitHQ.Metrics
{
    /// <summary>
    /// Renders the snapshot as one self-contained HTML file.
    ///
    /// No scripts, no fonts, no network. It opens from disk with nothing to
    /// load, and it will still open in five years.
    /// </summary>
    public static class Report
    {
        public static string Render(Snapshot s, List<History.Point> history)
        {
            var html = new StringBuilder();

            html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            html.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            html.Append("<title>PursuitHQ metrics</title>");
            html.Append("<style>").Append(Css).Append("</style></head><body>");

            html.Append("<header><h1>PursuitHQ</h1><p class=\"sub\">Aggregate metrics &middot; ")
                .Append(Encode(s.GeneratedAt.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)))
                .Append("</p></header><main>");

            // ---------- headline numbers ----------
            html.Append("<section><div class=\"tiles\">");
            html.Append(Tile("Total active accounts", s.Accounts.ToString("N0")));
            html.Append(Tile("New today", s.SignupsToday.ToString("N0")));
            html.Append(Tile("New this week", s.Signups7.ToString("N0")));
            html.Append(Tile("New in 30 days", s.Signups30.ToString("N0")));
            html.Append("</div></section>");

            // ---------- signups over time ----------
            html.Append("<section><h2>Signups, last 30 days</h2>");
            html.Append(TimeSeries(s.SignupsByDay));
            html.Append(DayTable(s.SignupsByDay));
            html.Append("</section>");

            // ---------- activation ----------
            html.Append("<section><h2>Activation</h2>");
            html.Append("<p class=\"note\">Of everyone who has ever signed up, how many got as far as each step. ");
            html.Append("The drop from one row to the next is where people give up.</p>");
            html.Append(Funnel(s.Activation));

            if (s.MedianHoursToFirstCourse is double hours)
            {
                html.Append("<p class=\"note\">Median time from signing up to adding a first course: <strong>")
                    .Append(Encode(Duration(hours))).Append("</strong>.</p>");
            }
            else
            {
                html.Append("<p class=\"note\">Nobody has added a course yet.</p>");
            }

            html.Append("</section>");

            // ---------- adoption ----------
            html.Append("<section><h2>Feature use, last 30 days</h2>");
            html.Append("<p class=\"note\">People who used each part at least once. ");
            html.Append("Read it for what to stop maintaining, not only for what is popular.</p>");
            html.Append(Bars(s.Adoption, "people"));
            html.Append("</section>");

            // ---------- things made ----------
            html.Append("<section><h2>Made this week</h2>");
            html.Append(Bars(s.Created7Days, "created"));
            html.Append("</section>");

            // ---------- churn ----------
            html.Append("<section><h2>Accounts lost</h2>");

            if (!s.TracksDeletions)
            {
                html.Append("<p class=\"note\">Not being recorded yet. Deleting an account removes ");
                html.Append("its row, so unless the app writes down that it happened there is nothing ");
                html.Append("left to count. Add the <code>AccountEvents</code> migration and this ");
                html.Append("fills in from that day forward.</p>");
            }
            else
            {
                var net = s.Accounts;
                html.Append("<div class=\"tiles\">");
                html.Append(Tile("Deleted, all time", s.DeletedTotal.ToString("N0")));
                html.Append(Tile("Deleted in 30 days", s.Deleted30Days.ToString("N0")));
                html.Append(Tile("Net accounts", net.ToString("N0")));
                html.Append(Tile("Churn, 30 days",
                    s.Accounts + s.Deleted30Days == 0
                        ? "-"
                        : (s.Deleted30Days * 100.0 / (s.Accounts + s.Deleted30Days)).ToString("0.#") + "%"));
                html.Append("</div>");

                if (s.DeletionsByDay.Any(d => d.Count > 0))
                {
                    html.Append("<h3>Deletions, last 30 days</h3>");
                    html.Append(TimeSeries(s.DeletionsByDay));
                }

                if (s.MedianAccountLifetimeDays is double life)
                {
                    html.Append("<p class=\"note\">Median account lifetime before deletion: <strong>")
                        .Append(Encode(life < 1 ? "under a day" : $"{life:0.#} days"))
                        .Append("</strong>. People leaving in the first week is an onboarding problem; ")
                        .Append("people leaving after a term is a different one.</p>");
                }
            }

            html.Append("</section>");

            // ---------- trend ----------
            html.Append("<section><h2>Over time</h2>");
            html.Append(Trend(history));
            html.Append("</section>");

            // ---------- attention ----------
            html.Append("<section><h2>Needs attention</h2><div class=\"tiles\">");
            html.Append(Tile("Open reports", s.OpenReports.ToString("N0"),
                s.OpenReports > 0 ? "critical" : null));
            html.Append(Tile("Reports this week", s.Reports7Days.ToString("N0")));
            html.Append(Tile("Stored files", Bytes(s.StorageBytes)));
            html.Append("</div></section>");

            html.Append("</main><footer>Aggregate counts only. ");
            html.Append("No query in this tool reads a name, an email, a message or a file.</footer>");
            html.Append("</body></html>");

            return html.ToString();
        }

        // ---------------------------------------------------------------- parts

        private static string Tile(string label, string value, string? status = null)
        {
            var cls = status is null ? "tile" : $"tile {status}";

            return $"<div class=\"{cls}\"><p class=\"tile-label\">{Encode(label)}</p>"
                 + $"<p class=\"tile-value\">{Encode(value)}</p></div>";
        }

        /// <summary>
        /// One bar per day. A column chart rather than a line: these are counts
        /// of discrete events on discrete days, and a line between them implies
        /// values in between that do not exist.
        /// </summary>
        private static string TimeSeries(List<DayCount> days)
        {
            if (days.Count == 0) return "<p class=\"note\">No data yet.</p>";

            var peak = Math.Max(1, days.Max(d => d.Count));
            var html = new StringBuilder("<div class=\"series\">");

            foreach (var day in days)
            {
                var height = day.Count == 0 ? 0 : Math.Max(3, (int)(day.Count * 100 / peak));
                var label = $"{day.Day:ddd d MMM}: {day.Count}";

                html.Append("<div class=\"col\" title=\"").Append(Encode(label)).Append("\">")
                    .Append("<div class=\"col-fill\" style=\"height:").Append(height).Append("%\"></div>")
                    .Append("</div>");
            }

            html.Append("</div><div class=\"series-axis\"><span>")
                .Append(Encode(days[0].Day.ToString("d MMM", CultureInfo.InvariantCulture)))
                .Append("</span><span>")
                .Append(Encode(days[^1].Day.ToString("d MMM", CultureInfo.InvariantCulture)))
                .Append("</span></div>");

            return html.ToString();
        }

        /// <summary>The same series as numbers, for anyone the chart does not serve.</summary>
        private static string DayTable(List<DayCount> days)
        {
            var withAny = days.Where(d => d.Count > 0).ToList();
            if (withAny.Count == 0) return "";

            var html = new StringBuilder("<details><summary>Show the numbers</summary><table>");
            html.Append("<thead><tr><th>Day</th><th>Signups</th></tr></thead><tbody>");

            foreach (var day in withAny)
            {
                html.Append("<tr><td>")
                    .Append(Encode(day.Day.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)))
                    .Append("</td><td>").Append(day.Count).Append("</td></tr>");
            }

            return html.Append("</tbody></table></details>").ToString();
        }

        /// <summary>
        /// The activation funnel, on an ordinal ramp - one hue darkening as the
        /// steps narrow, so the order is carried by the colour as well as the
        /// position. Every bar is labelled, so nothing depends on colour alone.
        /// </summary>
        private static string Funnel(List<Bar> steps)
        {
            if (steps.Count == 0) return "";

            var top = Math.Max(1, steps[0].Value);
            var html = new StringBuilder("<div class=\"funnel\">");

            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var share = step.Value * 100.0 / top;
                var width = Math.Max(2.0, share);
                var tone = Math.Min(3, i * 3 / Math.Max(1, steps.Count - 1));

                html.Append("<div class=\"row\"><span class=\"row-label\">")
                    .Append(Encode(step.Label)).Append("</span>")
                    .Append("<span class=\"track\"><span class=\"fill tone-").Append(tone)
                    .Append("\" style=\"width:").Append(width.ToString("0.#", CultureInfo.InvariantCulture))
                    .Append("%\"></span></span>")
                    .Append("<span class=\"row-value\">").Append(step.Value.ToString("N0"))
                    .Append(i == 0 ? "" : $" &middot; {share.ToString("0", CultureInfo.InvariantCulture)}%")
                    .Append("</span></div>");
            }

            return html.Append("</div>").ToString();
        }

        private static string Bars(List<Bar> bars, string unit)
        {
            if (bars.Count == 0) return "";

            var peak = Math.Max(1, bars.Max(b => b.Value));
            var html = new StringBuilder("<div class=\"funnel\">");

            foreach (var bar in bars)
            {
                var width = bar.Value == 0 ? 0.0 : Math.Max(2.0, bar.Value * 100.0 / peak);

                html.Append("<div class=\"row\" title=\"").Append(Encode($"{bar.Label}: {bar.Value} {unit}"))
                    .Append("\"><span class=\"row-label\">").Append(Encode(bar.Label)).Append("</span>")
                    .Append("<span class=\"track\"><span class=\"fill tone-1\" style=\"width:")
                    .Append(width.ToString("0.#", CultureInfo.InvariantCulture))
                    .Append("%\"></span></span><span class=\"row-value\">")
                    .Append(bar.Value.ToString("N0")).Append("</span></div>");
            }

            return html.Append("</div>").ToString();
        }

        /// <summary>
        /// The saved history - one point per day this tool was run.
        ///
        /// Gaps are real and are shown as gaps rather than smoothed over: the
        /// history only has the days somebody opened the report, and pretending
        /// otherwise would invent data.
        /// </summary>
        private static string Trend(List<History.Point> history)
        {
            if (history.Count < 2)
            {
                return "<p class=\"note\">Nothing to chart yet - this needs at least two days of "
                     + "history, and it saves one point each day you open it. Come back tomorrow.</p>";
            }

            var html = new StringBuilder();
            html.Append("<p class=\"note\">Saved each time the report is opened, on the machine that ")
                .Append("opened it. It covers the days you looked, not every day.</p>");

            html.Append(TrendChart("Accounts", history, p => p.Accounts));
            html.Append(TrendChart("Deleted, all time", history, p => p.DeletedTotal));
            html.Append(TrendChart("People with a course", history, p => p.WithCourse));

            html.Append("<details><summary>Show the numbers</summary><table>");
            html.Append("<thead><tr><th>Date</th><th>Accounts</th><th>Deleted</th>")
                .Append("<th>With a course</th></tr></thead><tbody>");

            foreach (var point in history)
            {
                html.Append("<tr><td>").Append(Encode(point.Date)).Append("</td><td>")
                    .Append(point.Accounts).Append("</td><td>")
                    .Append(point.DeletedTotal).Append("</td><td>")
                    .Append(point.WithCourse).Append("</td></tr>");
            }

            return html.Append("</tbody></table></details>").ToString();
        }

        private static string TrendChart(
            string title, List<History.Point> history, Func<History.Point, long> pick)
        {
            var peak = Math.Max(1, history.Max(pick));
            var html = new StringBuilder();

            html.Append("<h3>").Append(Encode(title)).Append("</h3><div class=\"series\">");

            foreach (var point in history)
            {
                var value = pick(point);
                var height = value == 0 ? 0 : Math.Max(3, (int)(value * 100 / peak));

                html.Append("<div class=\"col\" title=\"")
                    .Append(Encode($"{point.Date}: {value}")).Append("\">")
                    .Append("<div class=\"col-fill\" style=\"height:").Append(height).Append("%\"></div>")
                    .Append("</div>");
            }

            return html.Append("</div><div class=\"series-axis\"><span>")
                .Append(Encode(history[0].Date)).Append("</span><span>")
                .Append(Encode(history[^1].Date)).Append("</span></div>").ToString();
        }

        // ------------------------------------------------------------ helpers

        private static string Duration(double hours) =>
            hours < 1 ? $"{hours * 60:0} minutes"
            : hours < 48 ? $"{hours:0.#} hours"
            : $"{hours / 24:0.#} days";

        private static string Bytes(long bytes) =>
            bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824.0:0.#} GB"
            : bytes >= 1_048_576 ? $"{bytes / 1_048_576.0:0.#} MB"
            : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB"
            : $"{bytes} B";

        private static string Encode(string value) => WebUtility.HtmlEncode(value);

        // ---------------------------------------------------------------- css
        //
        // Colours come from the validated data-viz palette: one blue hue for
        // every mark, with the funnel walking an ordinal ramp. Dark mode is a
        // selected set of steps for the dark surface, not an inverted copy.

        private const string Css = @"
:root {
  color-scheme: light;
  --plane: #f9f9f7;
  --surface: #fcfcfb;
  --ink: #0b0b0b;
  --ink-2: #52514e;
  --muted: #898781;
  --line: #e1e0d9;
  --axis: #c3c2b7;
  --tone-0: #86b6ef;
  --tone-1: #5598e7;
  --tone-2: #2a78d6;
  --tone-3: #1c5cab;
  --critical: #d03b3b;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme='light']) {
    color-scheme: dark;
    --plane: #0d0d0d;
    --surface: #1a1a19;
    --ink: #ffffff;
    --ink-2: #c3c2b7;
    --muted: #898781;
    --line: #2c2c2a;
    --axis: #383835;
    --tone-0: #6da7ec;
    --tone-1: #3987e5;
    --tone-2: #256abf;
    --tone-3: #184f95;
    --critical: #d03b3b;
  }
}
* { box-sizing: border-box; }
body {
  margin: 0; background: var(--plane); color: var(--ink);
  font: 15px/1.5 -apple-system, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
}
header { padding: 32px 16px 8px; max-width: 900px; margin: 0 auto; }
h1 { margin: 0; font-size: 22px; letter-spacing: -0.01em; }
.sub { margin: 4px 0 0; color: var(--muted); font-size: 13px; }
main { max-width: 900px; margin: 0 auto; padding: 8px 16px 48px; }
section {
  background: var(--surface); border: 1px solid var(--line);
  border-radius: 12px; padding: 20px; margin-top: 16px;
}
h2 { margin: 0 0 4px; font-size: 15px; font-weight: 600; }
h3 { margin: 22px 0 8px; font-size: 13px; font-weight: 600; color: var(--ink-2); }
code { font-family: ui-monospace, Consolas, monospace; font-size: 12px;
       background: var(--line); padding: 1px 5px; border-radius: 4px; }
.note { margin: 0 0 16px; color: var(--ink-2); font-size: 13px; max-width: 60ch; }
/* A note that follows a chart is a caption, not an intro - it needs air above. */
.funnel + .note { margin: 16px 0 0; }
.tiles { display: grid; gap: 12px; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); }
.tile { border: 1px solid var(--line); border-radius: 10px; padding: 14px 16px; }
.tile-label { margin: 0; font-size: 12px; color: var(--muted); text-transform: uppercase;
              letter-spacing: 0.04em; }
.tile-value { margin: 6px 0 0; font-size: 26px; font-weight: 650; letter-spacing: -0.02em; }
.tile.critical .tile-value { color: var(--critical); }
.series { display: flex; align-items: flex-end; gap: 2px; height: 140px;
          border-bottom: 1px solid var(--axis); padding-top: 8px; }
.col { flex: 1; height: 100%; display: flex; align-items: flex-end; }
.col-fill { width: 100%; background: var(--tone-2); border-radius: 3px 3px 0 0; min-height: 0; }
.col:hover .col-fill { background: var(--tone-3); }
.series-axis { display: flex; justify-content: space-between; margin-top: 6px;
               font-size: 12px; color: var(--muted); }
.funnel { display: flex; flex-direction: column; gap: 8px; }
.row { display: grid; grid-template-columns: 11rem 1fr 6.5rem; gap: 12px; align-items: center; }
.row-label { font-size: 13px; color: var(--ink-2); }
.track { display: block; background: var(--line); border-radius: 4px; height: 20px; }
.fill { display: block; height: 100%; border-radius: 4px; }
.tone-0 { background: var(--tone-0); }
.tone-1 { background: var(--tone-1); }
.tone-2 { background: var(--tone-2); }
.tone-3 { background: var(--tone-3); }
.row-value { font-size: 13px; font-variant-numeric: tabular-nums; color: var(--ink); text-align: right; }
details { margin-top: 14px; }
summary { cursor: pointer; font-size: 13px; color: var(--ink-2); }
table { border-collapse: collapse; margin-top: 10px; font-size: 13px; width: 100%; max-width: 320px; }
th, td { text-align: left; padding: 5px 10px 5px 0; border-bottom: 1px solid var(--line); }
th { color: var(--muted); font-weight: 600; }
td:last-child { font-variant-numeric: tabular-nums; }
footer { max-width: 900px; margin: 0 auto; padding: 0 16px 40px; color: var(--muted); font-size: 12px; }
@media (max-width: 560px) {
  .row { grid-template-columns: 8rem 1fr 4.5rem; gap: 8px; }
  .row-label { font-size: 12px; }
}
";
    }
}
