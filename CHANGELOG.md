# Changelog

## 1.2.0 - 2026-11-16 - Full CRUD with role-based permissions

- Requests: new "New request" page (`/requests/new`) and full edit page (`/requests/{id}/edit`) with validation (title, description, category, district, priority, address, requester). The SLA due date is computed from the category and priority and recomputed on edit. Admin-only hard delete with a confirmation dialog; creation and edits are logged as timeline events (new `Edited` event kind). Buttons on the requests grid and the detail page.
- Notes: authors (and Admins) can edit or delete their own note events; status and system events are protected. Edited notes show "(edited)".
- Categories: Admin hard delete, blocked with a "deactivate instead" message while any request references the category.
- Districts: Admin create, edit and delete (blocked while referenced), on the Admin page.
- Users: Admin tab to list, create (email, display name, role, temporary password), change role, lock/unlock (soft disable) and delete users through ASP.NET Identity `UserManager`. Cannot delete, lock or demote yourself; the last Admin cannot be deleted, demoted or locked. Locked users cannot use one-click demo logins either.
- Audit log: hard deletes (requests, notes, categories, districts) and user changes are recorded in a new `AuditLogs` table, shown on the Admin page.
- Admin page reorganised into tabs (Categories, Districts, Users, Audit).
- Schema: new `AuditLogs` table and `RequestEvents.ActorId/EditedAt`. Because the app uses `EnsureCreated`, a schema version is stored in SQLite `PRAGMA user_version`; on mismatch at startup the demo database is recreated and reseeded (logged as a warning). Existing databases (local and host) are therefore reset once on first start of 1.2.0.
- Tests: service-level tests for create/edit/delete, permission denials per role, delete-blocking, last-Admin protection and the schema-version reset.

## 1.1.0 - 2026-11-16 - Deployment to production slot

- Deploys to the shared host's `PEM/` site folder (replacing the retired old app). Workflow gains a `clean_target` dispatch input that removes stale files (keeping App_Data, .well-known, logs) after the site is offline.
- HTTP to HTTPS redirect is now opt-in (config `Https:Redirect=true`) so the site works over plain HTTP until a certificate exists; HSTS is only sent on HTTPS requests.
- web.config: stdout logging to `.\logs`, `.db` downloads blocked, `App_Data` and `logs` hidden. Startup failures (unwritable storage, database creation) are logged clearly.
- Smoke test is skipped with a notice when the `SITE_URL` variable is unset; HTTP-to-HTTPS check only runs if `SITE_URL` is https.

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
