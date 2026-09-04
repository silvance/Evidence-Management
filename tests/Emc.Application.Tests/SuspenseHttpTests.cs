using System.Globalization;
using System.Net;
using Emc.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Temporary release at the HTTP boundary: the release recorded through the voucher page's own
/// form, the release page with its contact and return forms, the suspense dashboard with its
/// LOCAL threshold wording, and 404 for an unregistered account on every one of them.
/// </summary>
public class SuspenseHttpTests : IClassFixture<EmcWebFactory>, IClassFixture<UnregisteredPrincipalWebFactory>
{
    private readonly EmcWebFactory _registered;
    private readonly UnregisteredPrincipalWebFactory _unregistered;

    public SuspenseHttpTests(EmcWebFactory registered, UnregisteredPrincipalWebFactory unregistered)
    {
        _registered = registered;
        _unregistered = unregistered;
    }

    private static string Local(DateTime at) => at.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    /// <summary>The pages may say what a number is NOT (SUSP-004); any other use of the word is a defect.</summary>
    private static string WithoutDisclaimers(string html)
        => html.Replace("not a deadline", "", StringComparison.OrdinalIgnoreCase)
               .Replace("not a regulatory deadline", "", StringComparison.OrdinalIgnoreCase)
               .Replace("never an AR 195-5 deadline", "", StringComparison.OrdinalIgnoreCase);

    /// <summary>GETs the page for its anti-forgery token, then POSTs the fields (repeated names allowed) and returns the redirect target.</summary>
    private async Task<string> PostAsync(HttpClient client, string url, IEnumerable<KeyValuePair<string, string>> fields, string? handler = null)
    {
        var token = await _registered.GetAntiForgeryTokenAsync(client, url);
        var form = fields.Append(new("__RequestVerificationToken", token)).ToList();
        using var content = new FormUrlEncodedContent(form);
        var postUrl = handler is null ? url : $"{url}?handler={handler}";
        using var response = await client.PostAsync(postUrl, content);
        if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found)
        {
            return response.Headers.Location?.ToString() ?? url;
        }

