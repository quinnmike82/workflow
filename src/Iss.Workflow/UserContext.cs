namespace Iss.Workflow;

/// <summary>One role within one department. A role is only valid inside the department of its own membership.</summary>
public sealed record Membership(string Department, string Role);

/// <summary>The acting user. Build it from the current claims/identity on every request.</summary>
public sealed record UserContext(string UserId, IReadOnlyList<Membership> Memberships)
{
    public UserContext(string userId, params Membership[] memberships)
        : this(userId, (IReadOnlyList<Membership>)memberships)
    {
    }

    /// <summary>True when the user holds <paramref name="role"/> INSIDE <paramref name="department"/>.
    /// Director in department A + Officer in department B does not make a Director in department B.</summary>
    public bool HasRole(string department, string role) =>
        Memberships.Any(m => Same(m.Department, department) && Same(m.Role, role));

    public bool HasRoleInAnyDepartment(string role) => Memberships.Any(m => Same(m.Role, role));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
