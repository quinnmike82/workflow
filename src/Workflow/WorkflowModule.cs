namespace Workflow;

/// <summary>
/// OPTIONAL base class for a module service. Fixes the order in which an action is processed so that
/// "on status change" logic always lives in the right place:
/// <list type="number">
/// <item>Engine checks permission + guard (nothing changed yet)</item>
/// <item><see cref="BeforeAsync"/>: business validation</item>
/// <item>Engine changes status + stages history</item>
/// <item><see cref="OnTransitionedAsync"/>: update data, create records, enqueue jobs/outbox (same transaction)</item>
/// <item><see cref="SaveChangesAsync"/>: commit once</item>
/// <item><see cref="AfterCommitAsync"/>: send emails, call external systems (cannot be rolled back)</item>
/// </list>
/// Modules that do not want to inherit can still call <see cref="WorkflowEngine"/> directly.
/// </summary>
public abstract class WorkflowModule<TEntity, TStatus>(WorkflowEngine engine)
    where TEntity : IWorkflowEntity<TStatus>
    where TStatus : struct, Enum
{
    protected WorkflowEngine Engine { get; } = engine;

    /// <summary>Workflow used for this entity. Override to pick a variant per customer/application type.</summary>
    protected abstract WorkflowDefinition<TEntity, TStatus> GetWorkflow(TEntity entity);

    protected abstract Task<TEntity> LoadAsync(string id, CancellationToken ct);

    /// <summary>Commit status + history + domain changes in ONE transaction.</summary>
    protected abstract Task SaveChangesAsync(CancellationToken ct);

    /// <summary>Business validation before the status changes. Throw to stop: nothing has been changed yet.</summary>
    protected virtual Task BeforeAsync(TEntity entity, WorkflowAction action, UserContext user, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>Status changed in memory, not saved yet. Update data/create records here so they roll back together.</summary>
    protected virtual Task OnTransitionedAsync(TEntity entity, WorkflowResult<TStatus> result, UserContext user, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>Already committed. Send emails, notifications, call external APIs. Failures here do not undo the committed action: log/retry.</summary>
    protected virtual Task AfterCommitAsync(TEntity entity, WorkflowResult<TStatus> result, UserContext user, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>One method for every action. Example endpoint: POST /api/{module}/{id}/actions/{actionKey}</summary>
    public async Task<WorkflowResult<TStatus>> ActAsync(
        string id, string actionKey, UserContext user, CancellationToken ct = default, string? comment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKey);

        var entity = await LoadAsync(id, ct);
        var workflow = GetWorkflow(entity);
        var action = new WorkflowAction(actionKey);

        await Engine.EnsureAllowedAsync(entity, workflow, action, user, ct);
        await BeforeAsync(entity, action, user, ct);

        var result = await Engine.ExecuteAsync(entity, workflow, action, user, ct, comment);
        await OnTransitionedAsync(entity, result, user, ct);

        await SaveChangesAsync(ct);

        await AfterCommitAsync(entity, result, user, ct);
        return result;
    }

    public async Task<IReadOnlyList<AvailableAction<TStatus>>> GetAvailableActionsAsync(
        string id, UserContext user, CancellationToken ct = default)
    {
        var entity = await LoadAsync(id, ct);
        return await Engine.GetAvailableActionsAsync(entity, GetWorkflow(entity), user, ct);
    }
}
