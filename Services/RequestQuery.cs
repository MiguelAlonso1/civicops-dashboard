using CivicOps.Web.Data;

namespace CivicOps.Web.Services;

public enum QuickFilter { All, Open, Overdue, Unassigned, Mine, Urgent }

public enum SortField { Number, Title, Category, District, Priority, Status, Assignee, Created, Due }

public sealed class RequestQuery
{
    public string? Search { get; set; }
    public QuickFilter Quick { get; set; } = QuickFilter.All;
    public RequestStatus? Status { get; set; }
    public int? CategoryId { get; set; }
    public int? DistrictId { get; set; }
    public RequestPriority? Priority { get; set; }
    public SortField Sort { get; set; } = SortField.Created;
    public bool Descending { get; set; } = true;
    public int Page { get; set; }
    public int PageSize { get; set; } = 10;

    public RequestQuery Clone() => (RequestQuery)MemberwiseClone();

    /// <summary>Query-string form used for the CSV export link (filters and sort only, no paging).</summary>
    public string ToQueryString()
    {
        var parts = new List<string>();
        void Add(string k, object? v)
        {
            if (v?.ToString() is { Length: > 0 } s) parts.Add($"{k}={Uri.EscapeDataString(s)}");
        }
        Add("search", Search);
        if (Quick != QuickFilter.All) Add("quick", Quick);
        Add("status", Status);
        Add("category", CategoryId);
        Add("district", DistrictId);
        Add("priority", Priority);
        Add("sort", Sort);
        Add("desc", Descending ? "true" : "false");
        return string.Join('&', parts);
    }

    public static RequestQuery FromQuery(IQueryCollection q)
    {
        var r = new RequestQuery { Search = q["search"].FirstOrDefault() };
        if (Enum.TryParse<QuickFilter>(q["quick"].FirstOrDefault(), true, out var qf)) r.Quick = qf;
        if (Enum.TryParse<RequestStatus>(q["status"].FirstOrDefault(), true, out var st)) r.Status = st;
        if (Enum.TryParse<RequestPriority>(q["priority"].FirstOrDefault(), true, out var pr)) r.Priority = pr;
        if (Enum.TryParse<SortField>(q["sort"].FirstOrDefault(), true, out var sf)) r.Sort = sf;
        if (int.TryParse(q["category"].FirstOrDefault(), out var c)) r.CategoryId = c;
        if (int.TryParse(q["district"].FirstOrDefault(), out var d)) r.DistrictId = d;
        if (bool.TryParse(q["desc"].FirstOrDefault(), out var desc)) r.Descending = desc;
        return r;
    }
}

public sealed record RequestRow(
    int Id, string Number, string Title, string Category, string District, RequestPriority Priority,
    RequestStatus Status, string? Assignee, DateTime CreatedAt, DateTime DueAt, DateTime? ResolvedAt, string? AssigneeId = null)
{
    public bool IsOverdue(DateTime now) => StatusRules.IsOpen(Status) && DueAt < now;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