        var body = await response.Content.ReadAsStringAsync();
        var start = body.IndexOf("message--error", StringComparison.Ordinal);
        var excerpt = start < 0 ? body[..Math.Min(body.Length, 600)] : body[start..Math.Min(body.Length, start + 600)];
        throw new InvalidOperationException($"POST {postUrl} returned {(int)response.StatusCode}: {excerpt}");
    }

    [Fact]
    public async Task ReleaseContactDashboardAndReturnThroughThePages_ThenNothingForAnOutsider()
    {
        var client = _registered.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var example = await _registered.SeedWorkedExampleAsync(client);
        var voucherId = int.Parse(example.VoucherUrl.Split('/')[^1], CultureInfo.InvariantCulture);
        var itemId = int.Parse(example.ItemUrl.Split('/')[^1], CultureInfo.InvariantCulture);
        var roomId = _registered.EvidenceRoomId;

        // The paper files the release needs, through the paper files page (2-4f(1), 2-4f(3)).
        var containersUrl = $"/Filing/Containers/{roomId}";
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        await PostAsync(client, containersUrl, new Dictionary<string, string>
        {
            ["Input.Kind"] = "Active4137File", ["Input.Form"] = "Binder", ["Input.Label"] = $"ACTIVE 001-26 to 050-26 {suffix}",
            ["Input.RangeCalendarYear"] = "2026", ["Input.RangeFromSequence"] = "1", ["Input.RangeToSequence"] = "50"
        });
        await PostAsync(client, containersUrl, new Dictionary<string, string>
        {
            ["Input.Kind"] = "SuspenseAdjudication", ["Input.Form"] = "Folder", ["Input.Label"] = $"ADJUDICATION {suffix}"
        });

        int binderId, folderId;
        using (var scope = _registered.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EmcDbContext>();
            binderId = db.PhysicalFileContainers.Single(c => c.Label == $"ACTIVE 001-26 to 050-26 {suffix}").Id;
            folderId = db.PhysicalFileContainers.Single(c => c.Label == $"ADJUDICATION {suffix}").Id;
        }

        var when = DateTime.Now.AddHours(-8);
        await PostAsync(client, example.VoucherUrl, new Dictionary<string, string>
        {
            ["Physical.Action"] = "FileOriginalInActiveFile", ["Physical.ContainerId"] = binderId.ToString(CultureInfo.InvariantCulture),
            ["Physical.OccurredAtLocal"] = Local(when), ["Physical.CopyReason"] = "None"
        }, "RecordPhysical");

        // The release, through the voucher page's form: items, folder, recipient, purpose, the
        // five paper attestations (2-7b), and the custodian's own follow-up date.
        var releaseFields = new List<KeyValuePair<string, string>>
        {
            new("Release.ItemIds", itemId.ToString(CultureInfo.InvariantCulture)),
            new("Release.Category", "Adjudication"),
            new("Release.SuspenseFolderContainerId", folderId.ToString(CultureInfo.InvariantCulture)),
            new("Release.PaperAccompanying", "Original"),
            new("Release.RecipientKind", "ExternalPerson"),
            new("Release.RecipientName", "COUNSEL, TEST B."),
            new("Release.RecipientTitleOrGrade", "CPT"),
            new("Release.RecipientOrganization", "OSJA, Fort Test"),
            new("Release.Purpose", "Presentation at trial, US v. TEST"),
            new("Release.Destination", "Fort Test courtroom 2"),
            new("Release.ReleasedAtLocal", Local(when.AddHours(1))),
            new("Release.AmbiguousTimeChoice", "Unspecified"),
            new("Release.ExpectedFollowUpLocal", Local(when.AddDays(30))),
            new("Release.PhysicalInventoryPerformedAttested", "true"),
            new("Release.Original4137ReceivedBySignedAttested", "true"),
            new("Release.FirstCopyReceivedBySignedAttested", "true"),
            new("Release.IdentificationPresentedAttested", "true"),
            new("Release.ObligationsInformedAttested", "true"),
            new("Release.Notes", "TEST release through the page")
        };
        var afterRelease = await PostAsync(client, example.VoucherUrl, releaseFields, "Release");
        Assert.Equal(example.VoucherUrl, afterRelease);

        int releaseId;
        using (var scope = _registered.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EmcDbContext>();
            releaseId = db.TemporaryReleases.Single(r => r.VoucherId == voucherId).Id;
        }

        using (var voucherPage = await client.GetAsync(example.VoucherUrl))
        {
            var html = await voucherPage.Content.ReadAsStringAsync();
            Assert.Contains($"/Releases/Details/{releaseId}", html, StringComparison.Ordinal);
            Assert.Contains("COUNSEL, TEST B.", html, StringComparison.Ordinal);
            Assert.Contains("TemporarilyReleased", html, StringComparison.Ordinal);
        }

        var releaseUrl = $"/Releases/Details/{releaseId}";
        using (var releasePage = await client.GetAsync(releaseUrl))
        {
            Assert.Equal(HttpStatusCode.OK, releasePage.StatusCode);
            var html = await releasePage.Content.ReadAsStringAsync();
            Assert.Contains("Contact history", html, StringComparison.Ordinal);
            Assert.Contains("No contact recorded yet", html, StringComparison.Ordinal);
            Assert.Contains(example.DocumentNumber, html, StringComparison.Ordinal);
            Assert.DoesNotContain("deadline", WithoutDisclaimers(html), StringComparison.OrdinalIgnoreCase);
        }

        // A contact (2-7a) through the release page.
        await PostAsync(client, releaseUrl, new Dictionary<string, string>
        {
            ["Contact.ContactedAtLocal"] = Local(when.AddHours(2)), ["Contact.AmbiguousTimeChoice"] = "Unspecified",
            ["Contact.Method"] = "Telephone", ["Contact.ContactedPerson"] = "COUNSEL, TEST B.",
            ["Contact.Outcome"] = "EvidenceStillRequired", ["Contact.Narrative"] = "Trial set for next term (TEST).",
            ["Contact.NextFollowUpLocal"] = Local(when.AddDays(14))
        }, "Contact");
        using (var withContact = await client.GetAsync(releaseUrl))
        {
            var html = await withContact.Content.ReadAsStringAsync();
            Assert.Contains("Trial set for next term (TEST).", html, StringComparison.Ordinal);
            Assert.DoesNotContain("No contact recorded yet", html, StringComparison.Ordinal);
        }

        // The dashboard: the release under ADJUDICATION, the threshold labelled LOCAL, never a deadline.
        var dashboardUrl = $"/Suspense/Dashboard/{roomId}";
        using (var dashboard = await client.GetAsync(dashboardUrl))
        {
            Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
            var html = await dashboard.Content.ReadAsStringAsync();
            Assert.Contains("local review threshold", html, StringComparison.Ordinal);
            Assert.Contains("[LOCAL]", html, StringComparison.Ordinal);
            Assert.Contains("ADJUDICATION", html, StringComparison.Ordinal);
            Assert.Contains(releaseUrl, html, StringComparison.Ordinal);
            Assert.Contains("COUNSEL, TEST B.", html, StringComparison.Ordinal);
            Assert.DoesNotContain("deadline", WithoutDisclaimers(html), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("overdue", html, StringComparison.OrdinalIgnoreCase);
        }

        // The return (2-7b) through the release page: back to the bin it left, by explicit confirmation.
        var returnFields = new List<KeyValuePair<string, string>>
        {
            new("Return.ItemIds", itemId.ToString(CultureInfo.InvariantCulture)),
            new($"Return.Locations[{itemId}]", "0"),
            new($"Return.ConfirmPrior[{itemId}]", "true"),
            new("Return.ReturnedAtLocal", Local(when.AddHours(3))),
            new("Return.AmbiguousTimeChoice", "Unspecified"),
            new("Return.OriginalAnnotatedByCustodianAndReturnerAttested", "true"),
            new("Return.FirstCopyChainAnnotatedAttested", "true"),
            new("Return.ActiveFileContainerId", "0")
        };
        await PostAsync(client, releaseUrl, returnFields, "Return");
        using (var closed = await client.GetAsync(releaseUrl))
        {
            var html = await closed.Content.ReadAsStringAsync();
            Assert.Contains("Closed", html, StringComparison.Ordinal);
        }

        using (var history = await client.GetAsync(example.ItemUrl))
        {
            var html = await history.Content.ReadAsStringAsync();
            Assert.Contains("InEvidenceRoom", html, StringComparison.Ordinal);
            Assert.Contains("Bin 14", html, StringComparison.Ordinal);
        }

        // An unregistered account sees none of it (IAM-013 / SEC-004).
        var outsider = _unregistered.CreateClient();
        using (var deniedRelease = await outsider.GetAsync(releaseUrl))
        {
            Assert.Equal(HttpStatusCode.NotFound, deniedRelease.StatusCode);
        }

        using (var deniedDashboard = await outsider.GetAsync(dashboardUrl))
        {
            Assert.Equal(HttpStatusCode.NotFound, deniedDashboard.StatusCode);
        }
    }

    [Fact]
    public async Task TheItemHistoryPageOffersTheCustodyForm_AndAnUnknownReleaseIs404()
    {
        var client = _registered.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var example = await _registered.SeedWorkedExampleAsync(client);

        using var history = await client.GetAsync(example.ItemUrl + "?findingId=1&custodyDate=03%20MAR%2026&releasedByName=SMITH%2C%20TEST%20A.&receivedByName=JONES%2C%20TEST%20B.&purpose=TEST");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var html = await history.Content.ReadAsStringAsync();
        Assert.Contains("RecordCustody", html, StringComparison.Ordinal);
        Assert.Contains("SMITH, TEST A.", html, StringComparison.Ordinal);
        Assert.Contains("JONES, TEST B.", html, StringComparison.Ordinal);
        Assert.Contains("2026-03-03", html, StringComparison.Ordinal);

        using var missing = await client.GetAsync("/Releases/Details/999999");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
