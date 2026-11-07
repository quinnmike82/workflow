using System.Reflection;

namespace Iss.Workflow;

/// <summary>
/// Catches configuration errors before deploy: call it in a unit test (<see cref="FindAll"/>) and/or at startup (<see cref="EnsureValid"/>).
/// </summary>
public static class WorkflowValidator
{
    public static IReadOnlyList<string> Validate<TEntity, TStatus>(WorkflowDefinition<TEntity, TStatus> workflow)
        where TEntity : IWorkflowEntity<TStatus>
        where TStatus : struct, Enum
    {
        var errors = new List<string>();
        var rows = workflow.Transitions;
        var cmp = EqualityComparer<TStatus>.Default;
        string Row(Transition<TEntity, TStatus> t) => $"[{t.Order}] {t.From} --{t.Action}/{t.Approval}--> {t.To}";

        if (rows.Count == 0) errors.Add("Workflow has no transitions.");
        if (workflow.Terminal.Count == 0) errors.Add("Workflow has no terminal status.");
        if (workflow.Terminal.Contains(workflow.Initial)) errors.Add($"Initial status {workflow.Initial} cannot be terminal.");

        foreach (var t in rows.Where(t => workflow.Terminal.Contains(t.From)))
            errors.Add($"{Row(t)}: transition out of terminal status {t.From}.");

        // Two rows with the same (From, Action, Rank), the same actor and no guard → the later row is never used.
        for (var i = 0; i < rows.Count; i++)
        for (var j = i + 1; j < rows.Count; j++)
        {
            var a = rows[i];
            var b = rows[j];
            if (!cmp.Equals(a.From, b.From) || a.Action != b.Action || a.Rank != b.Rank) continue;
            if (a.Guard is not null || b.Guard is not null) continue;

            var overlap = a.Approval.Actors.Select(x => x.Key).Intersect(b.Approval.Actors.Select(x => x.Key)).ToList();
            if (overlap.Count > 0)
                errors.Add($"{Row(b)} is never used for {string.Join(", ", overlap)}: {Row(a)} has the same priority and no guard.");
        }

        // Reachability
        var reachable = new HashSet<TStatus> { workflow.Initial };
        var queue = new Queue<TStatus>([workflow.Initial]);
        while (queue.Count > 0)
        {
            var status = queue.Dequeue();
            foreach (var t in workflow.TransitionsFrom(status))
                if (reachable.Add(t.To)) queue.Enqueue(t.To);
        }

        foreach (var status in reachable.Where(s => !workflow.Terminal.Contains(s)))
            if (!workflow.TransitionsFrom(status).Any())
                errors.Add($"Dead end: status {status} is reachable but has no outgoing transition and is not terminal.");

        foreach (var status in workflow.Terminal.Where(s => !reachable.Contains(s)))
            errors.Add($"Terminal status {status} is never reached from {workflow.Initial}.");

        foreach (var t in rows.Where(t => !reachable.Contains(t.From)))
            errors.Add($"{Row(t)}: status {t.From} is never reached from {workflow.Initial}.");

        return errors;
    }

    /// <summary>Call at startup. Throws listing every problem across all workflows.</summary>
    public static void EnsureValid(IEnumerable<IWorkflowDefinition> workflows)
    {
        var problems = workflows
            .SelectMany(w => w.Validate().Select(e => $"{w.Name}: {e}"))
            .ToList();

        if (problems.Count > 0)
            throw new InvalidOperationException("Invalid workflow definition(s):" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>Finds every static WorkflowDefinition field in an assembly. Used by the "validate all" unit test.</summary>
    public static IReadOnlyList<IWorkflowDefinition> FindAll(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => !t.ContainsGenericParameters)
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(f => typeof(IWorkflowDefinition).IsAssignableFrom(f.FieldType))
            .Select(f => (IWorkflowDefinition?)f.GetValue(null))
            .OfType<IWorkflowDefinition>()
            .ToList();
}
