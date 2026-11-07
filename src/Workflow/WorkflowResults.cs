namespace Workflow;

/// <summary>Result of applying an action. The entity is updated in memory but not saved yet.</summary>
/// <param name="From">Status before the action.</param>
/// <param name="To">Status after the action. Equals <paramref name="From"/> when the step is not complete yet.</param>
/// <param name="Target">Target status of the transition row (reached once the step completes).</param>
/// <param name="StepCompleted">False when only part of a parallel approval has been collected.</param>
public sealed record WorkflowResult<TStatus>(
    TStatus From,
    TStatus To,
    TStatus Target,
    bool StepCompleted,
    WorkflowAction Action,
    string ActorKey,
    bool OnBehalf,
    int Approved,
    int Required)
    where TStatus : struct, Enum
{
    /// <summary>True when this action just moved the entity into <paramref name="status"/>. Use it to trigger side effects.</summary>
    public bool Reached(TStatus status) =>
        StepCompleted
        && !EqualityComparer<TStatus>.Default.Equals(From, To)
        && EqualityComparer<TStatus>.Default.Equals(To, status);
}

/// <summary>One FE button the current user is allowed to see.</summary>
public sealed record AvailableAction<TStatus>(
    WorkflowAction Action,
    TStatus Target,
    bool CompletesStep,
    string ActorKey,
    bool OnBehalf,
    int Approved,
    int Required)
    where TStatus : struct, Enum;

/// <summary>Parallel approval progress of the current step, so the FE can show "1/2 departments approved".</summary>
public sealed record StepProgress(
    WorkflowAction Action,
    int Required,
    IReadOnlyList<string> ApprovedBy,
    IReadOnlyList<string> Pending);

public enum WorkflowError
{
    /// <summary>The action does not exist at the current status. Suggested HTTP 409.</summary>
    InvalidAction,

    /// <summary>The user matches no actor. Suggested HTTP 403.</summary>
    NotAuthorized,

    /// <summary>Actor matches but the guard (data condition) is not satisfied. Suggested HTTP 422.</summary>
    GuardFailed,

    /// <summary>This user already acted in the current step. Suggested HTTP 409.</summary>
    AlreadyActed,

    /// <summary>Someone else already acted for the user's department/actor in this step. Suggested HTTP 409.</summary>
    SlotAlreadyFilled,
}

public sealed class WorkflowException(WorkflowError error, string status, WorkflowAction action, string message)
    : Exception(message)
{
    public WorkflowError Error { get; } = error;

    public string Status { get; } = status;

    public WorkflowAction Action { get; } = action;
}
