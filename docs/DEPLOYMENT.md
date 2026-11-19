# Deployment

CivicOps is built to deploy to the same Windows/IIS shared host as the personal site, under its own subdomain (live: https://ops.alonsomiguel.com, site folder `ops/`). Deployment is **off** until you enable it.

## One-time setup
1. Create the subdomain and site folder on the host (for example `ops.alonsomiguel.com/`), point DNS at the host, enable WebSockets and an HTTPS certificate for it.
2. Give the app pool identity **write** access to the site's `App_Data` folder (SQLite database and data-protection keys live there; the app creates the folder on first start).
3. In the GitHub repo (Settings > Secrets and variables > Actions) add:
   - Secrets: `FTP_SERVER`, `FTP_USERNAME`, `FTP_PASSWORD`
   - Variables: `FTP_SERVER_DIR` (site folder `ops/`; the job refuses empty, `.` or `..`), `SITE_URL` (optional, set to `https://ops.alonsomiguel.com/health`), `DEPLOY_ENABLED=true` (last)
4. Create a GitHub environment named `production` (the deploy job uses it; add required reviewers if you want manual approval).

## What the workflow does
Build, test and publish (self-contained `win-x86`) on every push; the `deploy` job runs only on `main` and only when `DEPLOY_ENABLED == 'true'`: validate folder, write probe, upload `app_offline.htm`, `lftp mirror -R` (never `--delete`), always remove `app_offline.htm`, then smoke test `/health`, `/login` and the HTTP to HTTPS redirect.

## Live data is never overwritten
The mirror excludes `App_Data/`, `*.db` and `*.db-*`, `.well-known/` and `*.pfx`. The first start creates and seeds `App_Data/civicops.db`; later deployments leave it alone. If the schema changes in a future version there are no migrations (the demo uses `EnsureCreated`): delete the live `.db` file once (or use Admin > Reset demo data after deleting it) so it is recreated. Admins can also reset data at any time from the Admin page, which regenerates all requests.

## Manual recovery
If a deployment dies midway the site stays offline behind `app_offline.htm`; the cleanup step normally removes it. If it cannot, delete `app_offline.htm` from the site folder over FTP.

## Notes
- `Demo:OneClickLogins` (config / environment `Demo__OneClickLogins=false`) turns off the one-click demo logins. Demo accounts share the public password `Demo!2026pass`; this is intentional for a synthetic-data demo and must not be reused anywhere real.
- Logs: stdout logging is off (`stdoutLogEnabled="false"`). To diagnose startup failures, temporarily set `stdoutLogEnabled="true"` in `web.config` and redeploy.
