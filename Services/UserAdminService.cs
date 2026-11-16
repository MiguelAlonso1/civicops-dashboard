using System.Net.Mail;
using CivicOps.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CivicOps.Web.Services;

public sealed record UserRow(string Id, string Email, string DisplayName, string Role, bool IsLocked);

/// <summary>User management (Admin only) on top of ASP.NET Core Identity's UserManager, with audit entries.</summary>
public sealed class UserAdminService(UserManager<ApplicationUser> users, IDbContextFactory<CivicOpsDb> factory, TimeProvider clock)
{
    private static OpResult Deny() => OpResult.Fail(Failure.Forbidden, "Only admins can manage users.");

    private static bool IsLocked(ApplicationUser u, DateTimeOffset now) => u.LockoutEnd is { } end && end > now;

    public async Task<IReadOnlyList<UserRow>> ListAsync(Actor? actor, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanManageUsers) return [];
        var now = clock.GetUtcNow();
        var list = await users.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct);
        var rows = new List<UserRow>(list.Count);
        foreach (var u in list)
        {
            var role = (await users.GetRolesAsync(u)).FirstOrDefault() ?? "";
            rows.Add(new UserRow(u.Id, u.Email ?? u.UserName ?? "", u.DisplayName, role, IsLocked(u, now)));
        }
        return rows;
    }

    public async Task<OpResult> CreateAsync(Actor? actor, string? email, string? displayName, string? role, string? tempPassword, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanManageUsers) return Deny();
        email = email?.Trim();
        displayName = displayName?.Trim();
        if (string.IsNullOrEmpty(email) || email.Length > 100 || !MailAddress.TryCreate(email, out var addr) || addr.Address != email)
            return OpResult.Fail(Failure.Invalid, "Enter a valid email address (100 characters max).");
        if (string.IsNullOrEmpty(displayName) || displayName.Length > 100) return OpResult.Fail(Failure.Invalid, "Display name is required (100 characters max).");
        if (role is null || !Roles.All.Contains(role)) return OpResult.Fail(Failure.Invalid, "Choose a role.");
        if (string.IsNullOrEmpty(tempPassword)) return OpResult.Fail(Failure.Invalid, "Enter a temporary password.");
        if (await users.FindByEmailAsync(email) is not null) return OpResult.Fail(Failure.Invalid, "A user with that email already exists.");

        var u = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = displayName };
        var created = await users.CreateAsync(u, tempPassword);
        if (!created.Succeeded) return OpResult.Fail(Failure.Invalid, string.Join(" ", created.Errors.Select(e => e.Description)));
        var added = await users.AddToRoleAsync(u, role);
        if (!added.Succeeded)
        {
            await users.DeleteAsync(u);
            return OpResult.Fail(Failure.Invalid, string.Join(" ", added.Errors.Select(e => e.Description)));
        }
        await AuditAsync(actor, "User created", email, $"{displayName}, role {role}", ct);
        return OpResult.Success;
    }

    public async Task<OpResult> ChangeRoleAsync(Actor? actor, string userId, string? role, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanManageUsers) return Deny();
        if (role is null || !Roles.All.Contains(role)) return OpResult.Fail(Failure.Invalid, "Choose a role.");
        var u = await users.FindByIdAsync(userId);
        if (u is null) return OpResult.Fail(Failure.NotFound, "User not found.");
        var current = await users.GetRolesAsync(u);
        if (current.Count == 1 && current[0] == role) return OpResult.Success;
        if (u.Id == actor.UserId) return OpResult.Fail(Failure.Invalid, "You cannot change your own role. Ask another admin.");
        if (current.Contains(Roles.Admin) && role != Roles.Admin && await AdminCountAsync() <= 1)
            return OpResult.Fail(Failure.Invalid, "This is the last Admin. Promote someone else first.");

        var removed = await users.RemoveFromRolesAsync(u, current);
        if (!removed.Succeeded) return OpResult.Fail(Failure.Invalid, string.Join(" ", removed.Errors.Select(e => e.Description)));
        var added = await users.AddToRoleAsync(u, role);
        if (!added.Succeeded) return OpResult.Fail(Failure.Invalid, string.Join(" ", added.Errors.Select(e => e.Description)));
        await users.UpdateSecurityStampAsync(u); // existing sessions pick up the new role at the next validation
        await AuditAsync(actor, "User role changed", u.Email ?? u.Id, $"{string.Join(", ", current)} to {role}", ct);
        return OpResult.Success;
    }

    /// <summary>Soft disable: a locked user cannot sign in but keeps their history and assignments.</summary>
    public async Task<OpResult> SetLockedAsync(Actor? actor, string userId, bool locked, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanManageUsers) return Deny();
        var u = await users.FindByIdAsync(userId);
        if (u is null) return OpResult.Fail(Failure.NotFound, "User not found.");
        if (locked)
        {
            if (u.Id == actor.UserId) return OpResult.Fail(Failure.Invalid, "You cannot lock your own account.");
            if (await users.IsInRoleAsync(u, Roles.Admin) && await AdminCountAsync(activeOnly: true) <= 1 && !IsLocked(u, clock.GetUtcNow()))
                return OpResult.Fail(Failure.Invalid, "This is the last active Admin and cannot be locked.");
            await users.SetLockoutEnabledAsync(u, true);
            await users.SetLockoutEndDateAsync(u, DateTimeOffset.MaxValue);
            await users.UpdateSecurityStampAsync(u);
        }
        else
        {
            await users.SetLockoutEndDateAsync(u, null);
            await users.ResetAccessFailedCountAsync(u);
        }
        await AuditAsync(actor, locked ? "User locked" : "User unlocked", u.Email ?? u.Id, null, ct);
        return OpResult.Success;
    }

    public async Task<OpResult> DeleteAsync(Actor? actor, string userId, CancellationToken ct = default)
    {
        if (actor is null || !actor.CanManageUsers) return Deny();
        var u = await users.FindByIdAsync(userId);
        if (u is null) return OpResult.Fail(Failure.NotFound, "User not found.");
        if (u.Id == actor.UserId) return OpResult.Fail(Failure.Invalid, "You cannot delete your own account.");
        if (await users.IsInRoleAsync(u, Roles.Admin) && await AdminCountAsync() <= 1)
            return OpResult.Fail(Failure.Invalid, "This is the last Admin and cannot be deleted.");

        var role = (await users.GetRolesAsync(u)).FirstOrDefault();
        var result = await users.DeleteAsync(u); // assigned requests become unassigned (FK SET NULL)
        if (!result.Succeeded) return OpResult.Fail(Failure.Invalid, string.Join(" ", result.Errors.Select(e => e.Description)));
        await AuditAsync(actor, "User deleted", u.Email ?? u.Id, $"{u.DisplayName}, role {role}", ct);
        return OpResult.Success;
    }

    private async Task<int> AdminCountAsync(bool activeOnly = false)
    {
        var now = clock.GetUtcNow();
        var admins = await users.GetUsersInRoleAsync(Roles.Admin);
        return activeOnly ? admins.Count(a => !IsLocked(a, now)) : admins.Count;
    }

    private async Task AuditAsync(Actor actor, string action, string key, string? details, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        Audit.Add(db, actor, clock.GetUtcNow().UtcDateTime, action, "User", key, details);
        await db.SaveChangesAsync(ct);
    }
}
