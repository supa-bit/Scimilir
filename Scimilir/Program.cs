// Scimilir: chat with AI models running on this PC, or in the cloud through
// OpenRouter, from this PC's browser or a phone on the same Wi-Fi.
using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Scimilir;
using Scimilir.Providers;
using Scimilir.Storage;

var builder = WebApplication.CreateBuilder(args);

// The data folder sits next to the program unless "--DataFolder <path>" says otherwise.
var paths = new AppPaths(builder.Configuration["DataFolder"] ?? Path.Combine(builder.Environment.ContentRootPath, "data"));
var settings = new SettingsStore(paths);
var port = builder.Configuration.GetValue<int?>("Port") ?? settings.Current.Port;
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";

// 0.0.0.0 means "answer on every network", which is what lets a phone connect.
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

static HttpClient MakeHttp(TimeSpan timeout) =>
    new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
    {
        Timeout = timeout,
        DefaultRequestHeaders = { { "User-Agent", "Scimilir" } },
    };

builder.Services
    .AddSingleton(paths)
    .AddSingleton(settings)
    .AddSingleton(new RunInfo(port, version))
    .AddSingleton<ChatStore>()
    .AddSingleton(TimeProvider.System)
    .AddSingleton<LoginThrottle>()
    .AddSingleton<LocalProvider>()
    .AddSingleton(_ => new OpenRouterProvider(MakeHttp(TimeSpan.FromMinutes(10)), settings))
    .AddSingleton(sp => new ModelDownloads(MakeHttp(Timeout.InfiniteTimeSpan), paths, sp.GetRequiredService<ILogger<ModelDownloads>>()))
    .AddSingleton(sp => new ProviderHub([sp.GetRequiredService<LocalProvider>(), sp.GetRequiredService<OpenRouterProvider>()]));

// Login keys are saved in data/keys so you stay logged in after a restart.
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("Scimilir")
    .PersistKeysToFileSystem(new DirectoryInfo(paths.Keys));
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "scimilir_login";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = c =>
        {
            c.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        // A login made before the password changed stops working.
        o.Events.OnValidatePrincipal = async c =>
        {
            if (c.Principal?.FindFirst("pw")?.Value != Passwords.Stamp(settings.Current.PasswordHash))
            {
                c.RejectPrincipal();
                await c.HttpContext.SignOutAsync();
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();
LocalProvider.ConfigureEngine(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LLama"));

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Always check for a newer page, so updates show up without clearing the browser.
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
});
app.UseAuthentication();
app.UseAuthorization();
app.MapScimilirApi();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var local = $"http://localhost:{port}";
    Console.WriteLine();
    Console.WriteLine($"  Scimilir {version} is running.");
    Console.WriteLine($"  On this PC:     {local}");
    foreach (var url in Network.LanUrls(port))
        Console.WriteLine($"  On your phone:  {url}   (phone must be on the same Wi-Fi)");
    Console.WriteLine($"  Files are kept in {paths.Data}");
    Console.WriteLine("  Keep this window open while you use Scimilir; close it to stop.");
    Console.WriteLine();
    if (OperatingSystem.IsWindows() && settings.Current.OpenBrowserOnStart)
    {
        try { Process.Start(new ProcessStartInfo(local) { UseShellExecute = true }); }
        catch (Exception) { /* No browser to open; the address is printed above. */ }
    }
});

try
{
    app.Run();
}
catch (IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException
                             || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"\n  Port {port} is already in use. Is Scimilir already running in another window?");
    Console.Error.WriteLine("  Close that window, or change \"Port\" in data/settings.json.\n");
    Environment.ExitCode = 1;
}

/// <summary>Lets the tests start the app.</summary>
public partial class Program;
