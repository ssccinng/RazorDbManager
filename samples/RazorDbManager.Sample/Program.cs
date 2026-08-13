using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using RazorDbManager;
using RazorDbManager.Core;
using RazorDbManager.MySql;
using RazorDbManager.Sample;
using RazorDbManager.Sample.Components;

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "RazorDbManager.Sample uses a development-only fixed administrator identity and must not run outside Development. " +
        "Install the packages in a host with real authentication for production use.");
}
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
var sampleOptions = builder.Configuration
    .GetSection(SampleDatabaseOptions.SectionName)
    .Get<SampleDatabaseOptions>() ?? new SampleDatabaseOptions();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddLocalization();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options
        .SetDefaultCulture(SampleCultures.Default)
        .AddSupportedCultures(SampleCultures.Supported)
        .AddSupportedUICultures(SampleCultures.Supported);
});
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(
        Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtection-Keys")));

builder.Services.AddAuthentication("Sample")
    .AddScheme<AuthenticationSchemeOptions, SampleAuthenticationHandler>("Sample", _ => { });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(RazorDbManagerPolicies.Access, policy =>
        policy.RequireAuthenticatedUser().RequireRole("DatabaseAdmin"))
    .AddPolicy(RazorDbManagerPolicies.HighRisk, policy =>
        policy.RequireAuthenticatedUser().RequireRole("DatabaseAdmin"));
builder.Services.AddSingleton<IRazorDbBackgroundAuthorizer, SampleBackgroundAuthorizer>();
builder.Services.AddSingleton<IRazorDbSessionValidator, SampleSessionValidator>();

builder.Services
    .AddRazorDbManager(options => options.DefaultDatabaseId = "Main")
    .AddMySql("Main", options =>
    {
        options.ConnectionStringName = sampleOptions.ReaderConnectionStringName;
        options.WriterConnectionStringName = sampleOptions.WriterConnectionStringName;
        options.SchemaConnectionStringName = sampleOptions.SchemaConnectionStringName;
        options.SqlConsoleConnectionStringName = sampleOptions.SqlConsoleConnectionStringName;
        options.EnabledCapabilities = sampleOptions.EnabledCapabilities();
        options.AllowSharedHighRiskCredential = sampleOptions.AllowSharedHighRiskCredential;
        options.AllowInsecureDevelopmentConnection = builder.Environment.IsDevelopment()
            && sampleOptions.AllowInsecureDevelopmentConnection;
        options.EnableSqlRestore = sampleOptions.EnableSqlRestore;
        foreach (string schema in sampleOptions.AllowedSchemas)
        {
            options.AllowedSchemas.Add(schema);
        }
    });

var app = builder.Build();
app.Use(async (context, next) =>
{
    System.Net.IPAddress? remoteAddress = context.Connection.RemoteIpAddress;
    if (remoteAddress is null || !System.Net.IPAddress.IsLoopback(remoteAddress))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await next(context);
});
app.UseRequestLocalization();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/culture/{culture}", (string culture, string? returnUrl, HttpContext context) =>
{
    var selectedCulture = SampleCultures.GetSupported(culture);
    if (selectedCulture is null)
    {
        return Results.BadRequest();
    }

    context.Response.Cookies.Append(
        CookieRequestCultureProvider.DefaultCookieName,
        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(selectedCulture)),
        new CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddYears(1),
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "/",
        });

    return Results.LocalRedirect(SampleCultures.GetLocalReturnUrl(returnUrl));
});
app.MapRazorDbManagerEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddRazorDbManagerPages();

app.Run();

internal static class SampleCultures
{
    internal const string Default = "en-US";

    internal static readonly string[] Supported = [Default, "zh-CN"];

    internal static string? GetSupported(string requestedCulture) =>
        Supported.FirstOrDefault(culture =>
            string.Equals(culture, requestedCulture, StringComparison.OrdinalIgnoreCase));

    internal static string GetLocalReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) ||
            returnUrl[0] != '/' ||
            returnUrl.StartsWith("//", StringComparison.Ordinal) ||
            returnUrl.StartsWith("/\\", StringComparison.Ordinal))
        {
            return "/";
        }

        return returnUrl;
    }
}
