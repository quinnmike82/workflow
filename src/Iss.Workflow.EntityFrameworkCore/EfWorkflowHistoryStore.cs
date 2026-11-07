using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Iss.Workflow.EntityFrameworkCore;

/// <summary>
/// Store backed by the system's existing DbContext. <see cref="Add"/> only tracks the entity;
/// the module calls SaveChanges once so everything shares one transaction.
/// To reuse an existing audit table, implement <see cref="IWorkflowHistoryStore"/> yourself and map to that table.
/// </summary>
public sealed class EfWorkflowHistoryStore(DbContext db) : IWorkflowHistoryStore
{
    public async Task<IReadOnlyList<WorkflowHistoryEntry>> GetAsync(string workflowName, string entityId, CancellationToken ct = default) =>
        await db.Set<WorkflowHistoryEntry>()
            .AsNoTracking()
            .Where(e => e.WorkflowName == workflowName && e.EntityId == entityId)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);

    public void Add(WorkflowHistoryEntry entry) => db.Set<WorkflowHistoryEntry>().Add(entry);
}

/// <summary>In OnModelCreating: <c>modelBuilder.ApplyConfiguration(new WorkflowHistoryEntryConfiguration());</c></summary>
public sealed class WorkflowHistoryEntryConfiguration(string table = "WorkflowHistory", string? schema = null)
    : IEntityTypeConfiguration<WorkflowHistoryEntry>
{
    public void Configure(EntityTypeBuilder<WorkflowHistoryEntry> b)
    {
        b.ToTable(table, schema);
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedOnAdd();
        b.Property(e => e.WorkflowName).HasMaxLength(100).IsRequired();
        b.Property(e => e.EntityId).HasMaxLength(64).IsRequired();
        b.Property(e => e.Action).HasMaxLength(100).IsRequired();
        b.Property(e => e.FromStatus).HasMaxLength(100).IsRequired();
        b.Property(e => e.ToStatus).HasMaxLength(100).IsRequired();
        b.Property(e => e.ActorKey).HasMaxLength(100).IsRequired();
        b.Property(e => e.UserId).HasMaxLength(100).IsRequired();
        b.Property(e => e.Comment).HasMaxLength(2000);
        b.HasIndex(e => new { e.WorkflowName, e.EntityId, e.Id });
    }
}
