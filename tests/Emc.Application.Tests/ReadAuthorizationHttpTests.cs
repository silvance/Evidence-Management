using System.Net;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Read authorization at the HTTP boundary — direct URL navigation, not service calls.
///
/// This is the layer that actually mattered: the pages previously queried the DbContext in their
/// GET handlers, so a valid domain account could read every case by typing a URL. Requirements:
/// IAM-017, IAM-018.
/// </summary>
public class ReadAuthorizationHttpTests
    : IClassFixture<EmcWebFactory>, IClassFixture<UnregisteredPrincipalWebFactory>
{
    private readonly EmcWebFactory _registered;
    private readonly UnregisteredPrincipalWebFactory _unregistered;

    public ReadAuthorizationHttpTests(
        EmcWebFactory registered, UnregisteredPrincipalWebFactory unregistered)
    {
        _registered = registered;
        _unregistered = unregistered;
    }

    [Fact]
    public async Task AnUnregisteredDomainAccountSeesNoCasesInTheListing()
    {
        var client = _unregistered.CreateClient();

        using var response = await client.GetAsync("/Cases");

        // The page itself renders — the account authenticated — but it contains no evidence data
        // and says why.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("CID902", html, StringComparison.Ordinal);
        Assert.Contains("not registered in this application", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectNavigationToEvidenceUrlsIsDeniedForAnUnregisteredAccount()
    {
        // IAM-018. Seed real records through the registered client first, then attempt to read
        // them by URL as the unregistered account.
        var seedClient = _registered.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

        var example = await _registered.SeedWorkedExampleAsync(seedClient);

        var outsider = _unregistered.CreateClient();

        foreach (var url in new[] { example.CaseUrl, example.VoucherUrl, example.ItemUrl })
        {
            using var response = await outsider.GetAsync(url);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task ForbiddenAndNonExistentRecordsAreIndistinguishable()
    {
        // A 403 on a real record and a 404 on a missing one would turn the identifier space into
        // an oracle for which cases exist. Both must be 404.
        var outsider = _unregistered.CreateClient();

        using var forbidden = await outsider.GetAsync("/Cases/Details/1");
        using var nonExistent = await outsider.GetAsync("/Cases/Details/999999");

        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        Assert.Equal(nonExistent.StatusCode, forbidden.StatusCode);
    }

    [Fact]
    public async Task TheItemHistoryUrlLeaksNoEvidenceContentToAnUnregisteredAccount()
    {
        var seedClient = _registered.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

        var example = await _registered.SeedWorkedExampleAsync(seedClient);

        var outsider = _unregistered.CreateClient();
        using var response = await outsider.GetAsync(example.ItemUrl);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        // None of the sensitive fields may appear, even in an error page.
        foreach (var secret in new[]
                 { "R58N30XXXXX", "356938035643809", "Samsung", example.DocumentNumber })
        {
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        }
    }
}
