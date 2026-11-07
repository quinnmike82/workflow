namespace Iss.Workflow;

/// <summary>
/// Shared engine for every module. It knows nothing about business rules and never saves.
/// Register it as Scoped together with <see cref="IWorkflowHistoryStore"/>.
/// </summary>
public sealed class WorkflowEngine(IWorkflowHistoryStore history, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Checks permission + guard, records the approval, changes status when the step completes, and stages one history entry.
    /// Throws <see cref="WorkflowException"/> when not allowed (nothing is changed).
    /// The caller must call SaveChanges afterwards.
    /// </summary>
    public async Task<WorkflowResult<TStatus>> ExecuteAsync<TEntity, TStatus>(
        TEntity entity,
        WorkflowDefinition<TEntity, TStatus> workflow,
        WorkflowAction action,
        UserContext user,
        CancellationToken ct = default,
        string? comment = null)
        where TEntity : IWorkflowEntity<TStatus>
        where TStatus : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(user);

        var step = await LoadStepAsync(entity, workflow, ct);
        var decision = Decide(entity, workflow, action, user, step);
        if (decision.Error is { } error)
            throw new WorkflowException(error, entity.Status.ToString(), action, decision.Message!);

        var transition = decision.Transition!;
        var from = entity.Status;
        if (decision.CompletesStep) entity.Status = transition.To;
        entity.MarkWorkflowChanged();

        history.Add(new WorkflowHistoryEntry
        {
            WorkflowName = workflow.Name,
            EntityId = entity.WorkflowEntityId,
            Action = action.Key,
            FromStatus = from.ToString(),
            ToStatus = entity.Status.ToString(),
            StepCompleted = decision.CompletesStep,
            ActorKey = decision.Actor!.Key,
            OnBehalf = transition.OnBehalf,
            UserId = user.UserId,
            Comment = comment,
            OccurredAt = _clock.GetUtcNow(),
        });

        return new WorkflowResult<TStatus>(
            from, entity.Status, transition.To, decision.CompletesStep, action,
            decision.Actor.Key, transition.OnBehalf, decision.Approved, transition.Approval.Required);
    }

    /// <summary>Checks permission + guard like Execute but changes NOTHING. Use it before expensive business validation.</summary>
    public async Task EnsureAllowedAsync<TEntity, TStatus>(
        TEntity entity,
        WorkflowDefinition<TEntity, TStatus> workflow,
        WorkflowAction action,
        UserContext user,
        CancellationToken ct = default)
        where TEntity : IWorkflowEntity<TStatus>
        where TStatus : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(user);

        var step = await LoadStepAsync(entity, workflow, ct);
        var decision = Decide(entity, workflow, action, user, step);
        if (decision.Error is { } error)
            throw new WorkflowException(error, entity.Status.ToString(), action, decision.Message!);
    }

    /// <summary>Actions the current user can perform, for FE buttons. Uses the same logic as Execute, so they never diverge.</summary>
    public async Task<IReadOnlyList<AvailableAction<TStatus>>> GetAvailableActionsAsync<TEntity, TStatus>(
        TEntity entity,
        WorkflowDefinition<TEntity, TStatus> workflow,
        UserContext user,
        CancellationToken ct = default)
        where TEntity : IWorkflowEntity<TStatus>
        where TStatus : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(user);

        var step = await LoadStepAsync(entity, workflow, ct);
        var result = new List<AvailableAction<TStatus>>();

        foreach (var action in workflow.TransitionsFrom(entity.Status).Select(t => t.Action).Distinct())
        {
            var d = Decide(entity, workflow, action, user, step);
            if (d.Error is not null) continue;

            result.Add(new AvailableAction<TStatus>(
                action, d.Transition!.To, d.CompletesStep, d.Actor!.Key,
                d.Transition.OnBehalf, d.Approved, d.Transition.Approval.Required));
        }

        return result;
    }

    /// <summary>Progress of parallel steps at the current status.</summary>
    public async Task<IReadOnlyList<StepProgress>> GetProgressAsync<TEntity, TStatus>(
        TEntity entity,
        WorkflowDefinition<TEntity, TStatus> workflow,
        CancellationToken ct = default)
        where TEntity : IWorkflowEntity<TStatus>
        where TStatus : struct, Enum
    {
        var step = await LoadStepAsync(entity, workflow, ct);

        return workflow.TransitionsFrom(entity.Status)
            .Where(t => t.Approval.IsParallel)
            .Select(t =>
            {
                var done = step.ActorKeys(t.Action.Key);
                return new StepProgress(
                    t.Action,
                    t.Approval.Required,
                    t.Approval.Actors.Where(a => done.Contains(a.Key)).Select(a => a.Key).ToList(),
                    t.Approval.Actors.Where(a => !done.Contains(a.Key)).Select(a => a.Key).ToList());
            })
            .ToList();
    }

    private async Task<StepState> LoadStepAsync<TEntity, TStatus>(
        TEntity entity, WorkflowDefinition<TEntity, TStatus> workflow, CancellationToken ct)
        where TEntity : IWorkflowEntity<TStatus>
        where TStatus : struct, Enum
    {
        var entries = await history.GetAsync(workflow.Name, entity.WorkflowEntityId, ct);
        return StepState.Of(entries, entity.Status.ToString());
    }

    private static Decision<TEntity, TStatus> Decide<TEntity, TStatus>(
        TEntity entity,
        WorkflowDefinition<TEntity, TStatus> workflow,
        WorkflowAction action,
        UserContext user,
        StepState step)
        where TEntity : IWorkflowEntity<TStatus>
        where TStatus : struct, Enum
    {
        var rows = workflow.TransitionsFrom(entity.Status).Where(t => t.Action == action).ToList();
        if (rows.Count == 0)
            return Decision<TEntity, TStatus>.Fail(WorkflowError.InvalidAction,
                $"Action '{action}' is not available when status is '{entity.Status}'.");

        var forUser = rows.Where(t => t.Approval.Actors.Any(a => a.Matches(user))).ToList();
        if (forUser.Count == 0)
            return Decision<TEntity, TStatus>.Fail(WorkflowError.NotAuthorized,
                $"User '{user.UserId}' is not allowed to '{action}' when status is '{entity.Status}'.");

        var allowed = forUser.Where(t => t.Guard is null || t.Guard(entity, user)).ToList();
        if (allowed.Count == 0)
            return Decision<TEntity, TStatus>.Fail(WorkflowError.GuardFailed,
                $"Condition not met for '{action}': {string.Join(", ", forUser.Select(t => t.GuardName ?? "guard").Distinct())}.");

        if (step.UserActed(action.Key, user.UserId))
            return Decision<TEntity, TStatus>.Fail(WorkflowError.AlreadyActed,
                $"User '{user.UserId}' already did '{action}' in the current step.");

        var done = step.ActorKeys(action.Key);

        // Selection order: highest Rank → guarded rows before unguarded rows → declaration order.
        foreach (var t in allowed.OrderByDescending(t => t.Rank).ThenBy(t => t.Guard is null).ThenBy(t => t.Order))
        {
            var actor = t.Approval.Actors.FirstOrDefault(a => !done.Contains(a.Key) && a.Matches(user));
            if (actor is null) continue;

            var approved = t.Approval.Actors.Count(a => a.Key == actor.Key || done.Contains(a.Key));
            return Decision<TEntity, TStatus>.Ok(t, actor, approved >= t.Approval.Required, approved);
        }

        return Decision<TEntity, TStatus>.Fail(WorkflowError.SlotAlreadyFilled,
            $"Another user already did '{action}' for your department/role in the current step.");
    }

    private sealed record Decision<TEntity, TStatus>(
        WorkflowError? Error,
        string? Message,
        Transition<TEntity, TStatus>? Transition,
        Actor? Actor,
        bool CompletesStep,
        int Approved)
        where TEntity : IWorkflowEntity<TStatus>
        where TStatus : struct, Enum
    {
        public static Decision<TEntity, TStatus> Fail(WorkflowError error, string message) =>
            new(error, message, null, null, false, 0);

        public static Decision<TEntity, TStatus> Ok(Transition<TEntity, TStatus> t, Actor actor, bool completes, int approved) =>
            new(null, null, t, actor, completes, approved);
    }
}
