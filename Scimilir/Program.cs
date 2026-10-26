// Scimilir: chat with AI models running on this PC, or in the cloud through
// OpenRouter, from this PC's browser or a phone on the same Wi-Fi.
using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Scimilir;
using Scimilir.Providers;
using Scimilir.Storage;

// "--background" (used when Scimilir starts with Windows) means: don't open the browser.
var background = args.Contains("--background");
args = [.. args.Where(a => a != "--background")];

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
#if WINDOWS_APP
    // The installed app finds its page files next to Scimilir.exe, wherever it was started from.
    ContentRootPath = AppContext.BaseDirectory,
#endif
});

// Settings, chats and models live in the user's app-data folder unless "--DataFolder <path>" says otherwise.
var paths = new AppPaths(builder.Configuration["DataFolder"] ?? Startup.DefaultDataFolder());
var settings = new SettingsStore(paths);
var port = builder.Configuration.GetValue<int?>("Port") ?? settings.Current.Port;
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";
var localUrl = $"http://localhost:{port}";

// Started twice? Just show the one that's already running.
if (builder.Configuration["DataFolder"] is null && Startup.AlreadyRunning(port))
{
    if (!background) Browser.Open(localUrl);
    return;
}

#if WINDOWS_APP
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(paths.Data, "scimilir.log")));
#endif

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

var firstRun = settings.Current.PasswordHash is null;
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine();
    Console.WriteLine($"  Scimilir {version} is running.");
    Console.WriteLine($"  On this PC:     {localUrl}");
    foreach (var url in Network.LanUrls(port))
        Console.WriteLine($"  On your phone:  {url}   (phone must be on the same Wi-Fi)");
    Console.WriteLine($"  Files are kept in {paths.Data}");
    Console.WriteLine();
#if WINDOWS_APP
    TrayIcon.Start(localUrl, app.Lifetime, firstRunTip: firstRun);
#endif
    if (!background && settings.Current.OpenBrowserOnStart) Browser.Open(localUrl);
});

try
{
    app.Run();
}
catch (IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException
                             || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
{
    var message = $"Scimilir couldn't start: another program is using network port {port}.\n\n" +
                  $"To use a different port, change \"Port\" in {paths.SettingsFile}.";
#if WINDOWS_APP
    TrayIcon.ShowError(message);
#endif
    Console.Error.WriteLine("\n  " + message + "\n");
    Environment.ExitCode = 1;
}

/// <summary>Lets the tests start the app.</summary>
public partial class Program;
