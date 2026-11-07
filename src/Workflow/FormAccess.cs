using System.Collections;
using System.Runtime.CompilerServices;

namespace Workflow;

/// <summary>Editable scope: the whole form or some sections (keys match the section keys in the Form Builder schema).</summary>
public sealed class EditScope
{
    private EditScope(bool all, IEnumerable<string> sections)
    {
        IsAll = all;
        Sections = sections.ToHashSet(StringComparer.Ordinal);
    }

    public bool IsAll { get; }

    public IReadOnlySet<string> Sections { get; }

    internal static readonly EditScope AllScope = new(true, []);

    internal static EditScope Of(string[] sections)
    {
        if (sections.Length == 0) throw new ArgumentException("At least one section is required. Use Edit.All for the whole form.", nameof(sections));
        if (sections.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Section key cannot be empty.", nameof(sections));
        return new EditScope(false, sections);
    }

    public override string ToString() => IsAll ? "*" : string.Join(", ", Sections);
}

public static class Edit
{
    public static EditScope All => EditScope.AllScope;

    public static EditScope Sections(params string[] sections) => EditScope.Of(sections);
}

public sealed record FormAccessRule<TEntity, TStatus>(
    TStatus Status,
    IReadOnlyList<Actor> Actors,
    EditScope Scope,
    Func<TEntity, UserContext, bool>? Guard = null,
    string? GuardName = null)
    where TEntity : IWorkflowEntity<TStatus>
    where TStatus : struct, Enum;

/// <summary>Result for one user on one application. Returned to the FE to enable/lock fields.</summary>
public sealed record EditPermission(bool All, IReadOnlySet<string> Sections)
{
    public static readonly EditPermission None = new(false, new HashSet<string>());

    public bool CanEditAnything => All || Sections.Count > 0;

    public bool CanEdit(string section) => All || Sections.Contains(section);
}

public sealed class FormAccessException(IReadOnlyList<string> deniedSections, string message) : Exception(message)
{
    public IReadOnlyList<string> DeniedSections { get; } = deniedSections;
}

/// <summary>
/// Who may EDIT the form, at which status, and which parts. Kept separate from the workflow because editing does not change status.
/// No matching row → read-only. A user matching several rows gets the union of their sections.
/// <code>
/// public static readonly FormAccess&lt;RtsApplication, RtsStatus&gt; Access = new()
/// {
///     { Draft,             Actors.Applicant,                                Edit.All },
///     { PendingDualReview, [Actors.MarineReviewer, Actors.FinanceReviewer], Edit.Sections("remarks") },
///     { PendingDualReview, Actors.FinanceReviewer,                          Edit.Sections("fees") },
/// };
/// </code>
/// </summary>
public sealed class FormAccess<TEntity, TStatus> : IEnumerable<FormAccessRule<TEntity, TStatus>>
    where TEntity : IWorkflowEntity<TStatus>
    where TStatus : struct, Enum
{
    private readonly List<FormAccessRule<TEntity, TStatus>> _rules = [];

    public FormAccess()
    {
    }

    /// <summary>Variant of an existing table: copy it, drop rows matching <paramref name="except"/>, then add new rows.</summary>
    public FormAccess(FormAccess<TEntity, TStatus> source, Func<FormAccessRule<TEntity, TStatus>, bool>? except = null)
    {
        foreach (var rule in source._rules)
            if (except is null || !except(rule))
                Add(rule);
    }

    public IReadOnlyList<FormAccessRule<TEntity, TStatus>> Rules => _rules;

    public void Add(TStatus status, Actor actor, EditScope scope) => Add(status, [actor], scope);

    public void Add(TStatus status, Actor[] actors, EditScope scope) =>
        Add(new FormAccessRule<TEntity, TStatus>(status, actors, scope));

    public void Add(TStatus status, Actor actor, EditScope scope,
        Func<TEntity, UserContext, bool> when, [CallerArgumentExpression(nameof(when))] string? whenName = null)
        => Add(status, [actor], scope, when, whenName);

    public void Add(TStatus status, Actor[] actors, EditScope scope,
        Func<TEntity, UserContext, bool> when, [CallerArgumentExpression(nameof(when))] string? whenName = null)
    {
        ArgumentNullException.ThrowIfNull(when);
        Add(new FormAccessRule<TEntity, TStatus>(status, actors, scope, when, whenName));
    }

    public void Add(FormAccessRule<TEntity, TStatus> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(rule.Scope);
        if (rule.Actors.Count == 0 || rule.Actors.Any(a => a is null))
            throw new ArgumentException("Rule needs at least one non-null actor. Check static field declaration order.", nameof(rule));
        _rules.Add(rule with { Actors = rule.Actors.ToArray() });
    }

    /// <summary>Edit permission of <paramref name="user"/> on <paramref name="entity"/> at its current status.</summary>
    public EditPermission GetEditPermission(TEntity entity, UserContext user)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(user);

        var all = false;
        var sections = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in _rules)
        {
            if (!EqualityComparer<TStatus>.Default.Equals(rule.Status, entity.Status)) continue;
            if (!rule.Actors.Any(a => a.Matches(user))) continue;
            if (rule.Guard is not null && !rule.Guard(entity, user)) continue;

            if (rule.Scope.IsAll) all = true;
            else sections.UnionWith(rule.Scope.Sections);
        }

        return all || sections.Count > 0 ? new EditPermission(all, sections) : EditPermission.None;
    }

    /// <summary>
    /// Call on the BE when saving the form. <paramref name="changedSections"/> are the sections whose data changed
    /// (compare the submitted DTO with current data). Never rely on the FE having locked fields.
    /// </summary>
    public void EnsureCanEdit(TEntity entity, UserContext user, IEnumerable<string> changedSections)
    {
        var permission = GetEditPermission(entity, user);
        var denied = changedSections.Where(s => !permission.CanEdit(s)).Distinct().ToList();
        if (denied.Count > 0)
            throw new FormAccessException(denied,
                $"User '{user.UserId}' cannot edit [{string.Join(", ", denied)}] when status is '{entity.Status}'.");
    }

    /// <summary>Checks that section keys exist in the schema (pass the form's section keys).</summary>
    public IReadOnlyList<string> Validate(IEnumerable<string> knownSections)
    {
        var known = knownSections.ToHashSet(StringComparer.Ordinal);
        return _rules
            .Where(r => !r.Scope.IsAll)
            .SelectMany(r => r.Scope.Sections.Where(s => !known.Contains(s)).Select(s => $"Status {r.Status}: unknown section '{s}'."))
            .Distinct()
            .ToList();
    }

    public IEnumerator<FormAccessRule<TEntity, TStatus>> GetEnumerator() => _rules.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
