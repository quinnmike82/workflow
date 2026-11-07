namespace Workflow.Samples.Rts;

/// <summary>Fixed numeric values: do not renumber or delete once data exists.</summary>
public enum RtsStatus
{
    Draft = 0,
    PendingRegistry = 100,
    PendingDualReview = 101,
    PendingDirector = 102,
    PendingApplicant = 150,
    Approved = 300,
    Rejected = 400,
}

/// <summary>Sections of the RTS Form Builder schema.</summary>
public static class RtsSections
{
    public const string Vessel = "vessel";
    public const string Documents = "documents";
    public const string Fees = "fees";
    public const string Remarks = "reviewRemarks";

    public static readonly string[] All = [Vessel, Documents, Fees, Remarks];
}

/// <summary>The module's existing entity; only the interface is added.</summary>
public sealed class RtsApplication : IWorkflowEntity<RtsStatus>
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string ApplicantId { get; init; }

    public RtsStatus Status { get; set; }

    public int RfiCount { get; set; }

    /// <summary>Configure as IsConcurrencyToken() in EF. The FE sends this value back on action/save.</summary>
    public int Version { get; private set; }

    string IWorkflowEntity<RtsStatus>.WorkflowEntityId => Id.ToString();

    void IWorkflowEntity<RtsStatus>.MarkWorkflowChanged() => Version++;

    /// <summary>Saving the form must also bump the version so it conflicts with a concurrent approval.</summary>
    public void MarkEdited() => Version++;
}
