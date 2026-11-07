using Iss.Workflow.Samples;
using Iss.Workflow.Samples.Rts;
using Xunit;
using static Iss.Workflow.WorkflowAction;

namespace Iss.Workflow.Tests;

public class RtsWorkflowTests
{
    private readonly InMemoryWorkflowHistoryStore _history = new();
    private readonly WorkflowEngine _engine;

    private readonly UserContext _applicant = Users.Applicant();
    private readonly UserContext _registry = Users.In("registry-1", Dept.Registry, Role.Checker);
    private readonly UserContext _marine = Users.In("marine-1", Dept.Marine, Role.Officer);
    private readonly UserContext _marine2 = Users.In("marine-2", Dept.Marine, Role.SeniorOfficer);
    private readonly UserContext _finance = Users.In("finance-1", Dept.Finance, Role.FinanceOfficer);
    private readonly UserContext _director = Users.In("director-1", Dept.Marine, Role.Director);

    public RtsWorkflowTests() => _engine = new WorkflowEngine(_history);

    private RtsApplication NewApp() => new() { ApplicantId = _applicant.UserId };

    private Task<WorkflowResult<RtsStatus>> Do(RtsApplication app, WorkflowAction action, UserContext user,
        WorkflowDefinition<RtsApplication, RtsStatus>? wf = null) =>
        _engine.ExecuteAsync(app, wf ?? RtsWorkflow.Definition, action, user);

    private async Task<RtsApplication> AtDualReview()
    {
        var app = NewApp();
        await Do(app, Submit, _applicant);
        await Do(app, Approve, _registry);
        return app;
    }

    [Fact]
    public async Task Happy_path_goes_through_parallel_review()
    {
        var app = await AtDualReview();
        Assert.Equal(RtsStatus.PendingDualReview, app.Status);

        var marine = await Do(app, Approve, _marine);
        Assert.False(marine.StepCompleted);
        Assert.Equal(RtsStatus.PendingDualReview, app.Status);
        Assert.Equal(1, marine.Approved);
        Assert.Equal(2, marine.Required);

        var finance = await Do(app, Approve, _finance);
        Assert.True(finance.StepCompleted);
        Assert.Equal(RtsStatus.PendingDirector, app.Status);

        var director = await Do(app, Approve, _director);
        Assert.True(director.Reached(RtsStatus.Approved));
        Assert.Equal(RtsStatus.Approved, app.Status);
    }

    [Fact]
    public async Task Every_action_bumps_version_even_when_status_does_not_change()
    {
        var app = await AtDualReview();
        var before = app.Version;

        await Do(app, Approve, _marine);

        Assert.Equal(RtsStatus.PendingDualReview, app.Status);
        Assert.Equal(before + 1, app.Version);
    }

    [Fact]
    public async Task Submit_by_someone_else_fails_guard()
    {
        var app = NewApp();
        var ex = await Assert.ThrowsAsync<WorkflowException>(() => Do(app, Submit, Users.Applicant("other")));
        Assert.Equal(WorkflowError.GuardFailed, ex.Error);
        Assert.Contains("IsOwner", ex.Message);
        Assert.Equal(RtsStatus.Draft, app.Status);
        Assert.Empty(_history.Entries);
    }

