using AllInOnePdf.UI;
using AllInOnePdf.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Security: bind to loopback unless the operator explicitly configures Urls / ASPNETCORE_URLS.
if (string.IsNullOrEmpty(builder.Configuration["Urls"]) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5180");
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options => options.DetailedErrors = builder.Environment.IsDevelopment());

// Browser uploads are capped so a single request cannot exhaust memory.
builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(o => o.DetailedErrors = builder.Environment.IsDevelopment());
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 256L * 1024 * 1024);

builder.Services.AddAllInOnePdfUI();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    // Offline by design: the browser may only talk to this origin (SignalR circuit included) and load inline images.
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; connect-src 'self' ws: wss:; img-src 'self' data: blob:; " +
        "style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline' 'unsafe-eval'; font-src 'self' data:; " +
        "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Routes).Assembly);

app.Run();
