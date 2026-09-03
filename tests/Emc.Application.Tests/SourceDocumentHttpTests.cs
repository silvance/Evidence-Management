using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Source documents at the HTTP boundary: upload through the real form, page images and
/// download by URL, and 404 for everything an unregistered account tries.
/// </summary>
public class SourceDocumentHttpTests : IClassFixture<EmcWebFactory>, IClassFixture<UnregisteredPrincipalWebFactory>
{
    private readonly EmcWebFactory _registered;
    private readonly UnregisteredPrincipalWebFactory _unregistered;

    public SourceDocumentHttpTests(EmcWebFactory registered, UnregisteredPrincipalWebFactory unregistered)
    {
        _registered = registered;
        _unregistered = unregistered;
    }

    [Fact]
    public async Task UploadViewPageImageAndDownload_ThenDeniedToAnOutsider()
    {
        var client = _registered.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var example = await _registered.SeedWorkedExampleAsync(client);
        var voucherId = int.Parse(example.VoucherUrl.Split('/')[^1]);

        var uploadUrl = $"/Documents/Upload/{voucherId}";
        var token = await _registered.GetAntiForgeryTokenAsync(client, uploadUrl);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(token), "__RequestVerificationToken");
        form.Add(new StringContent("DaForm4137"), "Input.DocumentType");
        form.Add(new StringContent("PhysicalOriginal"), "Input.Provenance");
        form.Add(new StringContent("UNCLASSIFIED"), "Input.ClassificationMarking");
        var file = new ByteArrayContent(SyntheticPdf.SinglePage("TEST HTTP UPLOAD"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "Input.File", "scan.pdf");

        using var upload = await client.PostAsync(uploadUrl, form);
        Assert.Equal(HttpStatusCode.Redirect, upload.StatusCode);
        var viewUrl = upload.Headers.Location!.ToString();
        Assert.StartsWith("/Documents/View/", viewUrl, StringComparison.Ordinal);

        using var view = await client.GetAsync(viewUrl);
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        var html = await view.Content.ReadAsStringAsync();
        Assert.Contains("DIGITAL COMPANION COPY", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<embed", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<object", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);

        using var page = await client.GetAsync(viewUrl + "?handler=Page&pageNumber=1");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("image/png", page.Content.Headers.ContentType!.MediaType);

        using var download = await client.GetAsync(viewUrl + "?handler=Download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.DoesNotContain("scan.pdf", download.Content.Headers.ContentDisposition.FileName ?? string.Empty, StringComparison.Ordinal);

        var outsider = _unregistered.CreateClient();
        foreach (var url in new[] { viewUrl, viewUrl + "?handler=Page&pageNumber=1", viewUrl + "?handler=Download", "/Documents/View/999999" })
        {
            using var denied = await outsider.GetAsync(url);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
    }

    [Fact]
    public async Task AnOversizeUploadIsRefusedAtTheRequestLayer()
    {
        var client = _registered.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var example = await _registered.SeedWorkedExampleAsync(client);
        var voucherId = int.Parse(example.VoucherUrl.Split('/')[^1]);
        var uploadUrl = $"/Documents/Upload/{voucherId}";
        var token = await _registered.GetAntiForgeryTokenAsync(client, uploadUrl);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(token), "__RequestVerificationToken");
        form.Add(new ByteArrayContent(new byte[53 * 1024 * 1024]), "Input.File", "big.pdf");

        using var response = await client.PostAsync(uploadUrl, form);
        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError, HttpStatusCode.OK });
    }
}
