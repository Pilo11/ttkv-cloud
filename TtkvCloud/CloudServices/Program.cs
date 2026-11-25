using CloudServices.Proxy;
using CloudServices.Services.Appointment;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// Secrets come as files (deploy/docker-compose.yml mounts them under /run/secrets, never as
// environment variables): the file name is the configuration key with "__" for ":", e.g.
// Termine__AdminKey. Without the directory (dotnet run, the root docker-compose.yml) nothing is added.
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

var aspNetCoreUrls = builder.Configuration["ASPNETCORE_URLS"]
    ?? builder.Configuration["Urls"];
if (!string.IsNullOrWhiteSpace(aspNetCoreUrls))
{
    builder.WebHost.UseUrls(aspNetCoreUrls);
}

builder.Services.AddRazorPages();
builder.Services.AddHealthChecks();

// Antiforgery and TempData keys outside the container (deploy/docker-compose.yml), so forms and
// messages stay valid across a rollout. Without the setting ASP.NET Core keeps its default.
var dataProtectionKeys = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeys))
{
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeys));
}
builder.Services.Configure<TermineOptions>(builder.Configuration.GetSection("Termine"));
builder.Services.AddSingleton<TermineStore>();
builder.Services.AddSingleton<StaffelleitungAccess>();
builder.Services.Configure<RouteOptions>(options =>
    options.ConstraintMap[StaffelleitungKeyConstraint.Name] = typeof(StaffelleitungKeyConstraint));
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(ClickTtProxyTransforms.Register);

var app = builder.Build();

// Resolve once at startup so the secret Staffelleitung path is created and logged.
app.Services.GetRequiredService<StaffelleitungAccess>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
// Checked by the container's health check and by the release workflow through Cloudflare.
app.MapHealthChecks("/healthz");
app.MapRazorPages();
app.MapReverseProxy();
app.Run();
