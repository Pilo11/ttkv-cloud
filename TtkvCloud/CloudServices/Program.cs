using CloudServices.Proxy;

var builder = WebApplication.CreateBuilder(args);

var aspNetCoreUrls = builder.Configuration["ASPNETCORE_URLS"]
    ?? builder.Configuration["Urls"];
if (!string.IsNullOrWhiteSpace(aspNetCoreUrls))
{
    builder.WebHost.UseUrls(aspNetCoreUrls);
}

builder.Services.AddRazorPages();
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(ClickTtProxyTransforms.Register);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.MapReverseProxy();
app.Run();
