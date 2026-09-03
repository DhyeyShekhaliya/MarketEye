using Microsoft.AspNetCore.HttpOverrides;
using MarketEye.Web.Components;
using MarketEye.Web.MarketData;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Blazor Server calls the API over HTTP. Base address comes from config so the same build runs
// locally and on App Service.
builder.Services.AddHttpClient("api", (sp, client) =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["Api:BaseUrl"] ?? "http://localhost:5199";
    client.BaseAddress = new Uri(baseUrl);
});
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("api"));

// Homepage NIFTY 50 ticker only (MarketData/YahooFinanceClient.cs) -- a typed client so it never
// collides with the plain HttpClient above, which every page injects directly for the API.
// User-Agent is set because Yahoo's endpoint is known to reject requests carrying .NET's default
// one.
builder.Services.AddHttpClient<YahooFinanceClient>(client =>
{
    client.BaseAddress = new Uri("https://query1.finance.yahoo.com/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// App Service terminates TLS at its own front door and forwards to this app over plain HTTP,
// tagging the original scheme in X-Forwarded-Proto. Without this, UseHttpsRedirection/UseHsts
// below never see a request they consider secure and redirect every single one -- which Azure's
// front door then forwards again as plain HTTP, looping forever (docs/azure-deployment-web-runbook.md §3c).
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});

app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
