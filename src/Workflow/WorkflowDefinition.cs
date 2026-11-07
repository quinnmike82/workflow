using System.Collections;
using System.Runtime.CompilerServices;
using System.Text;

namespace Workflow;

/// <summary>Non-generic part, so validators/tests can scan every workflow in an assembly.</summary>
public interface IWorkflowDefinition
{
    string Name { get; }

    IReadOnlyList<string> Validate();

    string ToMermaid();
}

/// <summary>
/// Workflow table of one module. Declare it with a collection initializer:
/// <code>
/// public static readonly WorkflowDefinition&lt;RtsApplication, RtsStatus&gt; Definition = new(Draft, [Approved, Rejected])
/// {
///     { Draft,           Submit,  Actors.Applicant,       PendingRegistry, IsOwner },
///     { PendingRegistry, Approve, Actors.RegistryChecker, PendingReview },
///     Levels.Escalate(SendToNext, done: Approved),
/// };
/// </code>
/// </summary>
public sealed class WorkflowDefinition<TEntity, TStatus> : IWorkflowDefinition, IEnumerable<Transition<TEntity, TStatus>>
    where TEntity : IWorkflowEntity<TStatus>
    where TStatus : struct, Enum
{
    private readonly List<Transition<TEntity, TStatus>> _transitions = [];
    private readonly HashSet<TStatus> _terminal;

    /// <param name="initial">Initial status of a new entity.</param>
    /// <param name="terminal">Final statuses (no further actions).</param>
    /// <param name="name">Name written to history. Defaults to the entity name. Do not change it once data exists.</param>
    public WorkflowDefinition(TStatus initial, IEnumerable<TStatus> terminal, string? name = null)
    {
        Initial = initial;
        _terminal = [.. terminal];
        Name = string.IsNullOrWhiteSpace(name) ? typeof(TEntity).Name : name;
    }

    /// <summary>
    /// Creates a variant of an existing workflow (e.g. for another customer): copies the rows, drops rows
    /// matching <paramref name="except"/>, then add new rows with a collection initializer.
    /// Keeps the same <see cref="Name"/> so history stays continuous.
    /// </summary>
    public WorkflowDefinition(WorkflowDefinition<TEntity, TStatus> source, Func<Transition<TEntity, TStatus>, bool>? except = null)
        : this(source.Initial, source.Terminal, source.Name)
    {
        foreach (var transition in source._transitions)
            if (except is null || !except(transition))
                Add(transition);
    }

    public string Name { get; }

    public TStatus Initial { get; }

    public IReadOnlySet<TStatus> Terminal => _terminal;

    public IReadOnlyList<Transition<TEntity, TStatus>> Transitions => _transitions;

    public void Add(TStatus from, WorkflowAction action, Approval approval, TStatus to)
        => Add(new Transition<TEntity, TStatus> { From = from, Action = action, Approval = approval, To = to });

    public void Add(TStatus from, WorkflowAction action, Approval approval, TStatus to,
        Func<TEntity, UserContext, bool> when,
        [CallerArgumentExpression(nameof(when))] string? whenName = null)
    {
        ArgumentNullException.ThrowIfNull(when);
        Add(new Transition<TEntity, TStatus>
        {
            From = from, Action = action, Approval = approval, To = to, Guard = when, GuardName = whenName,
        });
    }

    public void Add(TStatus from, WorkflowAction action, Approval approval, TStatus to,
        Func<TEntity, bool> when,
        [CallerArgumentExpression(nameof(when))] string? whenName = null)
    {
        ArgumentNullException.ThrowIfNull(when);
        Add(from, action, approval, to, (entity, _) => when(entity), whenName);
    }

    /// <summary>Escape hatch: build a <see cref="Transition{TEntity,TStatus}"/> yourself (set Rank, OnBehalf...).</summary>
    public void Add(Transition<TEntity, TStatus> transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(transition.Approval, "transition.Approval (check static field declaration order)");
        if (string.IsNullOrWhiteSpace(transition.Action.Key))
            throw new ArgumentException("Transition action key is required.", nameof(transition));

        _transitions.Add(transition with { Order = _transitions.Count });
    }

    /// <summary>Adds many rows at once: the output of <see cref="Ladder{TEntity,TStatus}"/> or a module's own generator.</summary>
    public void Add(IEnumerable<Transition<TEntity, TStatus>> transitions)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        foreach (var transition in transitions) Add(transition);
    }

    public IEnumerable<Transition<TEntity, TStatus>> TransitionsFrom(TStatus status) =>
        _transitions.Where(t => EqualityComparer<TStatus>.Default.Equals(t.From, status));

    public IReadOnlyList<string> Validate() => WorkflowValidator.Validate(this);

    /// <summary>Exports a Mermaid flowchart for BA/customer review.</summary>
    public string ToMermaid()
    {
        var sb = new StringBuilder();
        sb.AppendLine("flowchart TD");
        sb.AppendLine($"    START(( )) --> {Initial}");
        foreach (var status in _terminal) sb.AppendLine($"    {status}([{status}])");

        foreach (var t in _transitions)
        {
            var label = new StringBuilder($"{t.Action.Key} · {t.Approval}");
            if (t.OnBehalf) label.Append(" (on behalf)");
            if (t.GuardName is not null) label.Append($" · when {t.GuardName}");
            sb.AppendLine($"    {t.From} -->|\"{label.ToString().Replace("\"", "#quot;")}\"| {t.To}");
        }

        return sb.ToString();
    }

    public IEnumerator<Transition<TEntity, TStatus>> GetEnumerator() => _transitions.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
