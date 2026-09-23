# PursuitHQ Metrics

A local, read-only metrics dashboard for [PursuitHQ](https://pursuit-hq.com).

Run it, and it reads the production database, writes `metrics.html`, and opens
it in your browser. Nothing is deployed. Nothing listens on a port.

![the report](docs/preview.png)

## Why a separate tool

An operator dashboard is the most sensitive surface an application has — it is
the one screen designed to look at everybody at once. Building it into the app
means shipping that capability to production, where a mistake in one
authorisation check exposes it. Keeping it out here means the deployed API has
no such endpoint to get wrong.

Two properties keep it honest, and both are structural rather than intentions:

**1. It connects as a read-only database role.** Not "it does not write" — it
*cannot*. The role is granted `SELECT` and nothing else.

**2. Every query is an aggregate.** No statement in this repository selects a
name, an email address, a message, a file name, or any row belonging to one
identifiable person. Counts, sums and percentiles only.

It also does not reference the application's own data layer. Every query is
hand-written SQL, so what this tool can see is auditable by reading one small
project — rather than being bounded by which navigation properties somebody
remembered not to follow.

A "most active students" table is exactly the feature that gets added later
because it seems harmless. It is deliberately absent. If it is ever added, the
privacy policy has to change with it.

## Metrics

| Section | What it answers |
|---|---|
| Headline counts | How many accounts, and how many arrived today, this week, this month |
| Signups over 30 days | Is growth happening, and when |
| Activation funnel | How far new people get: signed up → added a course → added an assignment → uploaded material → connected → messaged |
| Median time to first course | How long before somebody does the thing the product is for |
| Feature use, 30 days | Distinct people per feature — read it for what to *stop* maintaining |
| Made this week | Courses, assignments, messages, materials and the rest |
| Accounts lost | Deletions all-time and this month, churn rate, median account lifetime |
| Over time | Saved history - accounts, deletions and activation across the days you ran it |
| Needs attention | Open reports, reports this week, total stored bytes |

The activation funnel is the one to watch. The drop between two rows is where
people give up, and it is the only number here that says whether the product is
understood rather than merely visited.

## Setup

### 1. Create the read-only role

In the Neon SQL editor, against the application's database:

```sql
CREATE ROLE metrics_reader WITH LOGIN PASSWORD 'pick-a-long-random-password';

GRANT CONNECT ON DATABASE neondb TO metrics_reader;
GRANT USAGE ON SCHEMA public TO metrics_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO metrics_reader;

-- So tables added by future migrations are readable too, without having to
-- remember to come back here.
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT TO metrics_reader;
```

That role can read and do nothing else — no `INSERT`, `UPDATE`, `DELETE` or
`DROP`. A bug in this tool, or a leak of its password, cannot damage the
database.

### 2. Point the tool at it

Take the .NET connection string from Neon and replace the username and password
with `metrics_reader` and the password chosen above.

```
setx PURSUITHQ_METRICS_DB "Host=...;Database=neondb;Username=metrics_reader;Password=...;SSL Mode=Require;Channel Binding=Require"
```

`setx` persists it for future terminals — **open a new terminal afterwards**, as
the one you typed it into will not see it.

The connection string is never committed. It lives in that environment variable
and nowhere else.

## Running

Double-click **`View metrics.cmd`**. It builds, reads the database, and opens
the report in your browser. The window closes on its own; it stays open only
when something failed, so the error is readable.

Or from a terminal:

```
cd PursuitHQ.Metrics
dotnet run
```

Both do the same thing. The first run takes a few seconds longer while NuGet
restores Npgsql.

## Not yet measurable

Two metrics need the application to record something it currently does not:

- **Daily and weekly active users, and retention cohorts.** There is no
  `LastSeenAt` on the user. One nullable column, touched on authenticated
  requests.
- **AI usage and spend.** The counter lives in memory and resets on restart, so
  there is nothing to report on. Moving it to a table would fix the reporting
  and the leaky daily cap at the same time.

Worth adding once it is clear which numbers get looked at.

## How history works

Two different mechanisms, because the questions are different.

**Deletions come from the application.** An `AccountEvents` table records a
tally mark - a kind and a date, with no user id, no email and no foreign key -
each time an account is created or destroyed. Nothing else would work: once a
row is deleted there is nothing left to count, and a record that outlives a
deleted account has to say nothing about them or the deletion was not real.
Deletion events also carry the account's age in days, which shows whether people
leave in week one or after a term.

**Everything else is saved locally.** Each run appends one line to
`history.jsonl` beside the report, holding that day's headline numbers. This is
for the questions the database cannot answer retroactively: what fraction of
people had added a course as of last Tuesday is a question about a moment that
has passed, and nothing stores it.

History therefore starts the first time you run the tool and covers the days you
ran it, not every day. That is the honest trade for a tool holding no write
permission on the database. `history.jsonl` is gitignored.

## Built with

.NET 10 · Npgsql · no other dependencies. The report is a single HTML file with
inline CSS, no scripts and no network requests — it opens from disk with nothing
to load, and will still open in five years.

## Licence

MIT.
