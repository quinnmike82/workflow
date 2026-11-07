namespace Iss.Workflow;

/// <summary>
/// One action history entry. This is the ONLY place parallel approval progress is stored:
/// "who has approved in the current step" is derived from these entries; no task table or Round column needed.
/// Map it to an existing audit table or a new one (see Iss.Workflow.EntityFrameworkCore).
/// </summary>
public sealed class WorkflowHistoryEntry
{
    /// <summary>Ascending. Used for ordering.</summary>
    public long Id { get; set; }

    public string WorkflowName { get; set; } = "";

    public string EntityId { get; set; } = "";

    public string Action { get; set; } = "";

    public string FromStatus { get; set; } = "";

    public string ToStatus { get; set; } = "";

    /// <summary>False = only partially recorded (e.g. 1/2 departments approved); status unchanged.</summary>
    public bool StepCompleted { get; set; }

    public string ActorKey { get; set; } = "";

    public bool OnBehalf { get; set; }

    public string UserId { get; set; } = "";

    public string? Comment { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}

public interface IWorkflowHistoryStore
{
    /// <summary>Full history of one entity, oldest → newest.</summary>
    Task<IReadOnlyList<WorkflowHistoryEntry>> GetAsync(string workflowName, string entityId, CancellationToken ct = default);

    /// <summary>
    /// Stages a new entry. Does NOT save. The module calls SaveChanges once so status, history and
    /// domain changes (certificate, billing...) share one transaction.
    /// </summary>
    void Add(WorkflowHistoryEntry entry);
}

/// <summary>In-memory store for module unit tests.</summary>
public sealed class InMemoryWorkflowHistoryStore : IWorkflowHistoryStore
{
    private readonly List<WorkflowHistoryEntry> _entries = [];
    private readonly Lock _lock = new();
    private long _nextId;

    public IReadOnlyList<WorkflowHistoryEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    public Task<IReadOnlyList<WorkflowHistoryEntry>> GetAsync(string workflowName, string entityId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<WorkflowHistoryEntry> result = _entries
                .Where(e => e.WorkflowName == workflowName && e.EntityId == entityId)
                .OrderBy(e => e.Id)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public void Add(WorkflowHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            entry.Id = ++_nextId;
            _entries.Add(entry);
        }
    }
}

/// <summary>Progress of the current step, derived from history.</summary>
internal sealed class StepState
{
    private readonly List<WorkflowHistoryEntry> _partial;

    private StepState(List<WorkflowHistoryEntry> partial) => _partial = partial;

    /// <summary>
    /// The current step starts at the latest time the entity ENTERED <paramref name="status"/>.
    /// Approvals recorded before that point (earlier rounds, before RFI/Return) no longer count.
    /// </summary>
    public static StepState Of(IReadOnlyList<WorkflowHistoryEntry> history, string status)
    {
        var start = 0;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].StepCompleted && history[i].ToStatus == status)
            {
                start = i + 1;
                break;
            }
        }

        var partial = new List<WorkflowHistoryEntry>();
        for (var i = start; i < history.Count; i++)
            if (!history[i].StepCompleted && history[i].FromStatus == status)
                partial.Add(history[i]);

        return new StepState(partial);
    }

    public bool UserActed(string action, string userId) =>
        _partial.Any(e => e.Action == action && e.UserId == userId);

    public HashSet<string> ActorKeys(string action) =>
        _partial.Where(e => e.Action == action).Select(e => e.ActorKey).ToHashSet();
}
