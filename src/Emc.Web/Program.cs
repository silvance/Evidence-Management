using Emc.Infrastructure;
using Emc.Web.Security;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Emc")
    ?? throw new InvalidOperationException(
        "Connection string 'Emc' is not configured. See appsettings.json.");

builder.Services.AddEmcInfrastructure(connectionString);

// DOC-004. Upload limits at the REQUEST layer: the server (Kestrel here; under IIS in-process
// the per-page RequestSizeLimit attribute sets the same server feature, and web.config's
// requestLimits caps the request before it reaches .NET at all) and multipart form parsing both
// refuse a body larger than the configured maximum before any application code runs. The same
// figure is enforced again in SourceDocumentService, and the upload page carries the attribute
// form of it. Several layers, one number.
var uploadLimit = builder.Configuration.GetValue<long?>("SourceDocuments:MaxContentBytes") ?? 50L * 1024 * 1024;
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = uploadLimit + 64 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = uploadLimit;
    options.ValueLengthLimit = 64 * 1024;
    options.MultipartHeadersLengthLimit = 16 * 1024;
});

// IAM-003: Windows Authentication (Negotiate/Kerberos), which in an Army environment is
// CAC-backed. EMC stores no passwords and no password hashes.
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();

// IAM-002: no page is reachable without an authenticated identity. Roles are resolved
// server-side from the database on each request; no role information is read from the client.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddScoped<IEmcPageAuthorization, EmcPageAuthorization>();

builder.Services.AddRazorPages(options =>
    // Anti-forgery validation on every state-changing request, applied globally so that a page
    // added later cannot forget it.
    options.Conventions.ConfigureFilter(new AutoValidateAntiforgeryTokenAttribute()));

builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// The application runs with no internet dependency, so the policy can be strict: nothing loads
// from anywhere but this origin.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; "
        + "object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

// AUD-012: migrations are applied by a deliberate deployment step with a higher-privilege login,
// never on startup. Silent schema change on an accountability system is unacceptable, and the
// application's runtime login is not granted the rights to do it (docs/architecture.md §11).
app.Run();

/// <summary>
/// Exposed so the test host can reference the entry point and render the pages with a test
/// identity against SQLite. Without this the Razor views would only ever be compile-checked, and
/// runtime rendering faults would reach production.
/// </summary>
public partial class Program;
