using System.Security.Claims;
using CivicOps.Web.Data;

namespace CivicOps.Web.Services;

/// <summary>The authenticated caller, reduced to what authorization decisions need.</summary>
public sealed record Actor(string UserId, string Name, string Role)
{
    public const string DisplayNameClaim = "display_name";

    public static Actor? From(ClaimsPrincipal? p)
    {
        if (p?.Identity?.IsAuthenticated != true) return null;
        var role = Roles.All.FirstOrDefault(p.IsInRole);
        var id = p.FindFirstValue(ClaimTypes.NameIdentifier);
        if (role is null || id is null) return null;
        return new Actor(id, p.FindFirstValue(DisplayNameClaim) ?? p.Identity.Name ?? "User", role);
    }

    public bool IsAdmin => Role == Roles.Admin;

    /// <summary>Viewer is read-only; everyone else may add notes and update statuses (agents only on their own work).</summary>
    public bool CanEdit => Role is Roles.Admin or Roles.Supervisor or Roles.Agent;
    public bool CanAssign => Role is Roles.Admin or Roles.Supervisor;
    public bool CanAdminister => Role == Roles.Admin;
}

public enum Failure { None, Forbidden, NotFound, Invalid }

public readonly record struct OpResult(Failure Failure, string? Message = null)
{
    public bool Ok => Failure == Failure.None;
    public static OpResult Success { get; } = new(Failure.None);
    public static OpResult Fail(Failure f, string message) => new(f, message);
}

/// <summary>Status workflow. Closed and Canceled are terminal.</summary>
public static class StatusRules
{
    private static readonly Dictionary<RequestStatus, RequestStatus[]> Map = new()
    {
        [RequestStatus.New] = [RequestStatus.InProgress, RequestStatus.Canceled],
        [RequestStatus.InProgress] = [RequestStatus.OnHold, RequestStatus.Resolved, RequestStatus.Canceled],
        [RequestStatus.OnHold] = [RequestStatus.InProgress, RequestStatus.Canceled],
        [RequestStatus.Resolved] = [RequestStatus.Closed, RequestStatus.InProgress],
        [RequestStatus.Closed] = [],
        [RequestStatus.Canceled] = [],
    };

    public static IReadOnlyList<RequestStatus> AllowedNext(RequestStatus from) => Map[from];

    public static bool CanTransition(RequestStatus from, RequestStatus to) => Map[from].Contains(to);

    /// <summary>Putting a request on hold, resolving it, canceling it or reopening it needs a written reason.</summary>
    public static bool RequiresNote(RequestStatus from, RequestStatus to) =>
        to is RequestStatus.OnHold or RequestStatus.Resolved or RequestStatus.Canceled
        || (from == RequestStatus.Resolved && to == RequestStatus.InProgress);

    public static bool IsOpen(RequestStatus s) => s is RequestStatus.New or RequestStatus.InProgress or RequestStatus.OnHold;

    public static string Label(RequestStatus s) => s switch
    {
        RequestStatus.InProgress => "In progress",
        RequestStatus.OnHold => "On hold",
        _ => s.ToString()
    };
}
