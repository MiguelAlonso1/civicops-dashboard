using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CivicOps.Web.Data;

/// <summary>Creates demo users and ~500 synthetic service requests spread over the last 12 months. All data is fictional.</summary>
public sealed class DemoDataSeeder(
    IDbContextFactory<CivicOpsDb> factory, UserManager<ApplicationUser> users, RoleManager<IdentityRole> roles, TimeProvider clock)
{
    public const string DemoPassword = "Demo!2026pass";
    public const int RequestCount = 500;

    /// <summary>The four one-click demo accounts.</summary>
    public static readonly IReadOnlyDictionary<string, (string Email, string Name)> DemoLogins = new Dictionary<string, (string, string)>
    {
        [Roles.Admin] = ("admin@civicops.demo", "Alex Rivera"),
        [Roles.Supervisor] = ("supervisor@civicops.demo", "Jordan Blake"),
        [Roles.Agent] = ("agent@civicops.demo", "Sam Okafor"),
        [Roles.Viewer] = ("viewer@civicops.demo", "Taylor Chen"),
    };

    private static readonly string[] ExtraAgents =
        ["Maria Santos", "Devon Price", "Priya Nair", "Luis Ortega", "Hannah Kim", "Marcus Hill", "Elena Petrova"];

    private sealed record CatSeed(string Name, string Department, int Weight, int[] Sla, string[] Titles, string[] Resolutions);

    private static readonly CatSeed[] Cats =
    [
        new("Pothole Repair", "Public Works", 18, [336, 168, 72, 24],
            ["Large pothole in travel lane", "Pothole near crosswalk", "Road surface cratering after rain", "Deep pothole damaging tires", "Sunken asphalt at intersection"],
            ["Crew patched the pothole with hot mix.", "Cold patch applied, scheduled for resurfacing.", "Utility cut repaired and compacted."]),
        new("Streetlight Outage", "Public Works", 12, [336, 120, 48, 24],
            ["Streetlight out on residential block", "Flickering streetlight at bus stop", "Dark stretch near park entrance", "Pole light cycling on and off"],
            ["Replaced photocell and lamp head.", "Reset breaker and replaced bulb.", "Fixture replaced with LED unit."]),
        new("Illegal Dumping", "Solid Waste", 12, [240, 120, 48, 24],
            ["Mattress and furniture dumped on swale", "Construction debris left at curb", "Overflowing dump site behind lot", "Tires abandoned near canal"],
            ["Debris removed and area photographed for enforcement.", "Crew hauled bulk waste; site cleared.", "Sign installed and site cleaned."]),
        new("Code Enforcement", "Neighborhood Compliance", 14, [480, 240, 96, 48],
            ["Overgrown vegetation on vacant lot", "Unpermitted structure reported", "Vehicle parked on lawn", "Property with broken fence and debris", "Standing water in unmaintained yard"],
            ["Inspection completed, notice of violation issued.", "Owner complied after courtesy notice.", "Case referred to hearing after re-inspection."]),
        new("Building Permit", "Permits and Inspections", 12, [720, 480, 240, 120],
            ["Residential roof replacement permit review", "Commercial tenant improvement permit", "Fence permit application", "Solar panel installation permit", "Accessory structure permit question"],
            ["Plan review approved and permit issued.", "Corrections accepted, permit issued.", "Final inspection passed."]),
        new("Business License", "Permits and Inspections", 6, [480, 240, 120, 48],
            ["New restaurant license application", "License renewal delay", "Home-based business registration", "Change of ownership filing"],
            ["License issued and mailed.", "Renewal processed after fee confirmation.", "Registration verified and approved."]),
        new("Tree Trimming", "Parks and Recreation", 8, [504, 336, 96, 24],
            ["Branches over power lines", "Fallen limb blocking sidewalk", "Dead tree leaning toward house", "Palm fronds overhanging road"],
            ["Crew trimmed canopy and removed debris.", "Hazard tree removed.", "Arborist inspection completed; pruned."]),
        new("Water Leak", "Water and Sewer", 8, [168, 72, 24, 6],
            ["Water bubbling from the street", "Low pressure and visible leak at meter", "Hydrant leaking at base", "Sewer odor and standing water"],
            ["Main isolated and leak repaired.", "Meter box replaced; flow restored.", "Hydrant gasket replaced."]),
        new("Noise Complaint", "Neighborhood Compliance", 6, [96, 48, 24, 8],
            ["Late-night amplified music", "Construction before permitted hours", "Persistent generator noise"],
            ["Officer contacted responsible party; noise stopped.", "Warning issued and logged.", "No violation observed on return visit."]),
        new("Graffiti Removal", "Solid Waste", 4, [168, 96, 48, 24],
            ["Graffiti on retaining wall", "Tagging on public bench", "Graffiti on utility box"],
            ["Surface cleaned and repainted.", "Graffiti removed by crew.", "Covered with matching paint."]),
    ];

    private static readonly (string Code, string Name, int Pop, int Weight, double Bias)[] Dists =
    [
        ("D1", "North Harbor", 61200, 12, 0.90), ("D2", "Westgate", 74850, 14, 1.00), ("D3", "Bayview", 58300, 11, 1.15),
        ("D4", "Old Town", 49100, 13, 0.85), ("D5", "Riverside", 82400, 16, 1.10), ("D6", "Lakeshore", 45600, 9, 0.95),
        ("D7", "Midtown", 91750, 15, 1.05), ("D8", "South Pines", 68900, 10, 1.25),
    ];

    private static readonly string[] Streets =
        ["Maple Ave", "Coral Way", "Harbor Blvd", "Palmetto St", "Lakeview Dr", "Bayshore Rd", "Cypress Ln", "Magnolia Ct", "Heron Pl",
         "Sunset Blvd", "Orchid Ave", "Marina Way", "Pine Ridge Rd", "Banyan St", "Ocean Dr", "Riverwalk Ln"];
    private static readonly string[] First = ["Olivia", "Noah", "Emma", "Liam", "Ava", "Mateo", "Sofia", "Ethan", "Isabella", "Lucas", "Mia", "Daniel", "Camila", "James", "Grace", "Victor"];
    private static readonly string[] Last = ["Martinez", "Johnson", "Garcia", "Nguyen", "Brown", "Lopez", "Williams", "Patel", "Davis", "Fernandez", "Wilson", "Reyes", "Clark", "Torres"];

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await EnsureUsersAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Categories.AnyAsync(ct)) return;
        await SeedDataAsync(db, ct);
    }

    /// <summary>Wipes requests, categories, SLA rules and districts, then regenerates them. Users are kept (passwords restored).</summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Events.ExecuteDeleteAsync(ct);
        await db.Requests.ExecuteDeleteAsync(ct);
        await db.SlaRules.ExecuteDeleteAsync(ct);
        await db.Categories.ExecuteDeleteAsync(ct);
        await db.Districts.ExecuteDeleteAsync(ct);
        await EnsureUsersAsync(restorePasswords: true);
        await SeedDataAsync(db, ct);
        await tx.CommitAsync(ct);
    }

    private async Task EnsureUsersAsync(bool restorePasswords = false)
    {
        foreach (var r in Roles.All)
            if (!await roles.RoleExistsAsync(r)) await roles.CreateAsync(new IdentityRole(r));

        foreach (var (role, (email, name)) in DemoLogins) await EnsureUserAsync(email, name, role, restorePasswords);
        foreach (var name in ExtraAgents)
            await EnsureUserAsync($"{name.Split(' ')[0].ToLowerInvariant()}@civicops.demo", name, Roles.Agent, restorePasswords);
    }

    private async Task EnsureUserAsync(string email, string name, string role, bool restorePassword)
    {
        var u = await users.FindByEmailAsync(email);
        if (u is null)
        {
            u = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = name };
            var created = await users.CreateAsync(u, DemoPassword);
            if (!created.Succeeded) throw new InvalidOperationException("Could not create demo user: " + string.Join("; ", created.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(u, role);
        }
        else if (restorePassword)
        {
            await users.RemovePasswordAsync(u);
            await users.AddPasswordAsync(u, DemoPassword);
            await users.SetLockoutEndDateAsync(u, null);
            await users.ResetAccessFailedCountAsync(u);
        }
    }

    private async Task SeedDataAsync(CivicOpsDb db, CancellationToken ct)
    {
        var rng = new Random(20261115);
        var now = clock.GetUtcNow().UtcDateTime;

        var categories = Cats.Select(c => new Category
        {
            Name = c.Name, Department = c.Department,
            SlaRules = Enum.GetValues<RequestPriority>().Select(p => new SlaRule { Priority = p, TargetHours = c.Sla[(int)p] }).ToList()
        }).ToList();
        var districts = Dists.Select(d => new District { Code = d.Code, Name = d.Name, Population = d.Pop }).ToList();
        db.Categories.AddRange(categories);
        db.Districts.AddRange(districts);
        await db.SaveChangesAsync(ct);

        var staff = new List<ApplicationUser>();
        foreach (var u in await users.GetUsersInRoleAsync(Roles.Agent)) staff.Add(u);
        foreach (var u in await users.GetUsersInRoleAsync(Roles.Supervisor)) staff.Add(u);
        var supervisor = staff.First(s => s.Email == DemoLogins[Roles.Supervisor].Email);

        var catPick = Weighted(Cats.Select(c => c.Weight).ToArray());
        var distPick = Weighted(Dists.Select(d => d.Weight).ToArray());
        var prioPick = Weighted([25, 50, 20, 5]);

        var requests = new List<ServiceRequest>(RequestCount);
        for (var i = 0; i < RequestCount; i++)
        {
            var ci = catPick(rng);
            var di = distPick(rng);
            var cs = Cats[ci];
            var prio = (RequestPriority)prioPick(rng);
            var daysAgo = 365.0 * Math.Pow(rng.NextDouble(), 1.3);
            var created = now.AddDays(-daysAgo).AddMinutes(-rng.Next(0, 600));
            if (created > now.AddHours(-1)) created = now.AddHours(-1 - rng.NextDouble() * 5);
            var slaHours = cs.Sla[(int)prio];
            var due = created.AddHours(slaHours);

            var trend = 0.95 + 0.25 * (daysAgo / 365.0);
            var factor = (0.12 + 1.35 * Math.Pow(rng.NextDouble(), 1.6)) * Dists[di].Bias * trend;
            if (rng.NextDouble() < 0.10) factor *= 2 + rng.NextDouble() * 8; // stalled cases build a realistic backlog
            var resolveAt = created.AddHours(slaHours * factor);
            var canceled = rng.NextDouble() < 0.03;

            var street = Streets[rng.Next(Streets.Length)];
            var req = new ServiceRequest
            {
                Title = cs.Titles[rng.Next(cs.Titles.Length)],
                Address = $"{rng.Next(100, 9900)} {street}",
                RequesterName = $"{First[rng.Next(First.Length)]} {Last[rng.Next(Last.Length)]}",
                Category = categories[ci], District = districts[di], Priority = prio,
                CreatedAt = created, DueAt = due, UpdatedAt = created
            };
            req.Description = Describe(cs.Name, req.Address, prio);

            var assignee = rng.NextDouble() < 0.97 ? staff[rng.Next(staff.Count)] : null;
            var assignedAt = created.AddMinutes(rng.Next(10, 180));
            var ev = req.Events;
            ev.Add(new RequestEvent { At = created, Kind = EventKind.Created, ToStatus = RequestStatus.New, Actor = "Resident portal", Note = $"Submitted by {req.RequesterName}." });

            if (canceled && resolveAt < now)
            {
                req.Status = RequestStatus.Canceled;
                var t = created.AddHours(Math.Max(2, slaHours * 0.2));
                if (t > now) t = now.AddMinutes(-30);
                ev.Add(new RequestEvent { At = t, Kind = EventKind.StatusChanged, FromStatus = RequestStatus.New, ToStatus = RequestStatus.Canceled, Actor = supervisor.DisplayName, Note = "Duplicate of an existing request." });
                req.UpdatedAt = t;
            }
            else if (resolveAt < now)
            {
                var who = assignee ?? staff[0];
                req.AssigneeId = who.Id;
                ev.Add(new RequestEvent { At = assignedAt, Kind = EventKind.Assigned, Actor = supervisor.DisplayName, Note = $"Assigned to {who.DisplayName}" });
                var started = assignedAt.AddHours(1 + rng.NextDouble() * Math.Max(1, (resolveAt - assignedAt).TotalHours * 0.3));
                if (started >= resolveAt) started = resolveAt.AddMinutes(-20);
                ev.Add(new RequestEvent { At = started, Kind = EventKind.StatusChanged, FromStatus = RequestStatus.New, ToStatus = RequestStatus.InProgress, Actor = who.DisplayName, Note = "Work started." });
                ev.Add(new RequestEvent { At = resolveAt, Kind = EventKind.StatusChanged, FromStatus = RequestStatus.InProgress, ToStatus = RequestStatus.Resolved, Actor = who.DisplayName, Note = cs.Resolutions[rng.Next(cs.Resolutions.Length)] });
                req.ResolvedAt = resolveAt;
                req.Status = RequestStatus.Resolved;
                req.UpdatedAt = resolveAt;
                var closedAt = resolveAt.AddHours(24 + rng.NextDouble() * 96);
                if (rng.NextDouble() < 0.65 && closedAt < now)
                {
                    ev.Add(new RequestEvent { At = closedAt, Kind = EventKind.StatusChanged, FromStatus = RequestStatus.Resolved, ToStatus = RequestStatus.Closed, Actor = supervisor.DisplayName });
                    req.Status = RequestStatus.Closed;
                    req.UpdatedAt = closedAt;
                }
            }
            else
            {
                var ageHours = (now - created).TotalHours;
                var status = ageHours < 8 ? RequestStatus.New : (rng.NextDouble() switch { < 0.2 => RequestStatus.New, < 0.78 => RequestStatus.InProgress, _ => RequestStatus.OnHold });
                if (status == RequestStatus.New && ageHours < 24 && rng.NextDouble() < 0.6) assignee = null;
                req.Status = status;
                if (assignee is not null && assignedAt >= now) assignee = null;
                if (status != RequestStatus.New) assignee ??= staff[rng.Next(staff.Count)];
                req.AssigneeId = assignee?.Id;
                if (assignee is not null)
                    ev.Add(new RequestEvent { At = assignedAt < now ? assignedAt : created, Kind = EventKind.Assigned, Actor = supervisor.DisplayName, Note = $"Assigned to {assignee.DisplayName}" });
                if (status != RequestStatus.New)
                {
                    var who = assignee!;
                    var started = created.AddMinutes(Math.Min(ageHours * 60 * 0.5, 240 + rng.Next(0, 600)));
                    ev.Add(new RequestEvent { At = started, Kind = EventKind.StatusChanged, FromStatus = RequestStatus.New, ToStatus = RequestStatus.InProgress, Actor = who.DisplayName, Note = "Work started." });
                    req.UpdatedAt = started;
                    if (status == RequestStatus.OnHold)
                    {
                        var hold = started.AddMinutes(Math.Min((now - started).TotalMinutes * 0.5, 600));
                        ev.Add(new RequestEvent { At = hold, Kind = EventKind.StatusChanged, FromStatus = RequestStatus.InProgress, ToStatus = RequestStatus.OnHold, Actor = who.DisplayName, Note = HoldReasons[rng.Next(HoldReasons.Length)] });
                        req.UpdatedAt = hold;
                    }
                }
                else if (assignee is not null) req.UpdatedAt = assignedAt < now ? assignedAt : created;
            }
            requests.Add(req);
        }

        var seq = 0;
        foreach (var r in requests.OrderBy(r => r.CreatedAt))
            r.Number = $"SR-{r.CreatedAt:yyyy}-{++seq:00000}";

        db.Requests.AddRange(requests);
        await db.SaveChangesAsync(ct);
    }

    private static readonly string[] HoldReasons =
        ["Waiting on materials from the warehouse.", "Waiting for the property owner to grant access.", "Pending utility locate ticket.", "Awaiting applicant documents."];

    private static string Describe(string category, string address, RequestPriority p) =>
        $"{category} reported at {address}. " + p switch
        {
            RequestPriority.Urgent => "Resident describes an immediate safety concern and requests same-day attention.",
            RequestPriority.High => "The issue is affecting daily use of the area and is getting worse.",
            RequestPriority.Low => "Not urgent, reported for routine follow-up.",
            _ => "Resident asks for a status update when work is scheduled."
        };

    private static Func<Random, int> Weighted(int[] weights)
    {
        var total = weights.Sum();
        return rng =>
        {
            var n = rng.Next(total);
            for (var i = 0; i < weights.Length; i++)
            {
                if (n < weights[i]) return i;
                n -= weights[i];
            }
            return weights.Length - 1;
        };
    }
}
