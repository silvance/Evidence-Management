using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Emc.Application.Abstractions;
using Emc.Domain.Common;
using Emc.Domain.Configuration;
using Emc.Domain.Identity;
using Emc.Domain.Storage;
using Emc.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Renders the Razor pages through a real host.
///
/// Building the project only compile-checks the views. Runtime rendering faults - a null
/// navigation property, a mis-bound SelectList, a missing partial - would otherwise reach
/// production, so the pages are actually requested here.
///
/// Windows Authentication is replaced with a test handler and SQL Server with SQLite; nothing
/// else about the application is substituted, so the real authorization pipeline, the real page
/// models and the real queries run.
/// </summary>
public class WebHostSmokeTests : IClassFixture<EmcWebFactory>
{
    private readonly EmcWebFactory _factory;

    public WebHostSmokeTests(EmcWebFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/")]
    [InlineData("/Cases")]
    public async Task PagesRenderForAnAuthenticatedUser(string url)
    {
        var client = _factory.CreateClient();
        using var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        // EMC-003 - the authoritative-record notice appears on every accountability view.
        Assert.Contains("COMPANION SYSTEM", html, StringComparison.Ordinal);
        Assert.Contains("2-5a", html, StringComparison.Ordinal);

        // SEC-003 - the classification banner is rendered from configuration.
        Assert.Contains("UNCLASSIFIED", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCaseAndVoucherAndItemPagesAllRender()
    {
        // Redirects are not followed, so a POST's Location header can be read to find the record
        // the handler created.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var example = await _factory.SeedWorkedExampleAsync(client);

        foreach (var url in new[] { example.CaseUrl, example.VoucherUrl, example.ItemUrl })
        {
            using var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var itemResponse = await client.GetAsync(example.ItemUrl);
        var html = await itemResponse.Content.ReadAsStringAsync();

        // Rendered text wraps across source lines, so compare on normalized whitespace rather
        // than on the exact line breaks in the .cshtml.
        var text = Regex.Replace(html, @"\s+", " ");

        // AUD-008 - the chain verification result is surfaced on the page.
        Assert.Contains("Integrity check passed", text, StringComparison.Ordinal);

        // AR 195-5 2-4c - the recorded official document number is displayed.
        Assert.Contains("037-26", text, StringComparison.Ordinal);

        // AR 195-5 2-5b(5) - the correction rationale is stated on the page itself, so a reader
        // can see why a superseded entry is still shown.
        Assert.Contains("so it may still be read", text, StringComparison.Ordinal);

        // LOC-002 - the location recorded through the page appears in the history.
        Assert.Contains("Shelf B / Bin 14", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownItemReturnsNotFound()
    {
        var client = _factory.CreateClient();
        using var response = await client.GetAsync("/Items/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>Hosts the real application with a test identity and a SQLite database.</summary>
public sealed class EmcWebFactory : WebApplicationFactory<Program>
{
    private SqliteConnection? _connection;

    public int EvidenceRoomId { get; private set; }
    public int CustodianUserId { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Swap SQL Server for SQLite. Nothing else about the application is replaced.
            services.RemoveAll<DbContextOptions<EmcDbContext>>();
            services.RemoveAll<EmcDbContext>();

            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            services.AddDbContext<EmcDbContext>(options => options.UseSqlite(_connection));
            services.AddScoped<IEmcDbContext>(sp => sp.GetRequiredService<EmcDbContext>());

            // Windows Authentication cannot run here, so a test handler supplies the identity
            // the real HttpCurrentUser then resolves against the database.
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName, _ => { });

            // The application registers Negotiate, which needs a Kestrel connection feature the
            // test server cannot provide - and which the authentication middleware would invoke
            // as a request handler even if it were not the default scheme. Registered last, so
            // this wins over the application's own configuration.
            services.Configure<AuthenticationOptions>(options =>
            {
                options.SchemeMap.Remove(NegotiateDefaults.AuthenticationScheme, out _);

                // AuthenticationOptions.Schemes is exposed as IEnumerable but is backed by a
                // List, and the authentication middleware iterates it to find request handlers.
                // Removing from SchemeMap alone is not enough. Contained in the test host so
                // production authentication is never weakened to suit a test.
                if (options.Schemes is List<AuthenticationSchemeBuilder> schemes)
                {
                    schemes.RemoveAll(s => s.Name == NegotiateDefaults.AuthenticationScheme);
                }

                options.DefaultScheme = TestAuthenticationHandler.SchemeName;
                options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
            });

            using var scope = services.BuildServiceProvider().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<EmcDbContext>();
            db.Database.EnsureCreated();
            Seed(db);
        });
    }

    private void Seed(EmcDbContext db)
    {
        var room = new EvidenceRoom("902d MI Group Evidence Room", "902d MI Group", "America/New_York");
        db.EvidenceRooms.Add(room);
        db.SystemConfigurations.Add(new SystemConfiguration("902d MI Group", "UNCLASSIFIED"));

        foreach (var name in EmcRoles.All)
        {
            db.Roles.Add(new Role(name, $"{name} role"));
        }

        var custodian = new User(TestAuthenticationHandler.Sid, "baker.alice@army.mil", "BAKER, ALICE C.");
        custodian.UpdateProfile("BAKER, ALICE C.", "SA", "902d MI Group");
        db.Users.Add(custodian);
        db.SaveChanges();

        EvidenceRoomId = room.Id;
        CustodianUserId = custodian.Id;

        // The test identity holds both roles so a single client can exercise the agent and
        // custodian pages. Role separation itself is asserted in AuthorizationTests.
        foreach (var roleName in new[] { EmcRoles.Agent, EmcRoles.PrimaryEvidenceCustodian })
        {
            var role = db.Roles.Single(r => r.Name == roleName);
            db.UserRoles.Add(new UserRole(custodian.Id, role.Id, custodian.Id, DateTimeOffset.UtcNow));
        }

        // AR 195-5 1-4g(1) - custodial authority requires a written appointment.
        db.CustodianAppointments.Add(new CustodianAppointment(
            room.Id, custodian.Id, CustodianAppointmentType.Primary,
            DateTimeOffset.UtcNow.AddDays(-10), "ORDERS 2026-114", "Commander, 902d MI Group",
            true, custodian.Id, DateTimeOffset.UtcNow.AddDays(-10)));

        var shelf = new StorageLocation(room.Id, "Shelf B", StorageLocationKind.Shelf);
        db.StorageLocations.Add(shelf);
        db.SaveChanges();

        db.StorageLocations.Add(new StorageLocation(room.Id, "Bin 14", StorageLocationKind.Bin, shelf));
        db.SaveChanges();
    }

    /// <summary>
    /// Builds a worked example by POSTing to the real pages, so the POST handlers, model binding
    /// and anti-forgery validation are exercised rather than bypassed.
    /// </summary>
    public async Task<WorkedExample> SeedWorkedExampleAsync(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        var caseLocation = await PostAsync(client, "/Cases", new Dictionary<string, string>
        {
            ["Input.CaseControlNumber"] = "0142-2026-CID902-XXXXX",
            ["Input.Title"] = "Worked example",
            ["Input.EvidenceRoomId"] = EvidenceRoomId.ToString(CultureInfo.InvariantCulture)
        });

        var voucherLocation = await PostAsync(client, caseLocation, new Dictionary<string, string>
        {
            ["Input.ReceivingActivity"] = "902d MI Group Evidence Room",
            ["Input.ReceivingActivityLocation"] = "Fort Meade, MD",
            ["Input.ReceivedFrom"] = "SUBJECT residence, 123 Elm Street",
            ["Input.AcquiredAtLocal"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
            ["Input.IsRequestForAssistance"] = "false"
        });

        var voucherId = IdFromLocation(voucherLocation);

        await PostAsync(client, voucherLocation, new Dictionary<string, string>
        {
            ["NewItem.Description"] = "One Samsung SM-S921U cellular telephone, black",
            ["NewItem.Quantity"] = "1",
            ["NewItem.SerialNumber"] = "R58N30XXXXX",
            ["NewItem.UniqueDeviceIdentifier"] = "356938035643809"
        }, handler: "AddItem");

        await PostAsync(client, voucherLocation, new Dictionary<string, string>(), handler: "Submit");

        await PostAsync(client, voucherLocation, new Dictionary<string, string>
        {
            ["DocumentNumber.Value"] = "037-26",
            ["DocumentNumber.ReceivedAtLocal"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),

            // EMC-002 / VCH-006 - the custodian's explicit attestation that the number was
            // assigned in the authoritative evidence ledger (AR 195-5 para 2-4c).
            ["DocumentNumber.AttestedAssignedInAuthoritativeLedger"] = "true"
        }, handler: "RecordDocumentNumber");

        int itemId;
        int locationId;

        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EmcDbContext>();
            itemId = db.EvidenceItems.Single(i => i.VoucherId == voucherId).Id;
            locationId = db.StorageLocations.Single(l => l.Name == "Bin 14").Id;
        }

        var itemUrl = $"/Items/History/{itemId.ToString(CultureInfo.InvariantCulture)}";

        await PostAsync(client, itemUrl, new Dictionary<string, string>
        {
            ["Location.StorageLocationId"] = locationId.ToString(CultureInfo.InvariantCulture),
            ["Location.OccurredAtLocal"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
            ["Location.Reason"] = "Initial placement following intake"
        }, handler: "AssignLocation");

        return new WorkedExample(caseLocation, voucherLocation, itemUrl);
    }

    /// <summary>The URLs the worked example produced, taken from the handlers' own redirects.</summary>
    public sealed record WorkedExample(string CaseUrl, string VoucherUrl, string ItemUrl);

    /// <summary>
    /// GETs the page to obtain its anti-forgery token, then POSTs. Anti-forgery validation is
    /// applied globally in Program.cs, so a POST without a valid token is rejected - which means
    /// this helper also proves the tokens are being issued and accepted.
    /// </summary>
    private static async Task<string> PostAsync(
        HttpClient client,
        string url,
        IDictionary<string, string> fields,
        string? handler = null)
    {
        using var getResponse = await client.GetAsync(url);
        getResponse.EnsureSuccessStatusCode();

        var html = await getResponse.Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(html);

        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
        var postUrl = handler is null ? url : $"{url}?handler={handler}";

        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync(postUrl, content);

        if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found)
        {
            return response.Headers.Location?.ToString() ?? url;
        }

        // A 200 means the page re-rendered instead of redirecting, which means the handler
        // rejected the input. Surface the message the page displayed rather than a bare status
        // code, so a failing test says what the application actually objected to.
        var body = await response.Content.ReadAsStringAsync();
        var message = Regex.Match(body, @"<div class=""message message--error""[^>]*>(?<body>.*?)</div>",
            RegexOptions.Singleline);

        var fieldErrors = Regex.Matches(body, @"<span class=""field-validation-error"">(?<e>.+?)</span>")
            .Select(m => m.Groups["e"].Value.Trim())
            .Where(e => e.Length > 0)
            .ToList();

        var detail = message.Success
            ? Regex.Replace(message.Groups["body"].Value, "<[^>]+>", " ").Trim()
            : fieldErrors.Count > 0
                ? string.Join(" | ", fieldErrors)
                : $"status {(int)response.StatusCode}, no error rendered";

        var dump = Path.Combine(Path.GetTempPath(), "emc-post-failure.html");
        await File.WriteAllTextAsync(dump, body);

        throw new InvalidOperationException(
            $"POST {postUrl} did not redirect: {detail}. Response body written to {dump}.");
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            """<input name="__RequestVerificationToken" type="hidden" value="(?<token>[^"]+)" />""");

        return match.Success
            ? match.Groups["token"].Value
            : throw new InvalidOperationException("No anti-forgery token was rendered on the page.");
    }

    private static int IdFromLocation(string location)
    {
        var match = Regex.Match(location, @"(?<id>\d+)$");

        return match.Success
            ? int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"No identifier in redirect location '{location}'.");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _connection?.Dispose();
        }
    }
}

/// <summary>
/// Supplies the Windows identity that Negotiate would supply in production. The application's
/// own HttpCurrentUser then resolves roles from the database, exactly as it does live.
/// </summary>
public sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string Sid = "S-1-5-21-TEST-CUSTODIAN";

    public TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "BAKER, ALICE C."),
                new Claim(ClaimTypes.PrimarySid, Sid)
            ],
            SchemeName);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