    [Fact]
    public async Task Wrong_department_is_not_authorized()
    {
        var app = NewApp();
        await Do(app, Submit, _applicant);

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => Do(app, Approve, _marine));
        Assert.Equal(WorkflowError.NotAuthorized, ex.Error);
    }

    [Fact]
    public async Task Role_must_belong_to_the_same_department()
    {
        var app = await AtDualReview();
        await Do(app, Approve, _marine);
        await Do(app, Approve, _finance);

        // Director in Finance + Officer in Marine must NOT combine into a Director in Marine
        var mixed = Users.With("mixed", (Dept.Finance, Role.Director), (Dept.Marine, Role.Officer));
        var ex = await Assert.ThrowsAsync<WorkflowException>(() => Do(app, Approve, mixed));
        Assert.Equal(WorkflowError.NotAuthorized, ex.Error);
    }

    [Fact]
    public async Task Invalid_action_for_status()
    {
        var app = NewApp();
        var ex = await Assert.ThrowsAsync<WorkflowException>(() => Do(app, Approve, _director));
        Assert.Equal(WorkflowError.InvalidAction, ex.Error);
    }

    [Fact]
    public async Task Second_officer_of_same_department_cannot_fill_the_slot_again()
    {
        var app = await AtDualReview();
        await Do(app, Approve, _marine);

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => Do(app, Approve, _marine2));
        Assert.Equal(WorkflowError.SlotAlreadyFilled, ex.Error);
    }

    [Fact]
    public async Task One_person_cannot_approve_for_two_departments()
    {
        var app = await AtDualReview();
        var both = Users.With("both", (Dept.Marine, Role.Officer), (Dept.Finance, Role.FinanceOfficer));

        await Do(app, Approve, both);
        var ex = await Assert.ThrowsAsync<WorkflowException>(() => Do(app, Approve, both));
        Assert.Equal(WorkflowError.AlreadyActed, ex.Error);
    }

    [Fact]
    public async Task Request_info_and_resubmit_starts_a_new_round()
    {
        var app = await AtDualReview();
        await Do(app, Approve, _marine);
        await Do(app, RequestInfo, _finance);
        Assert.Equal(RtsStatus.PendingApplicant, app.Status);

        await Do(app, Resubmit, _applicant);
        Assert.Equal(RtsStatus.PendingDualReview, app.Status);

        // Marine's earlier approval no longer counts
        var finance = await Do(app, Approve, _finance);
        Assert.False(finance.StepCompleted);
        Assert.Equal(RtsStatus.PendingDualReview, app.Status);

        // And the same Marine officer may approve again in the new round
        var marine = await Do(app, Approve, _marine);
        Assert.True(marine.StepCompleted);
        Assert.Equal(RtsStatus.PendingDirector, app.Status);
    }

    [Fact]
    public async Task Any_department_can_reject_during_parallel_review()
    {
        var app = await AtDualReview();
        await Do(app, Approve, _marine);
        await Do(app, Reject, _finance);
        Assert.Equal(RtsStatus.Rejected, app.Status);
    }

    [Fact]
    public async Task Director_cannot_approve_own_application()
    {
        var app = new RtsApplication { ApplicantId = _director.UserId };
        var directorAsApplicant = Users.With(_director.UserId, (Dept.Marine, Role.Director));
        var owner = new UserContext(_director.UserId,
            [.. directorAsApplicant.Memberships, new Membership("External", nameof(Role.Applicant))]);

        await Do(app, Submit, owner);
        await Do(app, Approve, _registry);
        await Do(app, Approve, _marine);
        await Do(app, Approve, _finance);

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => Do(app, Approve, owner));
        Assert.Equal(WorkflowError.GuardFailed, ex.Error);
    }

    [Fact]
    public async Task Available_actions_match_what_execute_allows()
    {
        var app = await AtDualReview();

        var marineActions = await _engine.GetAvailableActionsAsync(app, RtsWorkflow.Definition, _marine);
        Assert.Equal("APPROVE,REJECT,REQUEST_INFO", string.Join(",", marineActions.Select(a => a.Action.Key).OrderBy(k => k)));

        var approve = Assert.Single(marineActions, a => a.Action == Approve);
        Assert.False(approve.CompletesStep);
        Assert.Equal(RtsStatus.PendingDirector, approve.Target);

        Assert.Empty(await _engine.GetAvailableActionsAsync(app, RtsWorkflow.Definition, _applicant));

        await Do(app, Approve, _marine);
        var afterApprove = await _engine.GetAvailableActionsAsync(app, RtsWorkflow.Definition, _marine2);
        Assert.DoesNotContain(afterApprove, a => a.Action == Approve);

        var financeApprove = Assert.Single(
            await _engine.GetAvailableActionsAsync(app, RtsWorkflow.Definition, _finance), a => a.Action == Approve);
        Assert.True(financeApprove.CompletesStep);
    }

    [Fact]
    public async Task Progress_shows_who_approved_and_who_is_pending()
    {
        var app = await AtDualReview();
        await Do(app, Approve, _marine);

        var progress = Assert.Single(await _engine.GetProgressAsync(app, RtsWorkflow.Definition));
        Assert.Equal(Approve, progress.Action);
        Assert.Equal("MarineReviewer", string.Join(",", progress.ApprovedBy));
        Assert.Equal("FinanceReviewer", string.Join(",", progress.Pending));
    }

    [Fact]
    public async Task History_records_partial_and_completed_steps()
    {
        var app = await AtDualReview();
        await Do(app, Approve, _marine);
        await Do(app, Approve, _finance);

        var last = _history.Entries.TakeLast(2).ToList();
        Assert.False(last[0].StepCompleted);
        Assert.Equal("MarineReviewer", last[0].ActorKey);
        Assert.Equal("PendingDualReview", last[0].ToStatus);
        Assert.True(last[1].StepCompleted);
        Assert.Equal("PendingDirector", last[1].ToStatus);
    }

    [Fact]
    public async Task Customer_variant_skips_director()
    {
        var app = NewApp();
        await Do(app, Submit, _applicant, RtsWorkflow.WithoutDirector);
        await Do(app, Approve, _registry, RtsWorkflow.WithoutDirector);
        await Do(app, Approve, _marine, RtsWorkflow.WithoutDirector);
        var result = await Do(app, Approve, _finance, RtsWorkflow.WithoutDirector);

        Assert.True(result.Reached(RtsStatus.Approved));
        Assert.Equal(RtsWorkflow.Definition.Name, RtsWorkflow.WithoutDirector.Name);
    }
}
