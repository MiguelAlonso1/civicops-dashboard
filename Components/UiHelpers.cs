using CivicOps.Web.Data;
using CivicOps.Web.Services;
using MudBlazor;

namespace CivicOps.Web.Components;

public static class UiHelpers
{
    public static Color StatusColor(RequestStatus s) => s switch
    {
        RequestStatus.New => Color.Info,
        RequestStatus.InProgress => Color.Primary,
        RequestStatus.OnHold => Color.Warning,
        RequestStatus.Resolved => Color.Success,
        _ => Color.Default
    };

    public static string StatusIcon(RequestStatus s) => s switch
    {
        RequestStatus.New => Icons.Material.Rounded.NewReleases,
        RequestStatus.InProgress => Icons.Material.Rounded.Autorenew,
        RequestStatus.OnHold => Icons.Material.Rounded.PauseCircle,
        RequestStatus.Resolved => Icons.Material.Rounded.CheckCircle,
        RequestStatus.Closed => Icons.Material.Rounded.Archive,
        _ => Icons.Material.Rounded.Cancel
    };

    public static Color PriorityColor(RequestPriority p) => p switch
    {
        RequestPriority.Urgent => Color.Error,
        RequestPriority.High => Color.Warning,
        RequestPriority.Normal => Color.Info,
        _ => Color.Default
    };

    public static Color RoleColor(string role) => role switch
    {
        Roles.Admin => Color.Tertiary,
        Roles.Supervisor => Color.Secondary,
        Roles.Agent => Color.Primary,
        _ => Color.Default
    };

    /// <summary>"3d 4h", "5h 12m", "42m".</summary>
    public static string Duration(TimeSpan t)
    {
        t = t.Duration();
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{Math.Max(1, (int)t.TotalMinutes)}m";
    }

    public static string Hours(double h) => h < 48 ? $"{h:0.#} h" : $"{h / 24:0.#} d";

    public static string SlaText(RequestStatus status, DateTime dueAt, DateTime? resolvedAt, DateTime now)
    {
        if (resolvedAt is { } r) return r <= dueAt ? $"Met, {Duration(dueAt - r)} early" : $"Missed by {Duration(r - dueAt)}";
        if (!StatusRules.IsOpen(status)) return "n/a";
        return dueAt < now ? $"Overdue by {Duration(now - dueAt)}" : $"Due in {Duration(dueAt - now)}";
    }
}
