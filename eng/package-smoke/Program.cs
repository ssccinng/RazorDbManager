using Microsoft.AspNetCore.Authentication;
using RazorDbManager;
using RazorDbManager.Core;
using RazorDbManager.MySql;
using RazorDbManager.PackageSmoke;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Configuration["ConnectionStrings:MainDatabase"] ??=
    "Server=127.0.0.1;Port=1;Database=package_smoke;User ID=package_smoke;" +
    "SslMode=VerifyFull;PersistSecurityInfo=false;AllowLoadLocalInfile=false;Connection Timeout=1";

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services
    .AddAuthentication(PackageSmokeAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, PackageSmokeAuthenticationHandler>(
        PackageSmokeAuthenticationHandler.SchemeName,
        _ => { });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(RazorDbManagerPolicies.Access,
        policy => policy.RequireAuthenticatedUser().RequireRole("DatabaseAdmin"))
    .AddPolicy(RazorDbManagerPolicies.HighRisk,
        policy => policy.RequireAuthenticatedUser().RequireRole("DatabaseAdmin"));

builder.Services
    .AddRazorDbManager(options =>
    {
        options.DefaultDatabaseId = "Main";
        string? storagePath = builder.Configuration["PackageSmoke:StoragePath"];
        if (!string.IsNullOrWhiteSpace(storagePath)) options.StoragePath = storagePath;
    })
    .AddMySql("Main", options =>
    {
        options.ConnectionStringName = "MainDatabase";
        options.EnabledCapabilities = RazorDbCapabilitySets.DataEditor;
    });

WebApplication app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorDbManagerEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddRazorDbManagerPages();

app.Run();
