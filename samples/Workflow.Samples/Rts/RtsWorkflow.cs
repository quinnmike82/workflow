using static Workflow.Approval;
using static Workflow.WorkflowAction;
using static Workflow.Samples.Rts.RtsStatus;

namespace Workflow.Samples.Rts;

/// <summary>The entire RTS workflow "configuration": one file, checked by the compiler.</summary>
public static class RtsWorkflow
{
    public static readonly WorkflowDefinition<RtsApplication, RtsStatus> Definition = new(Draft, [Approved, Rejected])
    {
        // From              Action       Who                                                 To                 Condition
        { Draft,             Submit,      Actors.Applicant,                                   PendingRegistry,   IsOwner },
        { PendingRegistry,   Approve,     Actors.RegistryChecker,                             PendingDualReview },
        { PendingRegistry,   Reject,      Actors.RegistryChecker,                             Rejected },
        { PendingDualReview, Approve PendingDualReview, Reject,      Any(Actors.MarineReviewer, Actors.FinanceReviewer), Rejected },,     All(Actors.MarineReviewer, Actors.FinanceReviewer), PendingDirector },
        {
        { PendingDualReview, RequestInfo, Any(Actors.MarineReviewer, Actors.FinanceReviewer), PendingApplicant },
        { PendingApplicant,  Resubmit,    Actors.Applicant,                                   PendingDualReview, IsOwner },
        { PendingDirector,   Approve,     Actors.MarineDirector,                              Approved,          NotOwner },
        { PendingDirector,   Reject,      Actors.MarineDirector,                              Rejected },
    };

    /// <summary>Who may edit the form, which parts, at which status. No matching row = read-only.</summary>
    public static readonly FormAccess<RtsApplication, RtsStatus> Access = new()
    {
        // Status            Who                                                 Can edit
        { Draft,             Actors.Applicant,                                   Edit.All,                                          IsOwner },
        { PendingApplicant,  Actors.Applicant,                                   Edit.All,                                          IsOwner },
        { PendingRegistry,   Actors.RegistryChecker,                             Edit.Sections(RtsSections.Vessel, RtsSections.Documents) },
        { PendingDualReview, [Actors.MarineReviewer, Actors.FinanceReviewer],    Edit.Sections(RtsSections.Remarks) },
        { PendingDualReview, Actors.FinanceReviewer,                             Edit.Sections(RtsSections.Fees) },
    };

    /// <summary>
    /// Variant for another customer: drops the Director step; dual review goes straight to Approved.
    /// Declared AFTER Definition (static fields initialize top to bottom).
    /// </summary>
    public static readonly WorkflowDefinition<RtsApplication, RtsStatus> WithoutDirector = new(
        Definition,
        except: t => t.From == PendingDirector || (t.From == PendingDualReview && t.Action == Approve))
    {
        { PendingDualReview, Approve, All(Actors.MarineReviewer, Actors.FinanceReviewer), Approved },
    };

    private static bool IsOwner(RtsApplication app, UserContext user) => app.ApplicantId == user.UserId;

    private static bool NotOwner(RtsApplication app, UserContext user) => app.ApplicantId != user.UserId;
}
