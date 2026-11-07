# Workflow

Shared workflow/approval building block for .NET 10 modules. Code-first and generic: no JSON, no configuration tables.

- **Flow** (status → action → who → next status): one C# table per module, checked by the compiler.
- **Role + Department**: declared once in `Actors.cs`, shared by every module.
- **Parallel / Quorum / multi-level on-behalf / routing**: handled by the engine; modules only declare.
- **Form edit permission** per status × role: a second table, separate from the workflow.
- **Logic on status change** (data updates, emails): hooks in the module's service.

```
src/Workflow                      core, no dependencies
src/Workflow.EntityFrameworkCore  history store using the existing DbContext
samples/Workflow.Samples          RTS (sequential + parallel + RFI) and Permit (4 levels)
tests/Workflow.Tests              xUnit, 47 tests
```

---

## Adding a workflow to a module (3 steps)

**1. The existing entity implements `IWorkflowEntity<TStatus>`**

```csharp
public sealed class RtsApplication : IWorkflowEntity<RtsStatus>
{
    public RtsStatus Status { get; set; }
    public int Version { get; private set; }           // IsConcurrencyToken() in EF

    string IWorkflowEntity<RtsStatus>.WorkflowEntityId => Id.ToString();
    void IWorkflowEntity<RtsStatus>.MarkWorkflowChanged() => Version++;
}
```

**2. One `XxxWorkflow.cs` file**

```csharp
public static readonly WorkflowDefinition<RtsApplication, RtsStatus> Definition = new(Draft, [Approved, Rejected])
{
    // From              Action       Who                                                 To                 Condition
    { Draft,             Submit,      Actors.Applicant,                                   PendingRegistry,   IsOwner },
    { PendingRegistry,   Approve,     Actors.RegistryChecker,                             PendingDualReview },
    { PendingDualReview, Approve,     All(Actors.MarineReviewer, Actors.FinanceReviewer), PendingDirector },
    { PendingDualReview, Reject,      Any(Actors.MarineReviewer, Actors.FinanceReviewer), Rejected },
    { PendingDirector,   Approve,     Actors.MarineDirector,                              Approved,          NotOwner },
};
```

**3. The service inherits `WorkflowModule<TEntity, TStatus>`** (optional) and overrides the hooks it needs. See `samples/.../Rts/RtsService.cs`.

That's it. The `All_workflows_in_modules_are_valid` test picks up the new workflow automatically.

---

## Approval types

| Need | Write |
|---|---|
| One person | `Actors.X` |
| One of several groups | `Any(Actors.A, Actors.B)` |
| One person per group (parallel) | `All(Actors.A, Actors.B)` |
| N of M groups | `Quorum(2, Actors.A, Actors.B, Actors.C)` |
| Data-driven (owner, amount...) | add a 5th column: `IsOwner` or `app => app.Amount < 10_000` |

When a user matches several rows with the same (status, action), selection order is: **highest Rank → guarded row → declaration order.** That lets you write "small amounts are approved by the Manager directly, otherwise go to the Director":

```csharp
{ PendingManager, Approve, Actors.Manager, PendingDirector },
{ PendingManager, Approve, Actors.Manager, Approved, app => app.Amount < 10_000 },
```

## Multiple levels, higher acts on behalf of lower (`Ladder`)

```csharp
public static readonly WorkflowAction SendToNext = new("SEND_TO_NEXT");   // module-specific action

// Declare BEFORE Definition (static fields initialize top to bottom)
private static readonly Ladder<PermitApplication, PermitStatus> Levels = new(
    (PendingOfficer,  Actors.LicensingOfficer),
    (PendingSenior,   Actors.LicensingSenior),
    (PendingManager,  Actors.LicensingManager),
    (PendingDirector, Actors.LicensingDirector));

public static readonly WorkflowDefinition<PermitApplication, PermitStatus> Definition = new(Draft, [Approved, Rejected])
{
    { Draft, Submit, Actors.Applicant, PendingOfficer, IsOwner },
    Levels.Escalate(SendToNext, done: Approved),           // L2 acts while at L1 → goes to L3
    Levels.Back(Return, belowFirst: ReturnedToApplicant),  // back to the level right below
    Levels.RouteDown(),                                    // ROUTE_TO_<status>: to any lower level
    Levels.To(Reject, Rejected),                           // anyone at level ≥ current can reject
};
```

- `reach: 1`: may act on behalf of at most 1 level below. `reach: 0`: no acting on behalf.
- History records `OnBehalf = true` with From/To, so you can see who acted for whom.
- A user holding several levels acts at the highest one.

