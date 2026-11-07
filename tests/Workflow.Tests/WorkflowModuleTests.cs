using Workflow.Samples;
using Workflow.Samples.Rts;
using Xunit;

namespace Workflow.Tests;

public class WorkflowModuleTests
{
    private readonly List<string> _log = [];
    private readonly InMemoryWorkflowHistoryStore _history = new();
    private readonly RtsApplication _app = new() { ApplicantId = "applicant-1" };
    private readonly FakeShips _ships = new();
    private readonly RtsService _service;

    private readonly UserContext _applicant = Users.Applicant();
    private readonly UserContext _registry = Users.In("registry-1", Dept.Registry, Role.Checker);
    private readonly UserContext _marine = Users.In("marine-1", Dept.Marine, Role.Officer);
    private readonly UserContext _finance = Users.In("finance-1", Dept.Finance, Role.FinanceOfficer);
    private readonly UserContext _director = Users.In("director-1", Dept.Marine, Role.Director);

    public WorkflowModuleTests()
    {
        _ships.Log = _log;
        _service = new RtsService(new WorkflowEngine(_history), new FakeStore(_app, _log), _ships, new FakeEmail(_log));
    }

    private Task<WorkflowResult<RtsStatus>> Act(string action, UserContext user) =>
        _service.ActAsync(_app.Id.ToString(), action, user);

    [Fact]
    public async Task Hooks_run_in_order_effects_before_save_email_after()
    {
        await Act("SUBMIT", _applicant);
        await Act("APPROVE", _registry);
        await Act("APPROVE", _marine);
        await Act("APPROVE", _finance);
        _log.Clear();

        await Act("APPROVE", _director);

        Assert.Equal("certificate,save,email:rts-approved", string.Join(",", _log));
        Assert.Equal(RtsStatus.Approved, _app.Status);
    }

    [Fact]
    public async Task Business_rule_failure_changes_nothing()
    {
        _ships.Eligible = false;

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Act("SUBMIT", _applicant));

        Assert.Equal("SHIP_NOT_ELIGIBLE", ex.Code);
        Assert.Equal(RtsStatus.Draft, _app.Status);
        Assert.Empty(_history.Entries);
        Assert.DoesNotContain("save", _log);
    }

    [Fact]
    public async Task Unauthorized_user_never_reaches_business_validation()
    {
        var ex = await Assert.ThrowsAsync<WorkflowException>(() => Act("SUBMIT", _marine));

        Assert.Equal(WorkflowError.NotAuthorized, ex.Error);
        Assert.DoesNotContain("ship-check", _log);
    }

    [Fact]
    public async Task Request_info_updates_data_and_emails_applicant()
    {
        await Act("SUBMIT", _applicant);
        await Act("APPROVE", _registry);
        _log.Clear();

        await Act("REQUEST_INFO", _marine);

        Assert.Equal(1, _app.RfiCount);
        Assert.Equal("save,email:rts-request-info", string.Join(",", _log));
    }

    private sealed class FakeStore(RtsApplication app, List<string> log) : IRtsStore
    {
        public Task<RtsApplication> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(app);

        public void IssueCertificate(Guid applicationId) => log.Add("certificate");

        public Task SaveChangesAsync(CancellationToken ct)
        {
            log.Add("save");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeShips : IShipEligibility
    {
        public bool Eligible { get; set; } = true;

        public List<string> Log { get; set; } = [];

        public Task<bool> IsEligibleAsync(Guid applicationId, CancellationToken ct)
        {
            Log.Add("ship-check");
            return Task.FromResult(Eligible);
        }
    }

    private sealed class FakeEmail(List<string> log) : IEmailSender
    {
        public Task SendAsync(string to, string template, object model, CancellationToken ct)
        {
            log.Add($"email:{template}");
            return Task.CompletedTask;
        }
    }
}
