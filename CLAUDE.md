# CLAUDE.md

# Hard rules

- MudBlazor-first, always latest: use the latest stable MudBlazor release and its native components/APIs for all UI (layout, navigation, theming, dark mode, cards, timeline, charts, data grid, chips, buttons, typography, spacing utility classes). Before building any UI, check the current docs at https://mudblazor.com and the release notes; never hand-roll HTML/CSS/JS for something a MudBlazor component already provides. Do not use deprecated/obsolete parameters; the build must have zero warnings (including MudBlazor analyzer warnings; `TreatWarningsAsErrors` is on). When upgrading MudBlazor, follow its migration guide and update this file and CHANGELOG.md.
- Icons: use only MudBlazor's built-in icons (`Icons.Material.*` / `Icons.Custom.*`) - no icon fonts, SVG packs or other icon libraries.
- No external libraries: no NuGet UI/chart packages, CDN scripts/styles or third-party web fonts beyond MudBlazor itself and the .NET/ASP.NET Core/EF Core/Identity frameworks. Fonts are self-hosted in `wwwroot/fonts` (Inter). Test packages (xunit, Mvc.Testing) are dev-only and allowed.
- No custom JavaScript: no hand-written JS, inline `<script>` blocks or `IJSRuntime` calls in app code. Only `blazor.web.js`, MudBlazor's bundled `MudBlazor.min.js` and the unmodified framework-template `ReconnectModal.razor(.js/.css)` are allowed. Use Blazor/MudBlazor APIs, server-side cookies and plain form posts instead.
- All data is synthetic. Never add real county/agency names, logos, or real resident data. The footer must say "Demo with synthetic data".

## Overview
CivicOps: a government-style service-request and permit operations dashboard (portfolio demo). Overview KPIs and charts, server-side requests grid with CSV export, request detail with timeline and workflow, district breakdown, admin (categories, SLA rules, demo reset). Cookie auth with ASP.NET Core Identity and four roles.

## Stack
- .NET SDK 11.0.100, `net11.0`, Blazor Web App, Interactive Server (global; auth/error pages are static SSR)
- MudBlazor 9.13.0 (MudChart, MudDataGrid, MudTimeline, MudTable)
- EF Core 11 + SQLite (`App_Data/civicops.db`, `EnsureCreated`, seeded on startup; schema version in `PRAGMA user_version` via `Data/SchemaVersion.cs`, bump `SchemaVersion.Current` on every model change and the demo DB is recreated and reseeded), ASP.NET Core Identity (cookie)
- Hosting: Windows/IIS via FTPS from GitHub Actions (self-contained win-x86)

## Commands
- Run: `dotnet run` (http://localhost:5110)
- Build: `dotnet build CivicOps.slnx` (0 warnings, 0 errors)
- Test: `dotnet test CivicOps.slnx` (xunit + `WebApplicationFactory` + in-memory shared-cache SQLite)
- Publish (host): `dotnet publish CivicOps.Web.csproj -c Release -r win-x86 --self-contained true -o <dir outside the project folder>`
- Health: `/health`

## Structure
- `Program.cs` - services, Identity/cookies, authorization (fallback policy = authenticated), HTTPS redirect + HSTS, pipeline, startup seed
- `Data/` - `Models.cs` (entities, enums, `Roles`), `CivicOpsDb.cs` (Identity DbContext), `DemoDataSeeder.cs` (users + ~500 synthetic requests, deterministic seed, `ResetAsync`)
- `Services/` - `Domain.cs` (`Actor`, `OpResult`, `StatusRules` workflow), `RequestQuery*.cs` (filter/sort/page), `RequestCommandService.cs` (create/edit/delete/status/assign/notes, permission checks), `DashboardService.cs` (KPIs/series/districts), `AdminService.cs` (categories, districts, audit), `UserAdminService.cs` (Identity users), `CsvExport.cs`
- `Endpoints/` - `AccountEndpoints.cs` (POST `/account/login`, `/account/demo`, `/account/logout`; form posts, no JS), `ExportEndpoints.cs` (GET `/api/requests/export.csv`)
- `Theme/AppTheme.cs`, `Theme/ThemeEndpoints.cs` - same dark-first navy + indigo/cyan theme and cookie toggle (`/theme/{light|dark}`) as the personal site
- `Components/` - `App.razor`, `Routes.razor`, `Layout/MainLayout.razor` (interactive shell), `Layout/PublicLayout.razor` (static pages), `Kpi.razor`, `UiHelpers.cs`, `Pages/` (Dashboard, Requests, RequestDetail, Districts, Admin, Login, AccessDenied, NotFound, Error)
- `wwwroot/app.css` - small custom CSS (use palette CSS variables, no hard-coded colors); `wwwroot/fonts/` - Inter
- `tests/CivicOps.Web.Tests/` - `TestApp.cs` (factory), `HttpTests.cs`, `ServiceTests.cs`
- `web.config` - IIS (out-of-process, WebSockets, `.pfx` blocked, `.well-known` passthrough)
- `.github/workflows/ci-cd.yml` - CI + guarded FTPS deploy; `docs/DEPLOYMENT.md`

## Auth and roles
- Demo accounts (`admin|supervisor|agent|viewer@civicops.demo`, password `Demo!2026pass`); login page offers one-click demo logins (disable with config `Demo:OneClickLogins=false`).
- Viewer read-only; Agent creates requests and updates/edits/adds notes only on requests assigned to them (edit not when closed/canceled); Supervisor also assigns and edits any request; Admin also deletes requests, manages categories/SLA rules, districts and users and resets demo data. Authors edit/delete their own notes. Hard deletes write `AuditLogs` rows. Rules are enforced in the services (`Actor` checks), not only in the UI.
- Static SSR pages (`[ExcludeFromInteractiveRouting]`) must be used for anything reachable before sign-in, because the SignalR hub requires an authenticated user. Their forms need `<AntiforgeryToken />`.

## Conventions
- All timestamps UTC `DateTime` (SQLite cannot order `DateTimeOffset`).
- Status workflow lives only in `StatusRules`; notes are required for On hold, Resolved, Canceled and reopen.
- CSV export neutralizes spreadsheet formulas (`CsvExport.Escape`).
- Mobile-first: test at 390px and desktop, light and dark, before merging. Tap targets at least 44px.
- Update CHANGELOG.md and bump `<Version>` in the csproj (shown in the footer) on every change.

## CI/CD and hosting
- Secrets `FTP_SERVER`, `FTP_USERNAME`, `FTP_PASSWORD`; repo variables `FTP_SERVER_DIR` (required, site folder, e.g. `ops.alonsomiguel.com/`), `DEPLOY_ENABLED` (`true` to deploy), `SITE_URL` (smoke test URL, default `https://ops.alonsomiguel.com/health`).
- Deploy design (identical to the personal site): folder validation, write preflight, `app_offline.htm`, `lftp mirror -R` without `--delete`, always-run cleanup, smoke test, concurrency group.
- `App_Data/` and `*.db` are excluded from the mirror so live data and keys are never overwritten. The app creates `App_Data` itself; the app pool identity needs write access to it.
- Self-contained `win-x86` publish because the host only supports ASP.NET Core up to 10.x; switch to framework-dependent once it supports .NET 11.
