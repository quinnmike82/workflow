namespace Iss.Workflow;

/// <summary>
/// Multi-level approval chain. A higher level can always act on behalf of the pending level.
/// Ladder only GENERATES transition rows. The engine has no ladder-specific logic.
/// <code>
/// // Declare BEFORE Definition (static fields initialize top to bottom)
/// private static readonly Ladder&lt;Permit, PermitStatus&gt; Levels = new(
///     (PendingOfficer, Actors.Officer),
///     (PendingSenior,  Actors.Senior),
///     (PendingManager, Actors.Manager));
/// </code>
/// </summary>
public sealed class Ladder<TEntity, TStatus>
    where TEntity : IWorkflowEntity<TStatus>
    where TStatus : struct, Enum
{
    private readonly (TStatus Status, Actor Actor)[] _levels;

    /// <param name="levels">Lowest to highest: the status waiting for that level + that level's actor.</param>
    public Ladder(params (TStatus Status, Actor Actor)[] levels)
    {
        if (levels.Length == 0) throw new ArgumentException("At least one level is required.", nameof(levels));
        if (levels.Any(l => l.Actor is null))
            throw new ArgumentException("Level actor is null. Check static field declaration order.", nameof(levels));
        if (levels.Select(l => l.Status).Distinct().Count() != levels.Length)
            throw new ArgumentException("Each level must have its own status.", nameof(levels));

        _levels = levels.ToArray();
    }

    public IReadOnlyList<(TStatus Status, Actor Actor)> Levels => _levels;

    /// <summary>
    /// Moves to the level right ABOVE the acting user's level (not above the current status).
    /// Pending L1, L2 acts → L3. The top level acts → <paramref name="done"/>.
    /// </summary>
    /// <param name="reach">How many levels below one may act on behalf of. null = unlimited, 0 = own level only.</param>
    /// <param name="when">Guard applied to every generated row (optional).</param>
    public IEnumerable<Transition<TEntity, TStatus>> Escalate(WorkflowAction action, TStatus done,
        int? reach = null, Func<TEntity, UserContext, bool>? when = null, string? whenName = null)
        => Generate(action, reach, when, whenName,
            (_, actorLevel) => actorLevel + 1 < _levels.Length ? _levels[actorLevel + 1].Status : done);

    /// <summary>Anyone at a level ≥ the current level may act; fixed target. E.g. Reject → Rejected.</summary>
    public IEnumerable<Transition<TEntity, TStatus>> To(WorkflowAction action, TStatus target,
        int? reach = null, Func<TEntity, UserContext, bool>? when = null, string? whenName = null)
        => Generate(action, reach, when, whenName, (_, _) => target);

    /// <summary>Returns the application to the level right below the current one. At the lowest level → <paramref name="belowFirst"/>.</summary>
    public IEnumerable<Transition<TEntity, TStatus>> Back(WorkflowAction action, TStatus belowFirst,
        int? reach = null, Func<TEntity, UserContext, bool>? when = null, string? whenName = null)
        => Generate(action, reach, when, whenName,
            (currentLevel, _) => currentLevel == 0 ? belowFirst : _levels[currentLevel - 1].Status);

    /// <summary>
    /// Routes to ANY level below the current one (at L3 → choose L1 or L2).
    /// Each target is its own action: <c>{prefix}_{Status}</c>, e.g. <c>ROUTE_TO_PendingOfficer</c>.
    /// The FE shows a "Route to…" dropdown from availableActions.
    /// </summary>
    public IEnumerable<Transition<TEntity, TStatus>> RouteDown(string prefix = "ROUTE_TO",
        int? reach = null, Func<TEntity, UserContext, bool>? when = null, string? whenName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var rows = new List<Transition<TEntity, TStatus>>();
        for (var target = 0; target < _levels.Length - 1; target++)
        {
            var action = RouteAction(_levels[target].Status, prefix);
            var targetIndex = target;
            rows.AddRange(Generate(action, reach, when, whenName, (_, _) => _levels[targetIndex].Status)
                .Where(t => Index(t.From) > targetIndex));
        }

        return rows;
    }

    /// <summary>The action key <see cref="RouteDown"/> generates for a target, for modules/FE to reference.</summary>
    public static WorkflowAction RouteAction(TStatus target, string prefix = "ROUTE_TO") => new($"{prefix}_{target}");

    private int Index(TStatus status) =>
        Array.FindIndex(_levels, l => EqualityComparer<TStatus>.Default.Equals(l.Status, status));

    private List<Transition<TEntity, TStatus>> Generate(WorkflowAction action, int? reach,
        Func<TEntity, UserContext, bool>? when, string? whenName, Func<int, int, TStatus> target)
    {
        if (reach < 0) throw new ArgumentOutOfRangeException(nameof(reach));

        var rows = new List<Transition<TEntity, TStatus>>();
        for (var current = 0; current < _levels.Length; current++)
        for (var actorLevel = current; actorLevel < _levels.Length; actorLevel++)
        {
            if (reach is not null && actorLevel - current > reach) break;

            rows.Add(new Transition<TEntity, TStatus>
            {
                From = _levels[current].Status,
                Action = action,
                Approval = _levels[actorLevel].Actor,
                To = target(current, actorLevel),
                Rank = actorLevel,
                OnBehalf = actorLevel > current,
                Guard = when,
                GuardName = when is null ? null : whenName ?? "ladder guard",
            });
        }

        return rows;
    }
}
