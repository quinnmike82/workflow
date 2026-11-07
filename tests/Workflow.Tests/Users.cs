using Workflow.Samples;

namespace Workflow.Tests;

internal static class Users
{
    public static UserContext Applicant(string id = "applicant-1") =>
        new(id, new Membership("External", nameof(Role.Applicant)));

    public static UserContext In(string id, Dept dept, Role role) =>
        new(id, new Membership(dept.ToString(), role.ToString()));

    public static UserContext With(string id, params (Dept Dept, Role Role)[] memberships) =>
        new(id, memberships.Select(m => new Membership(m.Dept.ToString(), m.Role.ToString())).ToArray());
}
