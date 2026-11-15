# Changelog

## 1.0.1 - 2026-11-15

- Fix: "Reset demo data" failed with "database is locked" (Identity writes ran inside the seeding transaction) and surfaced as an unhandled circuit error; users are now restored before the transaction and failures show a snackbar.
- Requests grid: Number column never wraps; compact mobile layout (number, title, status chip, priority and due date per row) replaces the stacked cards; Assignee/Created hide below 1280px so tablet widths stay readable.
- Status chips: "New" uses a clearer icon and all chip icons share one size.
- Dashboard: "Requests per week" chart height fills its card.
- Admin: category/SLA editor is now a responsive card grid (no cramped inputs at tablet widths, tidy on mobile); Districts mobile rows align the district name correctly.

## 1.0.0 - 2026-11-15

Initial release.

- Overview dashboard: KPI tiles (open, overdue, average resolution time, SLA compliance) and MudChart line, bar and donut charts (requests per week, by category, by district, SLA trend).
- Requests grid (MudDataGrid) with server-side paging, sorting and filtering, quick filters, status/priority chips and CSV export via a server endpoint.
- Request detail with MudTimeline of events, notes, assignment and a validated status-update form with workflow rules.
- District breakdown table and Admin page (categories, SLA rules, "Reset demo data").
- ASP.NET Core Identity cookie auth, four roles (Admin, Supervisor, Agent, Viewer), one-click demo logins via plain form posts.
- EF Core + SQLite, created and seeded on startup with about 500 synthetic requests over 12 months.
- Dark-first theme shared with the personal site, self-hosted Inter, cookie-based theme toggle without JavaScript.
- xUnit tests (WebApplicationFactory + in-memory SQLite) for auth, roles, filtering/paging, CSV export and status transitions.
- Self-contained win-x86 publish, `web.config`, and a guarded GitHub Actions CI/CD workflow (deploy disabled until `DEPLOY_ENABLED=true`).
