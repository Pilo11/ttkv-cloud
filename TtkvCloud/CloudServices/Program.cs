using CloudServices.Proxy;
using CloudServices.Services.Appointment;

var builder = WebApplication.CreateBuilder(args);

var aspNetCoreUrls = builder.Configuration["ASPNETCORE_URLS"]
    ?? builder.Configuration["Urls"];
if (!string.IsNullOrWhiteSpace(aspNetCoreUrls))
{
    builder.WebHost.UseUrls(aspNetCoreUrls);
}

builder.Services.AddRazorPages();
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
app.MapRazorPages();
app.MapReverseProxy();
app.Run();
