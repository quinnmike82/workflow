using static Iss.Workflow.WorkflowAction;
using static Iss.Workflow.Samples.Permit.PermitStatus;

namespace Iss.Workflow.Samples.Permit;

/// <summary>
/// 4-level module: a higher level can always act on behalf of a lower one.
/// Pending Officer, Senior clicks "Send to next level" → jumps straight to Manager.
/// </summary>
public static class PermitWorkflow
{
    // Module-specific action.
    public static readonly WorkflowAction SendToNext = new("SEND_TO_NEXT");

    // MUST be declared before Definition: static fields initialize top to bottom.
    private static readonly Ladder<PermitApplication, PermitStatus> Levels = new(
        (PendingOfficer,  Actors.LicensingOfficer),
        (PendingSenior,   Actors.LicensingSenior),
        (PendingManager,  Actors.LicensingManager),
        (PendingDirector, Actors.LicensingDirector));

    public static readonly WorkflowDefinition<PermitApplication, PermitStatus> Definition = new(Draft, [Approved, Rejected])
    {
        { Draft,               Submit,   Actors.Applicant, PendingOfficer, IsOwner },
        { ReturnedToApplicant, Resubmit, Actors.Applicant, PendingOfficer, IsOwner },

        Levels.Escalate(SendToNext, done: Approved),       // up to the level right above the acting user's level
        Levels.Back(Return, belowFirst: ReturnedToApplicant), // back to the level right below
        Levels.RouteDown(),                                // route to any lower level
        Levels.To(Reject, Rejected),                       // anyone at level >= current can reject
    };

    private static bool IsOwner(PermitApplication app, UserContext user) => app.ApplicantId == user.UserId;
}
