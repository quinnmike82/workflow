using Workflow.Samples;
using Workflow.Samples.Permit;
using Workflow.Samples.Rts;
using Xunit;
using static Workflow.Approval;
using static Workflow.Samples.Rts.RtsStatus;
using static Workflow.WorkflowAction;

namespace Workflow.Tests;

/// <summary>This test replaces "validate JSON at runtime": any broken workflow fails in CI.</summary>
public class DefinitionValidationTests
{
    [Fact]
    public void All_workflows_in_modules_are_valid()
    {
        var workflows = WorkflowValidator.FindAll(typeof(RtsWorkflow).Assembly);

        Assert.Equal(3, workflows.Count); // Rts, Rts WithoutDirector, Permit
        WorkflowValidator.EnsureValid(workflows);
    }

    [Fact]
    public void Form_access_sections_exist_in_schema()
    {
        Assert.Empty(RtsWorkflow.Access.Validate(RtsSections.All));
    }

    [Fact]
    public void Detects_dead_end()
    {
        var wf = new WorkflowDefinition<RtsApplication, RtsStatus>(Draft, [Approved])
        {
            { Draft, Submit, Actors.Applicant, PendingRegistry },
        };

        Assert.Contains(wf.Validate(), e => e.Contains("Dead end") && e.Contains("PendingRegistry"));
        Assert.Contains(wf.Validate(), e => e.Contains("Terminal status Approved is never reached"));
    }

    [Fact]
    public void Detects_ambiguous_rows()
    {
        var wf = new WorkflowDefinition<RtsApplication, RtsStatus>(Draft, [Approved, Rejected])
        {
            { Draft, Submit, Actors.Applicant, Approved },
            { Draft, Submit, Actors.Applicant, Rejected },
        };

        Assert.Contains(wf.Validate(), e => e.Contains("never used"));
    }

    [Fact]
    public void Detects_transition_out_of_terminal()
    {
        var wf = new WorkflowDefinition<RtsApplication, RtsStatus>(Draft, [Approved])
        {
            { Draft, Submit, Actors.Applicant, Approved },
            { Approved, Withdraw, Actors.Applicant, Draft },
        };

        Assert.Contains(wf.Validate(), e => e.Contains("out of terminal"));
    }

    [Fact]
    public void Ensure_valid_throws_with_workflow_name()
    {
        var wf = new WorkflowDefinition<RtsApplication, RtsStatus>(Draft, [Approved], name: "BrokenFlow")
        {
            { Draft, Submit, Actors.Applicant, PendingRegistry },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowValidator.EnsureValid([wf]));
        Assert.Contains("BrokenFlow", ex.Message);
    }

    [Fact]
    public void Approval_rejects_bad_configuration()
    {
        Assert.Throws<ArgumentException>(() => All(Actors.MarineReviewer, Actors.MarineReviewer));
        Assert.Throws<ArgumentOutOfRangeException>(() => Quorum(3, Actors.MarineReviewer, Actors.FinanceReviewer));
    }

    [Fact]
    public void Guard_name_is_captured_from_expression()
    {
        var submit = RtsWorkflow.Definition.Transitions.First(t => t.Action == Submit);
        Assert.Equal("IsOwner", submit.GuardName);
    }

    [Fact]
    public void Mermaid_export_contains_edges()
    {
        var mermaid = PermitWorkflow.Definition.ToMermaid();

        Assert.StartsWith("flowchart TD", mermaid);
        Assert.Contains("PendingOfficer -->|\"SEND_TO_NEXT · LicensingSenior (on behalf)\"| PendingManager", mermaid);
    }

    [Fact]
    public void Quorum_two_of_three()
    {
        var approval = Quorum(2, Actors.MarineReviewer, Actors.FinanceReviewer, Actors.RegistryChecker);
        Assert.Equal(2, approval.Required);
        Assert.True(approval.IsParallel);
        Assert.Equal("2 OF (MarineReviewer, FinanceReviewer, RegistryChecker)", approval.ToString());
    }
}
