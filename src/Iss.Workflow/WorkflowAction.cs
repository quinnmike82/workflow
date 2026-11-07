namespace Iss.Workflow;

/// <summary>
/// Action key. Use the built-in actions below, or let a module declare its own:
/// <code>public static readonly WorkflowAction SendToNext = new("SEND_TO_NEXT");</code>
/// The key is written to history and returned to the FE, so do not change it once data exists.
/// </summary>
public readonly record struct WorkflowAction(string Key)
{
    public static readonly WorkflowAction Submit = new("SUBMIT");
    public static readonly WorkflowAction Approve = new("APPROVE");
    public static readonly WorkflowAction Reject = new("REJECT");
    public static readonly WorkflowAction Return = new("RETURN");
    public static readonly WorkflowAction RequestInfo = new("REQUEST_INFO");
    public static readonly WorkflowAction Resubmit = new("RESUBMIT");
    public static readonly WorkflowAction Withdraw = new("WITHDRAW");

    public override string ToString() => Key;
}
