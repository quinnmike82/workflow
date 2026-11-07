using Iss.Workflow.Samples;
using Iss.Workflow.Samples.Rts;
using Xunit;
using static Iss.Workflow.WorkflowAction;

namespace Iss.Workflow.Tests;

public class FormAccessTests
{
    private readonly WorkflowEngine _engine = new(new InMemoryWorkflowHistoryStore());
    private readonly UserContext _applicant = Users.Applicant();
    private readonly UserContext _registry = Users.In("registry-1", Dept.Registry, Role.Checker);
    private readonly UserContext _marine = Users.In("marine-1", Dept.Marine, Role.Officer);
    private readonly UserContext _finance = Users.In("finance-1", Dept.Finance, Role.FinanceOfficer);

    private static FormAccess<RtsApplication, RtsStatus> Access => RtsWorkflow.Access;

    [Fact]
    public void Owner_can_edit_everything_in_draft_others_cannot()
    {
        var app = new RtsApplication { ApplicantId = _applicant.UserId };

        Assert.True(Access.GetEditPermission(app, _applicant).All);
        Assert.False(Access.GetEditPermission(app, Users.Applicant("someone-else")).CanEditAnything);
        Assert.False(Access.GetEditPermission(app, _marine).CanEditAnything);
    }

    [Fact]
    public async Task Sections_are_merged_when_user_matches_several_rows()
    {
        var app = new RtsApplication { ApplicantId = _applicant.UserId };
        await _engine.ExecuteAsync(app, RtsWorkflow.Definition, Submit, _applicant);
        await _engine.ExecuteAsync(app, RtsWorkflow.Definition, Approve, _registry);
        Assert.Equal(RtsStatus.PendingDualReview, app.Status);

        var finance = Access.GetEditPermission(app, _finance);
        Assert.True(finance.CanEdit(RtsSections.Fees));
        Assert.True(finance.CanEdit(RtsSections.Remarks));
        Assert.False(finance.CanEdit(RtsSections.Vessel));

        var marine = Access.GetEditPermission(app, _marine);
        Assert.True(marine.CanEdit(RtsSections.Remarks));
        Assert.False(marine.CanEdit(RtsSections.Fees));

        Assert.False(Access.GetEditPermission(app, _applicant).CanEditAnything);
    }

    [Fact]
    public async Task Ensure_can_edit_lists_denied_sections()
    {
        var app = new RtsApplication { ApplicantId = _applicant.UserId };
        await _engine.ExecuteAsync(app, RtsWorkflow.Definition, Submit, _applicant);
        await _engine.ExecuteAsync(app, RtsWorkflow.Definition, Approve, _registry);

        var ex = Assert.Throws<FormAccessException>(
            () => Access.EnsureCanEdit(app, _marine, [RtsSections.Remarks, RtsSections.Fees]));
        Assert.Equal(RtsSections.Fees, Assert.Single(ex.DeniedSections));

        Access.EnsureCanEdit(app, _marine, [RtsSections.Remarks]); // does not throw
    }

    [Fact]
    public void Read_only_once_approved()
    {
        var app = new RtsApplication { ApplicantId = _applicant.UserId, Status = RtsStatus.Approved };
        Assert.False(Access.GetEditPermission(app, _applicant).CanEditAnything);
        Assert.False(Access.GetEditPermission(app, _marine).CanEditAnything);
    }

    [Fact]
    public void Validate_reports_unknown_sections()
    {
        var access = new FormAccess<RtsApplication, RtsStatus>
        {
            { RtsStatus.Draft, Actors.Applicant, Edit.Sections("vesel") },
        };

        Assert.Contains(access.Validate(RtsSections.All), e => e.Contains("vesel"));
    }
}
