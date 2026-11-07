namespace Iss.Workflow;

/// <summary>The module's existing entity (RtsApplication, MortgageApplication...) implements this interface.</summary>
public interface IWorkflowEntity<TStatus> where TStatus : struct, Enum
{
    /// <summary>Id used to look up history, usually <c>Id.ToString()</c>.</summary>
    string WorkflowEntityId { get; }

    TStatus Status { get; set; }

    /// <summary>
    /// Called on EVERY action (even when the status does not change, e.g. only 1/2 departments approved).
    /// Bump the concurrency token (e.g. <c>Version++</c> configured as IsConcurrencyToken) so that when two people
    /// approve at the same time, one gets DbUpdateConcurrencyException instead of a lost update.
    /// </summary>
    void MarkWorkflowChanged();
}
