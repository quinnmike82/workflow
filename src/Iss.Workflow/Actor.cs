namespace Iss.Workflow;

/// <summary>
/// "Who" may do something: role + department, or a custom rule.
/// Declare them centrally in one Actors.cs file shared by all modules.
/// </summary>
public sealed class Actor
{
    private readonly Func<UserContext, bool> _matches;

    private Actor(string key, string description, Func<UserContext, bool> matches)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = key;
        Description = description;
        _matches = matches;
    }

    /// <summary>Stable key written to history (used to compute parallel approval). Do not change it while applications are pending.</summary>
    public string Key { get; }

    public string Description { get; }

    public bool Matches(UserContext user) => _matches(user);

    /// <summary>User holds one of these roles, inside this exact department.</summary>
    public static Actor In<TDept, TRole>(string key, TDept department, params TRole[] roles)
        where TDept : struct, Enum
        where TRole : struct, Enum
        => In(key, department.ToString(), roles.Select(r => r.ToString()).ToArray());

    /// <inheritdoc cref="In{TDept, TRole}(string, TDept, TRole[])"/>
    public static Actor In(string key, string department, params string[] roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(department);
        if (roles.Length == 0) throw new ArgumentException("At least one role is required.", nameof(roles));
        var allowed = roles.ToArray();
        return new Actor(key, $"{department}: {string.Join(" | ", allowed)}",
            user => allowed.Any(role => user.HasRole(department, role)));
    }

    /// <summary>User holds one of these roles in any department (e.g. an external Applicant).</summary>
    public static Actor WithRole<TRole>(string key, params TRole[] roles) where TRole : struct, Enum
    {
        if (roles.Length == 0) throw new ArgumentException("At least one role is required.", nameof(roles));
        var allowed = roles.Select(r => r.ToString()).ToArray();
        return new Actor(key, $"any dept: {string.Join(" | ", allowed)}",
            user => allowed.Any(user.HasRoleInAnyDepartment));
    }

    /// <summary>Custom rule when role/department is not expressive enough.</summary>
    public static Actor Custom(string key, string description, Func<UserContext, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        return new Actor(key, description, matches);
    }

    public override string ToString() => Key;
}
