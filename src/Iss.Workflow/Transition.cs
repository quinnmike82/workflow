namespace Iss.Workflow;

/// <summary>One row of the workflow table: at <see cref="From"/>, whoever performs <see cref="Action"/> moves it to <see cref="To"/>.</summary>
public sealed record Transition<TEntity, TStatus>
    where TEntity : IWorkflowEntity<TStatus>
    where TStatus : struct, Enum
{
    public required TStatus From { get; init; }

    public required WorkflowAction Action { get; init; }

    public required Approval Approval { get; init; }

    public required TStatus To { get; init; }

    /// <summary>Extra data-driven condition (application owner, amount...). Null = always true.</summary>
    public Func<TEntity, UserContext, bool>? Guard { get; init; }

    /// <summary>Guard name shown in errors/diagrams. Captured automatically from the expression via CallerArgumentExpression.</summary>
    public string? GuardName { get; init; }

    /// <summary>
    /// When a user matches several rows with the same (From, Action), the highest Rank wins.
    /// Ladder sets Rank = level, so a user holding several levels goes the furthest.
    /// </summary>
    public int Rank { get; init; }

    /// <summary>True when a higher level acts on behalf of the pending level.</summary>
    public bool OnBehalf { get; init; }

    /// <summary>Declaration order, assigned by <see cref="WorkflowDefinition{TEntity,TStatus}"/>.</summary>
    public int Order { get; internal init; }
}
