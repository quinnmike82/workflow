namespace Iss.Workflow.Samples;

public enum Dept
{
    Registry,
    Marine,
    Finance,
    Licensing,
}

public enum Role
{
    Applicant,
    Checker,
    Officer,
    SeniorOfficer,
    FinanceOfficer,
    Manager,
    Director,
}

/// <summary>
/// Role + Department declared in ONE place, shared by every module.
/// Customer changes "only Marine Senior Officers may review" → edit exactly one line here.
/// </summary>
public static class Actors
{
    public static readonly Actor Applicant = Actor.WithRole(nameof(Applicant), Role.Applicant);

    public static readonly Actor RegistryChecker = Actor.In(nameof(RegistryChecker), Dept.Registry, Role.Checker);
    public static readonly Actor MarineReviewer = Actor.In(nameof(MarineReviewer), Dept.Marine, Role.Officer, Role.SeniorOfficer);
    public static readonly Actor FinanceReviewer = Actor.In(nameof(FinanceReviewer), Dept.Finance, Role.FinanceOfficer);
    public static readonly Actor MarineDirector = Actor.In(nameof(MarineDirector), Dept.Marine, Role.Director);

    // Licensing's 4 approval levels
    public static readonly Actor LicensingOfficer = Actor.In(nameof(LicensingOfficer), Dept.Licensing, Role.Officer);
    public static readonly Actor LicensingSenior = Actor.In(nameof(LicensingSenior), Dept.Licensing, Role.SeniorOfficer);
    public static readonly Actor LicensingManager = Actor.In(nameof(LicensingManager), Dept.Licensing, Role.Manager);
    public static readonly Actor LicensingDirector = Actor.In(nameof(LicensingDirector), Dept.Licensing, Role.Director);
}
