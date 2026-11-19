# Changelog

## 1.3.3 - 2026-11-19 - GitHub Actions on Node 24

- CI: checkout v7, setup-dotnet v6, upload-artifact v7, download-artifact v8 (Node 24 runtimes; deploy logic unchanged).
- Verified the theme toggle is a plain link (`Href` + `data-enhance-nav="false"`, full page load, no NavigationManager or JS); added a test for the rendered link. No other forceLoad navigations exist (logout is a POST form).

## 1.3.2 - 2026-11-19 - User secrets for local AI keys

- Added a `UserSecretsId` to the web project so `dotnet user-secrets set` works (secrets load automatically in Development).
- README and Rider/DataGrip guide: corrected local AI-key instructions (exact config keys, user secrets, or an `Ai__Gemini__ApiKey` environment variable in a Rider run configuration).

## 1.3.1 - 2026-11-18 - AI provider abstraction: Google Gemini (free tier) and Claude

- New `IAiProvider` (structured JSON completion with a JSON schema, streaming text) with `GeminiProvider` and `AnthropicProvider` (the previous Claude code, unchanged in behavior). `AiService` keeps permissions, limits, audit and validation provider-agnostic.
- Gemini via the native `generateContent` / `streamGenerateContent?alt=sse` API, key in the `x-goog-api-key` header, schema-constrained JSON (`responseMimeType` + `responseJsonSchema`), default model `gemini-3.5-flash-lite`. Quota (429), key/access (403), 5xx and safety blocks map to friendly messages.
- Config: `Ai:Provider` (`Gemini` default or `Anthropic`), `Ai:Gemini:ApiKey|Model`, `Ai:Anthropic:ApiKey|Model` (old `Claude:*` keys still work), `Ai:DailyLimit`, `Ai:TimeoutSeconds` (fall back to `Claude:*`). The disabled state is keyed to the selected provider.
- CI: the deploy job injects `Ai__Gemini__ApiKey` (secret `GEMINI_API_KEY`), `Claude__ApiKey` (secret `ANTHROPIC_API_KEY`) and `Ai__Provider` (repo variable `AI_PROVIDER`) only when set; never echoed, never in the build artifact.
- Tests: fake-handler tests for the Gemini request shape, header, JSON parsing, SSE chunks, error mapping and provider selection.

## 1.3.0 - 2026-11-18 - AI Assist (Claude)

- New request form: **Suggest with AI** reads the title and description and proposes a category (from the live category list), priority, district (if inferable), a one-line summary and a short rationale. The answer is requested as schema-constrained JSON and validated server-side; unknown categories, priorities or districts are rejected. Shown in a card with **Apply suggestions**; nothing is ever submitted automatically.
- Request detail: **Draft resident update** streams a short polite status update (from title, status, timeline and SLA due date) into an editable text field that can be saved as a note through the normal AddNote flow; **Summarize timeline** gives a bullet summary of long histories.
- Permissions and safety: Admin, Supervisor and Agent only (Viewer denied), enforced in `AiService`. 20 AI calls per user per hour, global daily cap (`Claude:DailyLimit`, default 200), input length limits, request timeouts and friendly error messages. Each call is written to the audit log ("AI triage", "AI draft", "AI summary", no prompt text).
- Configuration: `Claude:ApiKey` (env `Claude__ApiKey`), `Claude:Model` (default `claude-haiku-5-5`), `Claude:DailyLimit`, `Claude:TimeoutSeconds`. Without a key the AI buttons are disabled with the tooltip "AI features need an API key"; the app never fails. The system prompt is sent with prompt caching. The Claude Messages API is called with `HttpClient` and `System.Text.Json` (no SDK, no new packages).
- CI: the deploy job injects `Claude__ApiKey` into the published `web.config` only when the `ANTHROPIC_API_KEY` secret is set (never echoed, never part of the public build artifact).
- Tests: fake `HttpMessageHandler` tests for triage parsing and validation, Viewer denial, per-user rate limit, daily cap, disabled state without a key, streaming parsing, error mapping and audit logging.

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
