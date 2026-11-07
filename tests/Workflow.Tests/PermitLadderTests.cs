using Workflow.Samples;
using Workflow.Samples.Permit;
using Xunit;
using static Workflow.Samples.Permit.PermitStatus;
using static Workflow.WorkflowAction;

namespace Workflow.Tests;

public class PermitLadderTests
{
    private readonly InMemoryWorkflowHistoryStore _history = new();
    private readonly WorkflowEngine _engine;

    private readonly UserContext _applicant = Users.Applicant();
    private readonly UserContext _officer = Users.In("l1", Dept.Licensing, Role.Officer);
    private readonly UserContext _senior = Users.In("l2", Dept.Licensing, Role.SeniorOfficer);
    private readonly UserContext _manager = Users.In("l3", Dept.Licensing, Role.Manager);
    private readonly UserContext _director = Users.In("l4", Dept.Licensing, Role.Director);

    private static readonly WorkflowAction SendToNext = PermitWorkflow.SendToNext;

    public PermitLadderTests() => _engine = new WorkflowEngine(_history);

    private Task<WorkflowResult<PermitStatus>> Do(PermitApplication app, WorkflowAction action, UserContext user,
        WorkflowDefinition<PermitApplication, PermitStatus>? wf = null) =>
        _engine.ExecuteAsync(app, wf ?? PermitWorkflow.Definition, action, user);

    private async Task<PermitApplication> Submitted(decimal amount = 0)
    {
        var app = new PermitApplication { ApplicantId = _applicant.UserId, Amount = amount };
        await Do(app, Submit, _applicant);
        Assert.Equal(PendingOfficer, app.Status);
        return app;
    }

    [Fact]
    public async Task Each_level_sends_to_the_next()
    {
        var app = await Submitted();
        await Do(app, SendToNext, _officer);
        Assert.Equal(PendingSenior, app.Status);
        await Do(app, SendToNext, _senior);
        Assert.Equal(PendingManager, app.Status);
        await Do(app, SendToNext, _manager);
        Assert.Equal(PendingDirector, app.Status);
        await Do(app, SendToNext, _director);
        Assert.Equal(Approved, app.Status);
    }

    [Fact]
    public async Task Level2_acting_at_level1_goes_straight_to_level3()
    {
        var app = await Submitted();

        var result = await Do(app, SendToNext, _senior);

        Assert.Equal(PendingManager, app.Status);
        Assert.True(result.OnBehalf);
        Assert.Equal("LicensingSenior", result.ActorKey);

        var entry = _history.Entries[^1];
        Assert.True(entry.OnBehalf);
        Assert.Equal("PendingOfficer", entry.FromStatus);
        Assert.Equal("PendingManager", entry.ToStatus);
    }

    [Fact]
    public async Task Top_level_acting_at_level1_approves()
    {
        var app = await Submitted();
        var result = await Do(app, SendToNext, _director);
        Assert.True(result.Reached(Approved));
    }

