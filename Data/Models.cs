using Microsoft.AspNetCore.Identity;

namespace CivicOps.Web.Data;

public enum RequestStatus { New = 0, InProgress = 1, OnHold = 2, Resolved = 3, Closed = 4, Canceled = 5 }

public enum RequestPriority { Low = 0, Normal = 1, High = 2, Urgent = 3 }

public enum EventKind { Created = 0, StatusChanged = 1, Assigned = 2, Note = 3 }

public static class Roles
{
    public const string Admin = "Admin";
    public const string Supervisor = "Supervisor";
    public const string Agent = "Agent";
    public const string Viewer = "Viewer";

    public static readonly string[] All = [Admin, Supervisor, Agent, Viewer];
}

public class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
}

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Department { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public List<SlaRule> SlaRules { get; set; } = [];
}

/// <summary>Target resolution time (hours) for one category at one priority.</summary>
public class SlaRule
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public Category? Category { get; set; }
    public RequestPriority Priority { get; set; }
    public int TargetHours { get; set; }
}

public class District
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int Population { get; set; }
}

public class ServiceRequest
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Address { get; set; } = "";
    public string RequesterName { get; set; } = "";
    public int CategoryId { get; set; }
    public Category? Category { get; set; }
    public int DistrictId { get; set; }
    public District? District { get; set; }
    public RequestPriority Priority { get; set; }
    public RequestStatus Status { get; set; }
    public string? AssigneeId { get; set; }
    public ApplicationUser? Assignee { get; set; }
    /// <summary>All timestamps are UTC (SQLite orders DateTime text correctly, DateTimeOffset it cannot).</summary>
    public DateTime CreatedAt { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<RequestEvent> Events { get; set; } = [];
}

public class RequestEvent
{
    public int Id { get; set; }
    public int ServiceRequestId { get; set; }
    public ServiceRequest? ServiceRequest { get; set; }
    public DateTime At { get; set; }
    public EventKind Kind { get; set; }
    public RequestStatus? FromStatus { get; set; }
    public RequestStatus? ToStatus { get; set; }
    public string Actor { get; set; } = "";
    public string? Note { get; set; }
}
