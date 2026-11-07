namespace Iss.Workflow.Samples.Permit;

public enum PermitStatus
{
    Draft = 0,
    PendingOfficer = 101,
    PendingSenior = 102,
    PendingManager = 103,
    PendingDirector = 104,
    ReturnedToApplicant = 150,
    Approved = 300,
    Rejected = 400,
}

public sealed class PermitApplication : IWorkflowEntity<PermitStatus>
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string ApplicantId { get; init; }

    public decimal Amount { get; init; }

    public PermitStatus Status { get; set; }

    public int Version { get; private set; }

    string IWorkflowEntity<PermitStatus>.WorkflowEntityId => Id.ToString();

    void IWorkflowEntity<PermitStatus>.MarkWorkflowChanged() => Version++;
}