    [Fact]
    public async Task Lower_level_cannot_act_on_higher_level()
    {
        var app = await Submitted();
        await Do(app, SendToNext, _officer);

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => Do(app, SendToNext, _officer));
        Assert.Equal(WorkflowError.NotAuthorized, ex.Error);
    }

    [Fact]
    public async Task User_with_several_levels_uses_the_highest()
    {
        var app = await Submitted();
        var seniorAndManager = Users.With("multi", (Dept.Licensing, Role.SeniorOfficer), (Dept.Licensing, Role.Manager));

        await Do(app, SendToNext, seniorAndManager);

        Assert.Equal(PendingDirector, app.Status);
    }

    [Fact]
    public async Task Return_goes_one_level_down_and_then_to_applicant()
    {
        var app = await Submitted();
        await Do(app, SendToNext, _officer);
        Assert.Equal(PendingSenior, app.Status);

        await Do(app, Return, _senior);
        Assert.Equal(PendingOfficer, app.Status);

        await Do(app, Return, _officer);
        Assert.Equal(ReturnedToApplicant, app.Status);

        await Do(app, Resubmit, _applicant);
        Assert.Equal(PendingOfficer, app.Status);
    }

    [Fact]
    public async Task Higher_level_can_return_on_behalf()
    {
        var app = await Submitted();
        await Do(app, SendToNext, _officer); // PendingSenior

        var result = await Do(app, Return, _manager);

        Assert.Equal(PendingOfficer, app.Status);
        Assert.True(result.OnBehalf);
    }

    [Fact]
    public async Task Route_down_to_any_lower_level()
    {
        var app = await Submitted();
        await Do(app, SendToNext, _manager); // PendingDirector
        Assert.Equal(PendingDirector, app.Status);

        var actions = await _engine.GetAvailableActionsAsync(app, PermitWorkflow.Definition, _director);
        var routes = actions.Where(a => a.Action.Key.StartsWith("ROUTE_TO_", StringComparison.Ordinal))
            .Select(a => a.Target.ToString()).OrderBy(s => s);
        Assert.Equal("PendingManager,PendingOfficer,PendingSenior", string.Join(",", routes));

        await Do(app, Ladder<PermitApplication, PermitStatus>.RouteAction(PendingOfficer), _director);
        Assert.Equal(PendingOfficer, app.Status);
    }

    [Fact]
    public async Task Cannot_route_to_own_or_higher_level()
    {
        var app = await Submitted();
        await Do(app, SendToNext, _officer); // PendingSenior

        var ex = await Assert.ThrowsAsync<WorkflowException>(
            () => Do(app, Ladder<PermitApplication, PermitStatus>.RouteAction(PendingManager), _manager));
        Assert.Equal(WorkflowError.InvalidAction, ex.Error);
    }

    [Fact]
    public async Task Available_actions_show_the_real_target()
    {
        var app = await Submitted();

        var send = Assert.Single(
            await _engine.GetAvailableActionsAsync(app, PermitWorkflow.Definition, _senior), a => a.Action == SendToNext);

        Assert.Equal(PendingManager, send.Target);
        Assert.True(send.OnBehalf);
    }

    [Fact]
    public async Task Any_level_at_or_above_can_reject()
    {
        var app = await Submitted();
        await Do(app, Reject, _manager);
        Assert.Equal(Rejected, app.Status);
    }

    [Fact]
    public async Task Reach_limits_how_far_below_a_level_can_act()
    {
        var levels = new Ladder<PermitApplication, PermitStatus>(
            (PendingOfficer, Actors.LicensingOfficer),
            (PendingSenior, Actors.LicensingSenior),
            (PendingManager, Actors.LicensingManager),
            (PendingDirector, Actors.LicensingDirector));

        var wf = new WorkflowDefinition<PermitApplication, PermitStatus>(Draft, [Approved])
        {
            { Draft, Submit, Actors.Applicant, PendingOfficer },
            levels.Escalate(SendToNext, done: Approved, reach: 1),
        };
        Assert.Empty(wf.Validate());

        var app = new PermitApplication { ApplicantId = _applicant.UserId };
        await Do(app, Submit, _applicant, wf);

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => Do(app, SendToNext, _manager, wf));
        Assert.Equal(WorkflowError.NotAuthorized, ex.Error);

        await Do(app, SendToNext, _senior, wf);
        Assert.Equal(PendingManager, app.Status);
    }

    [Fact]
    public async Task Guarded_row_wins_over_unguarded_row_for_amount_routing()
    {
        // Custom example: small amounts are approved by the Manager directly, large ones go up to the Director.
        var wf = new WorkflowDefinition<PermitApplication, PermitStatus>(Draft, [Approved])
        {
            { Draft,           Submit,  Actors.Applicant,         PendingManager },
            { PendingManager,  Approve, Actors.LicensingManager,  PendingDirector },
            { PendingManager,  Approve, Actors.LicensingManager,  Approved, app => app.Amount < 10_000 },
            { PendingDirector, Approve, Actors.LicensingDirector, Approved },
        };
        Assert.Empty(wf.Validate());

        var small = new PermitApplication { ApplicantId = _applicant.UserId, Amount = 5_000 };
        await Do(small, Submit, _applicant, wf);
        await Do(small, Approve, _manager, wf);
        Assert.Equal(Approved, small.Status);

        var large = new PermitApplication { ApplicantId = _applicant.UserId, Amount = 50_000 };
        await Do(large, Submit, _applicant, wf);
        await Do(large, Approve, _manager, wf);
        Assert.Equal(PendingDirector, large.Status);
    }
}
