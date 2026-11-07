namespace Iss.Workflow.Samples.Rts;

/// <summary>The module's existing persistence (a DbContext in practice). Extracted as an interface so the sample runs.</summary>
public interface IRtsStore
{
    Task<RtsApplication> GetAsync(Guid id, CancellationToken ct);

    void IssueCertificate(Guid applicationId);

    /// <summary>One transaction: status + history + domain changes. Throws a concurrency error if Version changed.</summary>
    Task SaveChangesAsync(CancellationToken ct);
}

public interface IShipEligibility
{
    Task<bool> IsEligibleAsync(Guid applicationId, CancellationToken ct);
}

public interface IEmailSender
{
    Task SendAsync(string to, string template, object model, CancellationToken ct);
}

public sealed class BusinessRuleException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record RtsFormDto(string? VesselName, string? Documents, decimal? Fee, string? Remarks);

/// <summary>
/// The module's existing service. Workflow work is reduced to: pick the definition + override the hooks you need.
/// All RTS-specific logic still lives here, placed according to when it runs.
/// </summary>
public sealed class RtsService(WorkflowEngine engine, IRtsStore store, IShipEligibility ships, IEmailSender email)
    : WorkflowModule<RtsApplication, RtsStatus>(engine)
{
    protected override WorkflowDefinition<RtsApplication, RtsStatus> GetWorkflow(RtsApplication app) =>
        RtsWorkflow.Definition; // or pick a variant per customer/application type

    protected override Task<RtsApplication> LoadAsync(string id, CancellationToken ct) =>
        store.GetAsync(Guid.Parse(id), ct);

    protected override Task SaveChangesAsync(CancellationToken ct) => store.SaveChangesAsync(ct);

    // 1. Business validation, before the status changes
    protected override async Task BeforeAsync(RtsApplication app, WorkflowAction action, UserContext user, CancellationToken ct)
    {
        if (action == WorkflowAction.Submit && !await ships.IsEligibleAsync(app.Id, ct))
            throw new BusinessRuleException("SHIP_NOT_ELIGIBLE");
    }

    // 2. Data changes, same transaction as the status change
    protected override Task OnTransitionedAsync(RtsApplication app, WorkflowResult<RtsStatus> r, UserContext user, CancellationToken ct)
    {
        switch (r)
        {
            case { StepCompleted: true, To: RtsStatus.Approved }:
                store.IssueCertificate(app.Id);
                break;

            case { Action.Key: "REQUEST_INFO" }:
                app.RfiCount++;
                break;
        }

        return Task.CompletedTask;
    }

    // 3. After commit: email, external systems
    protected override async Task AfterCommitAsync(RtsApplication app, WorkflowResult<RtsStatus> r, UserContext user, CancellationToken ct)
    {
        if (r.Reached(RtsStatus.Approved))
            await email.SendAsync(app.ApplicantId, "rts-approved", new { app.Id }, ct);
        else if (r.Reached(RtsStatus.PendingApplicant))
            await email.SendAsync(app.ApplicantId, "rts-request-info", new { app.Id }, ct);
    }

    /// <summary>Data for the FE: which buttons to show, which sections are editable, parallel progress.</summary>
    public async Task<object> GetScreenAsync(Guid id, UserContext user, CancellationToken ct)
    {
        var app = await store.GetAsync(id, ct);
        var edit = RtsWorkflow.Access.GetEditPermission(app, user);

        return new
        {
            app.Id,
            Status = app.Status.ToString(),
            app.Version,
            Actions = await Engine.GetAvailableActionsAsync(app, RtsWorkflow.Definition, user, ct),
            Progress = await Engine.GetProgressAsync(app, RtsWorkflow.Definition, ct),
            Editable = edit.All ? ["*"] : edit.Sections.ToArray(),
        };
    }

    /// <summary>Save the form (not a workflow action): check per-section permission, then bump Version.</summary>
    public async Task SaveFormAsync(Guid id, RtsFormDto dto, UserContext user, CancellationToken ct)
    {
        var app = await store.GetAsync(id, ct);

        var changed = new List<string>();
        if (dto.VesselName is not null) changed.Add(RtsSections.Vessel);
        if (dto.Documents is not null) changed.Add(RtsSections.Documents);
        if (dto.Fee is not null) changed.Add(RtsSections.Fees);
        if (dto.Remarks is not null) changed.Add(RtsSections.Remarks);

        RtsWorkflow.Access.EnsureCanEdit(app, user, changed);

        // ... map the dto onto the entity as today ...
        app.MarkEdited();
        await store.SaveChangesAsync(ct);
    }
}