## Form edit permission

Editing the form does not change status, so edit permission lives in its own table. No matching row means read-only. A user matching several rows gets the union of their sections.

```csharp
public static readonly FormAccess<RtsApplication, RtsStatus> Access = new()
{
    { Draft,             Actors.Applicant,                                Edit.All, IsOwner },
    { PendingDualReview, [Actors.MarineReviewer, Actors.FinanceReviewer], Edit.Sections(RtsSections.Remarks) },
    { PendingDualReview, Actors.FinanceReviewer,                          Edit.Sections(RtsSections.Fees) },
};
```

- FE: `Access.GetEditPermission(app, user)` returns `All` / `Sections` to enable or lock fields.
- BE on save: `Access.EnsureCanEdit(app, user, changedSections)`, then `app.MarkEdited()` (bumps Version).
- Several people editing at once: the FE sends `version` back → if someone saved or approved first, return 409. No record locking.
- Test: `Access.Validate(RtsSections.All)` catches mistyped section keys.

## Logic on status change: where it goes

| Hook (`WorkflowModule`) | Runs when | Put here |
|---|---|---|
| `BeforeAsync` | Permission checked, status not changed yet | Business validation. Throwing changes nothing |
| `OnTransitionedAsync` | Status changed, not saved yet | Data updates, certificates, enqueue jobs/outbox (roll back together) |
| `AfterCommitAsync` | Committed | Emails, external systems. **Never send email before commit** |

```csharp
switch (result)
{
    case { StepCompleted: true, To: RtsStatus.Approved }: store.IssueCertificate(app.Id); break;
    case { Action.Key: "REQUEST_INFO" }:                  app.RfiCount++; break;
}
```

## Per-customer variants

```csharp
public static readonly WorkflowDefinition<RtsApplication, RtsStatus> WithoutDirector = new(
    Definition, except: t => t.From == PendingDirector || (t.From == PendingDualReview && t.Action == Approve))
{
    { PendingDualReview, Approve, All(Actors.MarineReviewer, Actors.FinanceReviewer), Approved },
};
```

Pick the variant in the service's `GetWorkflow(entity)`.

---

## Application integration

```csharp
// OnModelCreating
modelBuilder.ApplyConfiguration(new WorkflowHistoryEntryConfiguration());   // or map to an existing audit table

// Program.cs
services.AddScoped<IWorkflowHistoryStore>(sp => new EfWorkflowHistoryStore(sp.GetRequiredService<AppDbContext>()));
services.AddScoped<WorkflowEngine>();
services.AddScoped<RtsService>();

// Startup: fail fast on a broken workflow
WorkflowValidator.EnsureValid(WorkflowValidator.FindAll(typeof(RtsWorkflow).Assembly));
```

Error → HTTP mapping: `InvalidAction`/`AlreadyActed`/`SlotAlreadyFilled` → 409, `NotAuthorized` → 403, `GuardFailed` → 422, `DbUpdateConcurrencyException` → 409.

If a suitable audit/history table already exists, implement `IWorkflowHistoryStore` yourself (2 methods); no new table needed.

## How the engine stores parallel approval

There is no task table and no Round column. Every action writes one history entry (`StepCompleted = false` when only partially approved). "Who has approved in the current step" = the entries after the latest time the application **entered** its current status. After RFI → Resubmit the application enters that status again, so earlier-round approvals stop counting automatically.

Every action calls `MarkWorkflowChanged()` (even when the status does not change), so when two people approve at the same time one of them gets a concurrency error instead of a lost update.

## Rules when changing code

- **Do not rename** status enum members, `Actor.Key`, `WorkflowAction.Key` or `WorkflowDefinition.Name` once data exists: they are written to history.
- Removing or merging a status → write a script to migrate applications currently in that status.
- `Ladder` and workflow variants must be declared **after** the static fields they use.
- Diagram for BA review: `RtsWorkflow.Definition.ToMermaid()`.

## Test status

- Core, samples and the 47 tests build and pass on .NET 10.0.114 (plus 2 mutation checks: deliberately breaking priority ordering and round reset makes the tests fail).
- The build environment had no NuGet access, so the tests ran through a minimal xUnit shim, and **the `Workflow.EntityFrameworkCore` project was not compiled**. Run `dotnet test` once in your application repository to confirm the package versions (`Microsoft.EntityFrameworkCore.Relational`, `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`) match your solution.
