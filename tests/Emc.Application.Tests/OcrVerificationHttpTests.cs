using System.Net;
using System.Net.Http.Headers;
using Emc.Application.Abstractions;
using Emc.Application.Documents;
using Emc.Application.Ocr;
using Emc.Domain.Common;
using Emc.Domain.Ocr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// The verification page through the web host: upload, request OCR, run the processor with a
/// fake engine against the host's own database and store, then verify through the page.
/// Requirements: OCR-005, OCR-014, IAM-018.
/// </summary>
public class OcrVerificationHttpTests : IClassFixture<EmcWebFactory>, IClassFixture<UnregisteredPrincipalWebFactory>
{
    private readonly EmcWebFactory _registered;
    private readonly UnregisteredPrincipalWebFactory _unregistered;

    public OcrVerificationHttpTests(EmcWebFactory registered, UnregisteredPrincipalWebFactory unregistered)
    {
        _registered = registered;
        _unregistered = unregistered;
    }

    [Fact]
    public async Task UploadRequestRunAndVerify_ThroughThePage()
    {
        var client = _registered.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var example = await _registered.SeedWorkedExampleAsync(client);
        var voucherId = int.Parse(example.VoucherUrl.Split('/')[^1]);

        // Upload a companion copy.
        var uploadUrl = $"/Documents/Upload/{voucherId}";
        var token = await _registered.GetAntiForgeryTokenAsync(client, uploadUrl);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(token), "__RequestVerificationToken");
        form.Add(new StringContent("DaForm4137"), "Input.DocumentType");
        form.Add(new StringContent("PhysicalOriginal"), "Input.Provenance");
        form.Add(new StringContent("UNCLASSIFIED"), "Input.ClassificationMarking");
        var file = new ByteArrayContent(SyntheticPdf.SinglePage("TEST VERIFY PAGE"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "Input.File", "scan.pdf");
        using var upload = await client.PostAsync(uploadUrl, form);
        Assert.Equal(HttpStatusCode.Redirect, upload.StatusCode);
        var viewUrl = upload.Headers.Location!.ToString();
        var documentId = int.Parse(viewUrl.Split('/')[^1]);

        // Request OCR from the document page.
        token = await _registered.GetAntiForgeryTokenAsync(client, viewUrl);
        using var request = await client.PostAsync(viewUrl + "?handler=RequestOcr", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, request.StatusCode);

        // The worker, in miniature: the processor over the host's own database and store, with a fake engine.
        using (var scope = _registered.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var processor = new OcrJobProcessor(
                sp.GetRequiredService<IEmcDbContext>(), sp.GetRequiredService<ISourceDocumentStore>(),
                new StubEngine(), new StubPreprocessor(), [new GenericLineTemplateMapper()], sp.GetRequiredService<IClock>(),
                Options.Create(new OcrOptions { WorkerId = "test-worker" }), NullLogger<OcrJobProcessor>.Instance);
            Assert.True(await processor.ProcessNextAsync());
        }

        // The verification page: statement, fields, the run image, a form per field.
        var verifyUrl = $"/Documents/Verify/{documentId}";
        using var verify = await client.GetAsync(verifyUrl);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var html = await verify.Content.ReadAsStringAsync();
        Assert.Contains(Emc.Web.Pages.Documents.VerifyModel.CompanionStatement, html, StringComparison.Ordinal);
        Assert.Contains("Page[1].Line[1]", html, StringComparison.Ordinal);
        Assert.Contains("TEST WORD", html, StringComparison.Ordinal);
        Assert.Contains("handler=RunPage", html, StringComparison.Ordinal);
        Assert.Contains("mandatory verification(s) outstanding", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);

        using var runImage = await client.GetAsync(verifyUrl + "?handler=RunPage&runId=1&pageNumber=1");
        Assert.Equal(HttpStatusCode.OK, runImage.StatusCode);
        Assert.Equal("image/png", runImage.Content.Headers.ContentType!.MediaType);

        // Verify the field as corrected.
        var fieldId = int.Parse(System.Text.RegularExpressions.Regex.Match(html, @"name=""Input\.FieldId"" value=""(\d+)""").Groups[1].Value);
        token = await _registered.GetAntiForgeryTokenAsync(client, verifyUrl);
        using var post = await client.PostAsync(verifyUrl + "?handler=Verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.FieldId"] = fieldId.ToString(),
            ["Input.Decision"] = nameof(FieldVerificationDecision.CorrectedByVerifier),
            ["Input.EnteredValue"] = "TEST WORD CORRECTED",
            ["Input.Note"] = "read from the scan"
        }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);

        using var after = await client.GetAsync(verifyUrl);
        var afterHtml = await after.Content.ReadAsStringAsync();
        Assert.Contains("CorrectedByVerifier", afterHtml, StringComparison.Ordinal);
        Assert.Contains("TEST WORD CORRECTED", afterHtml, StringComparison.Ordinal);
        Assert.Contains("<code>TEST WORD</code>", afterHtml, StringComparison.Ordinal); // raw text still shown, unchanged
        Assert.Contains("<strong>0</strong> mandatory verification(s) outstanding", afterHtml, StringComparison.Ordinal);

        // An outsider sees nothing: page, image and verification handler alike (IAM-018).
        var outsider = _unregistered.CreateClient();
        foreach (var url in new[] { verifyUrl, verifyUrl + "?handler=RunPage&runId=1&pageNumber=1", "/Documents/Verify/999999" })
        {
            using var denied = await outsider.GetAsync(url);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
    }

    private sealed class StubEngine : IOcrEngine
    {
        public string EngineName => "fake";
        public string EngineVersion => "0.0";
        public IReadOnlyList<OcrModelInfo> Models { get; } = [new("eng", new string('0', 64))];
        public Task<OrientationResult> DetectOrientationAsync(byte[] png, CancellationToken ct = default) => Task.FromResult(new OrientationResult(0, 20m));
        public Task<OcrPageResult> RecognizeAsync(byte[] png, CancellationToken ct = default)
            => Task.FromResult(new OcrPageResult([new("TEST", 70m, 10, 10, 40, 20, 1, 1, 1, 1), new("WORD", 72m, 60, 10, 40, 20, 1, 1, 1, 2)], 200, 100));
    }

    private sealed class StubPreprocessor : IImagePreprocessor
    {
        public string Version => "stub/1";
        public PreprocessedImage Preprocess(byte[] png, int sourceDpi, int rotateClockwiseDegrees, CancellationToken ct = default)
            => new(png, 200, 100, rotateClockwiseDegrees, 0, sourceDpi);
    }
}
