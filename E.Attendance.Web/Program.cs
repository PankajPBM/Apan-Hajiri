using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using E.Attendance.Core.Logging;
using E.Attendance.Core.Models;
using E.Attendance.Data;
using E.Attendance.Data.Production;
using E.Attendance.Services;
using E.Attendance.Services.Production;
using E.Attendance.Web.Licensing;
using E.Attendance.Web.Production;

AppDomain.CurrentDomain.UnhandledException += static (sender, e) =>
{
    AppEventLogger.LogFatal("AppDomain", "Unhandled fatal exception occurred in Web service.", e.ExceptionObject as Exception);
};
TaskScheduler.UnobservedTaskException += static (sender, e) =>
{
    AppEventLogger.LogError("TaskScheduler", "Unobserved task exception in Web service.", null, e.Exception);
    e.SetObserved();
};

var contentRootPath = ResolveContentRootPath();
var webRootPath = ResolveWebRootPath(contentRootPath);
AppEventLogger.LogInfo("Startup", $"E.Attendance.Web service initializing. ContentRoot: {contentRootPath}, WebRoot: {webRootPath}, LogDir: {AppEventLogger.LogDirectory}");
Console.WriteLine($"[Startup] ContentRootPath: {contentRootPath}");
Console.WriteLine($"[Startup] WebRootPath: {webRootPath}");

var filteredArgs = args.Where(a => !a.StartsWith("--urls", StringComparison.OrdinalIgnoreCase)).ToArray();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = filteredArgs,
    ContentRootPath = contentRootPath,
    WebRootPath = webRootPath
});
builder.Host.UseWindowsService();
builder.Services.AddControllers();

string appDataRoot;
try
{
    appDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "E_Attendance");
    Directory.CreateDirectory(appDataRoot);
}
catch
{
    appDataRoot = Path.Combine(AppContext.BaseDirectory, "Data");
    Directory.CreateDirectory(appDataRoot);
}

// Self-contained JWT signing key: generated once per install and persisted alongside the
// license data, so tokens survive app restarts but a fresh install/service reinstall starts clean.
var jwtSigningKey = LoadOrCreateJwtSigningKey(Path.Combine(appDataRoot, "auth-signing.key"));
const string JwtIssuer = "EAttendance";
const string JwtAudience = "EAttendanceApi";

builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = JwtIssuer,
            ValidateAudience = true,
            ValidAudience = JwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(jwtSigningKey),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
    {
        policy
            .SetIsOriginAllowed(static origin => IsAllowedFrontendOrigin(origin))
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});
var postgresConnectionString = builder.Configuration.GetConnectionString("PostgreSQLConnection");
if (string.IsNullOrWhiteSpace(postgresConnectionString))
{
    postgresConnectionString = BuildDefaultPostgresConnectionString();
    Console.WriteLine("[Startup] appsettings.json PostgreSQLConnection was missing. Using built-in default PostgreSQL settings.");
}

postgresConnectionString = ResolvePostgresConnectionString(postgresConnectionString);

const string DefaultAreaCode = "1";
const string DefaultAreaName = "Default Area";
const string DefaultDepartmentCode = "1";
const string DefaultDepartmentName = "Default Department";

var isServiceMode = args.Any(a => a.Equals("--service-mode", StringComparison.OrdinalIgnoreCase));
var serviceRole = ResolveServiceRole(args);
var personnelStore = new PersonnelStore();
var attLogRangeStartBySerial = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

builder.Services.AddDbContext<PbAttendanceDbContext>(options =>
{
    options.UseNpgsql(postgresConnectionString);
});
builder.Services.AddScoped<AttendanceProcessingEngine>();
builder.Services.AddScoped<ConfigSyncService>();
E.Attendance.Web.ServiceExtensions.AddPostgresModules(builder.Services, postgresConnectionString);

var settingsPath = Path.Combine(appDataRoot, "websettings.json");
var settings = EnsureBindableRuntimePorts(WebAppSettings.Load(settingsPath), settingsPath);
builder.WebHost.UseUrls(BuildListenUrls(settings.Port, settings.FrontendPort, serviceRole));

static async Task<bool> TryServeNextFlightFileAsync(HttpContext context, string? webRoot)
{
    if (string.IsNullOrWhiteSpace(webRoot))
    {
        return false;
    }

    var path = context.Request.Path.Value ?? string.Empty;
    if (!HttpMethods.IsGet(context.Request.Method) || !path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var trimmed = path.Trim('/');
    if (string.IsNullOrEmpty(trimmed))
    {
        return false;
    }

    // 1. Direct file check on disk
    var directPath = Path.Combine(webRoot, trimmed.Replace('/', Path.DirectorySeparatorChar));
    if (File.Exists(directPath))
    {
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.SendFileAsync(directPath);
        return true;
    }

    // 2. Next.js 16 RSC flight mapping:
    // Requested URL: .../__next.{group}.{subpath}.__PAGE__.txt
    // On disk:       .../__next.{group}/{subpath}/__PAGE__.txt
    if (trimmed.Contains("__next."))
    {
        var lastSlash = trimmed.LastIndexOf('/');
        var dirPrefix = lastSlash >= 0 ? trimmed[..lastSlash] : string.Empty;
        var fileName = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;

        if (fileName.StartsWith("__next.", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            var withoutExt = fileName[..^4]; // strip ".txt"
            var expandedPath = withoutExt.Replace('.', Path.DirectorySeparatorChar) + ".txt";
            var mappedCandidate = string.IsNullOrEmpty(dirPrefix)
                ? Path.Combine(webRoot, expandedPath)
                : Path.Combine(webRoot, dirPrefix.Replace('/', Path.DirectorySeparatorChar), expandedPath);

            if (File.Exists(mappedCandidate))
            {
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.SendFileAsync(mappedCandidate);
                return true;
            }
        }
    }

    // 3. Fallback to index.txt if present in the target route directory
    var lastSlashIdx = trimmed.LastIndexOf('/');
    var routeDir = lastSlashIdx >= 0 ? trimmed[..lastSlashIdx] : string.Empty;
    if (!string.IsNullOrEmpty(routeDir))
    {
        var indexTxtPath = Path.Combine(webRoot, routeDir.Replace('/', Path.DirectorySeparatorChar), "index.txt");
        if (File.Exists(indexTxtPath))
        {
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.SendFileAsync(indexTxtPath);
            return true;
        }
    }

    return false;
}

if (string.Equals(serviceRole, "web", StringComparison.OrdinalIgnoreCase))
{
    var webOnlyApp = builder.Build();
    webOnlyApp.Use(async (context, next) =>
    {
        if (await TryServeNextFlightFileAsync(context, webOnlyApp.Environment.WebRootPath))
        {
            return;
        }
        await next();
    });
    webOnlyApp.UseDefaultFiles();
    webOnlyApp.UseStaticFiles();
    webOnlyApp.MapGet("/api-config.json", () => Results.Json(new { apiPort = settings.Port, webPort = settings.FrontendPort }));
    webOnlyApp.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "E-Attendance-Web", role = "web" }));
    webOnlyApp.MapFallback((HttpContext context) =>
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/iclock", StringComparison.OrdinalIgnoreCase))
        {
            return Results.NotFound(new { message = $"API endpoint '{path}' is hosted on API port {settings.Port}." });
        }
        var webRoot = webOnlyApp.Environment.WebRootPath;
        if (HttpMethods.IsGet(context.Request.Method))
        {
            var trimmed = path.Trim('/');
            if (!string.IsNullOrEmpty(trimmed))
            {
                var indexPath = Path.Combine(webRoot, trimmed.Replace('/', Path.DirectorySeparatorChar), "index.html");
                if (File.Exists(indexPath))
                {
                    if (!path.EndsWith('/'))
                    {
                        var query = context.Request.QueryString.Value ?? string.Empty;
                        return Results.Redirect(path + "/" + query, permanent: false);
                    }

                    return Results.File(indexPath, "text/html");
                }

                var htmlPath = Path.Combine(webRoot, trimmed.Replace('/', Path.DirectorySeparatorChar) + ".html");
                if (File.Exists(htmlPath))
                {
                    return Results.File(htmlPath, "text/html");
                }
            }
        }

        return Results.File(Path.Combine(webRoot, "index.html"), "text/html");
    });
    webOnlyApp.Run();
    return;
}

var devicePollDelaySeconds = builder.Configuration.GetValue<int?>("DeviceSettings:PollDelaySeconds") ?? 10;
var deviceDefaultTimeZoneMinutes = DeviceProtocolService.TimezoneToMinutes(
    builder.Configuration.GetValue<string?>("DeviceSettings:DefaultTimeZone") ?? "+5:45");
var deviceEnableAttPhoto = builder.Configuration.GetValue<bool?>("DeviceSettings:EnableAttPhoto") ?? false;
var deviceAllowUnknownEmployeeAutoCreate = builder.Configuration.GetValue<bool?>("DeviceSettings:AllowUnknownEmployeeAutoCreate") ?? true;

var personnelPath = Path.Combine(appDataRoot, "personnel.json");

var repository = new PostgresAttendanceRepository(postgresConnectionString);
var protocolService = new DeviceProtocolService();
var service = new AttendanceService(repository, protocolService);
// Use this singleton everywhere (incl. ConfigSyncService) so commands queued by background
// sync use the SAME instance that delivers commands and records device replies.
builder.Services.AddSingleton<E.Attendance.Core.Interfaces.IAttendanceService>(service);

// Heavy initialization moved to background task to prevent Windows Service startup timeout (Error 1053)
_ = Task.Run(() =>
{
    try
    {
        TryStartPostgreSqlService(postgresConnectionString);
        // This runs in a background task (does NOT block service start), so we can wait as long as
        // needed for a freshly-installed PostgreSQL to come up — no risk of Error 1053.
        WaitForPostgresConnection(postgresConnectionString, maxWaitSeconds: 300);
        // CRITICAL: keep retrying schema creation until the core tables actually exist. On a fresh
        // install PostgreSQL can accept a connection before it's fully ready, so a single attempt can
        // run too early and create nothing — which is what caused "relation auth_users does not exist".
        // This runs in a background task and must NEVER give up for a fresh install, so we keep
        // retrying for a long window (up to ~15 min) so a slow first-time PostgreSQL init still
        // ends with a complete schema and zero manual steps. The login endpoint also self-heals
        // auth_users on demand as a final safety net.
        var schemaReady = false;
        for (var attempt = 1; attempt <= 450; attempt++)
        {
            try
            {
                EnsurePostgresSchema(postgresConnectionString); // idempotent — IF NOT EXISTS
                attLogRangeStartBySerial.Clear();
                MigrateBiometricFkConstraints(postgresConnectionString);
                if (PostgresCoreTablesExist(postgresConnectionString))
                {
                    Console.WriteLine($"[Startup] Database schema verified ready (attempt {attempt}).");
                    schemaReady = true;
                    break;
                }
                Console.Error.WriteLine($"[Startup] Schema not complete yet after attempt {attempt}; retrying...");
            }
            catch (Exception schemaEx)
            {
                Console.Error.WriteLine($"[Startup] Schema attempt {attempt} failed: {schemaEx.Message}; retrying...");
            }
            System.Threading.Thread.Sleep(2000);
        }
        if (!schemaReady)
        {
            Console.Error.WriteLine("[Startup] Schema not confirmed ready after retries; endpoints self-heal on demand.");
        }

        var dbStore = LoadPersonnelStoreFromDb(postgresConnectionString);
        ReplacePersonnelStoreContents(personnelStore, dbStore);

        if (IsPersonnelStoreEmpty(personnelStore) && File.Exists(personnelPath))
        {
            var legacyPersonnelStore = PersonnelStore.Load(personnelPath);
            if (!IsPersonnelStoreEmpty(legacyPersonnelStore))
            {
                ReplacePersonnelStoreContents(personnelStore, legacyPersonnelStore);
                PersistPersonnelSetupToDb(postgresConnectionString, personnelStore);
                HydratePersonnelStoreFromDb(personnelStore, postgresConnectionString);
                TryArchiveLegacyPersonnelFile(personnelPath);
                Console.WriteLine($"[Startup] Migrated legacy personnel store '{personnelPath}' to PostgreSQL.");
            }
        }

        EnsurePersonnelSetupDefaults(
            personnelStore,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName);
        EnsureDeviceAssignmentsForExistingDevices(
            service,
            personnelStore,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName);
        // Do not auto-backfill employees from local profile cache at startup.
        // Employee rows must come from explicit device/user sync or manual personnel actions.
        NormalizeStoredPasswordsToMd5(
            repository,
            personnelStore,
            personnelPath,
            postgresConnectionString);
        NormalizeEmployeeDepartmentsToSetup(
            repository,
            personnelStore,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName);
        NormalizeEmployeePositionsToSetup(
            repository,
            personnelStore);
        NormalizePositionsToSetup(
            personnelStore,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName);
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
        EnsureDefaultSoftwareLoginInDb(postgresConnectionString);
        PurgeSoftwareLoginsFromDeviceStores(
            postgresConnectionString,
            repository,
            personnelStore,
            personnelPath);
        Console.WriteLine("[Startup] Background database and personnel initialization completed.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Startup] Background initialization failed: {ex.Message}");
    }
});

var app = builder.Build();

// Write a build marker file so you can VERIFY the newly-built code is actually deployed.
// Check C:\ProgramData\E_Attendance\BUILD_MARKER.txt after installing.
try
{
    var markerDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "E_Attendance");
    Directory.CreateDirectory(markerDir);
    File.WriteAllText(
        Path.Combine(markerDir, "BUILD_MARKER.txt"),
        $"Build: SCHEMA-TEXT-INTEGER-FIX-2026-06-20{Environment.NewLine}" +
        $"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
        $"Assembly: {System.Reflection.Assembly.GetExecutingAssembly().Location}{Environment.NewLine}");
}
catch { /* non-fatal */ }

EnsureBundledWebUiFiles(app.Environment.WebRootPath);
var configuredLicenseDirectory = builder.Configuration["License:Directory"];
// Default license directory is in C:\ProgramData\E_Attendance\license so it is the
// same path whether the app runs as a Windows service (LocalSystem) or interactively.
// Previously it defaulted to %APPDATA%\PB_Attendance which differs between LocalSystem
// and normal users, meaning the installer could not clear the trial-state.json correctly.
var licenseDirectory = string.IsNullOrWhiteSpace(configuredLicenseDirectory)
    ? Path.Combine(appDataRoot, "license")
    : ResolveLicenseDirectory(configuredLicenseDirectory);
var trialStatePath = Path.Combine(licenseDirectory, "trial-state.json");
const int TrialDurationDays = 30;
const int TrialMaxDevices = 2;
var licenseGuard = new LicenseGuard(new LicenseRuntimeOptions(
    appDataRoot,
    app.Environment.ContentRootPath,
    AppContext.BaseDirectory,
    licenseDirectory,
    builder.Configuration["License:PublicKeyPath"]));
var licenseState = BuildLicenseAccessSnapshot(licenseGuard, trialStatePath, TrialDurationDays, licenseDirectory);
var licenseRefreshSync = new object();
var lastLicenseRefreshUtc = licenseState.CheckedAtUtc;
if (!licenseState.IsAccessGranted)
{
    Console.Error.WriteLine(
        $"[License] Startup access check failed: {licenseState.Validation.Code} - {licenseState.ActivationMessage}");
}
else if (licenseState.IsTrialActive)
{
    Console.WriteLine(
        $"[License] Trial mode active. {licenseState.TrialDaysRemaining} day(s) remaining until {licenseState.TrialExpiryUtc:yyyy-MM-dd HH:mm:ss} UTC.");
}
else
{
    Console.WriteLine(
        $"[License] Valid license loaded. Expires at {licenseState.Validation.ExpiryUtc:yyyy-MM-dd HH:mm:ss} UTC.");
}

// Declared early (ahead of app.UseCors) so the exception-handling middleware below — which is
// registered first in the pipeline so it wraps everything downstream — can call AddLog directly
// instead of only Console.Error, which is invisible for a Windows Service with no attached console.
var appLogs = new ConcurrentQueue<AppLogEntry>();
var appLogPersistChannel = Channel.CreateBounded<AppLogEntry>(new BoundedChannelOptions(8000)
{
    FullMode = BoundedChannelFullMode.DropOldest,
    SingleReader = true,
    SingleWriter = false
});

app.UseCors("frontend");

// Ensure CORS headers are present even on 500 errors so the browser
// can read the error body instead of showing a generic CORS-block message.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[UnhandledException] {context.Request.Path}: {ex.Message}");
        var path = context.Request.Path.Value ?? string.Empty;
        // ZK PUSH devices treat any non-"OK" response body (and non-200 status) as "upload
        // failed" and silently retry the exact same payload forever — with zero visibility if
        // whatever throws does so before that request's own AddLog call. So for /iclock/* traffic,
        // never let an exception surface as a 500: log it here (this is the one place guaranteed
        // to run) and answer "OK" so the device moves on instead of looping invisibly.
        if (path.StartsWith("/iclock", StringComparison.OrdinalIgnoreCase))
        {
            AddLog(
                "WARN",
                "DeviceEvent",
                $"Unhandled exception on {path} — responded OK to avoid a silent device retry loop.",
                $"error={ex.GetType().Name}: {ex.Message}");
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync("OK");
            }
            return;
        }
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            var msg = System.Text.Json.JsonSerializer.Serialize(new { error = "internal_error", message = ex.Message });
            await context.Response.WriteAsync(msg);
        }
    }
});

app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    var isLicensePath = path.StartsWith("/api/license", StringComparison.OrdinalIgnoreCase);
    var isHealthPath = path.Equals("/health", StringComparison.OrdinalIgnoreCase);
    if (isLicensePath || isHealthPath)
    {
        await next();
        return;
    }

    var nowUtc = DateTimeOffset.UtcNow;
    if (nowUtc - lastLicenseRefreshUtc >= TimeSpan.FromSeconds(30))
    {
        lock (licenseRefreshSync)
        {
            if (DateTimeOffset.UtcNow - lastLicenseRefreshUtc >= TimeSpan.FromSeconds(30))
            {
                try
                {
                    licenseState = BuildLicenseAccessSnapshot(licenseGuard, trialStatePath, TrialDurationDays, licenseDirectory);
                    lastLicenseRefreshUtc = licenseState.CheckedAtUtc;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[License] Background refresh failed: {ex.Message}");
                    // Keep old licenseState to avoid breaking the middleware
                }
            }
        }
    }

    if (licenseState.IsAccessGranted)
    {
        await next();
        return;
    }

    if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status423Locked;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "license_invalid",
            message = licenseState.ActivationMessage,
            code = licenseState.Validation.Code,
            machineHash = licenseState.Validation.MachineHash,
            expiryUtc = licenseState.EffectiveExpiryUtc,
            activationMode = licenseState.ActivationMode,
            trialDaysRemaining = licenseState.TrialDaysRemaining
        });
        return;
    }
    // Allow the web UI to render the login page, while keeping APIs locked.
    await next();
});
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    var isApiPath = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
    if (!isApiPath || IsPubliclyAllowedApiRequest(context.Request.Method, path))
    {
        await next();
        return;
    }

    if (context.User.Identity?.IsAuthenticated != true)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "unauthorized", message = "Login required." });
        return;
    }

    await next();
});
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "/";
    var localPort = context.Connection.LocalPort;
    var isDeviceRoute = IsDeviceRoute(path);
    var apiPort = settings.Port;
    var webPort = settings.FrontendPort;
    var isApiPath = path.StartsWith("/api", StringComparison.OrdinalIgnoreCase);
    var isHealthPath = string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase);
    var isOptions = HttpMethods.IsOptions(context.Request.Method);

    // Allow CORS preflight (OPTIONS) to pass through to CORS middleware
    if (isOptions)
    {
        await next();
        return;
    }

    if (apiPort != webPort)
    {
        if (localPort == webPort)
        {
            if (isDeviceRoute)
            {
                Console.WriteLine(
                    $"[WARN] [DeviceEvent] Device request received on Web UI port. path={path}; expectedApiPort={apiPort}; actualPort={localPort}; ip={context.Connection.RemoteIpAddress}");
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync($"Device endpoints are available on port {apiPort}.");
                return;
            }
        }
        else if (localPort == apiPort)
        {
            if (!(isApiPath || isDeviceRoute || isHealthPath))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync($"Web UI is available on port {webPort}.");
                return;
            }
        }
    }

    await next();
});
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;

    if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) &&
        !path.StartsWith("/iclock", StringComparison.OrdinalIgnoreCase) &&
        HttpMethods.IsGet(context.Request.Method))
    {
        if (await TryServeNextFlightFileAsync(context, webRootPath))
        {
            return;
        }

        var trimmed = path.Trim('/');
        if (!string.IsNullOrEmpty(trimmed))
        {
            var indexPath = Path.Combine(webRootPath, trimmed.Replace('/', Path.DirectorySeparatorChar), "index.html");
            if (File.Exists(indexPath))
            {
                if (!path.EndsWith('/'))
                {
                    var query = context.Request.QueryString.Value ?? string.Empty;
                    context.Response.Redirect(path + "/" + query, permanent: false);
                    return;
                }

                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(indexPath);
                return;
            }

            var htmlPath = Path.Combine(webRootPath, trimmed.Replace('/', Path.DirectorySeparatorChar) + ".html");
            if (File.Exists(htmlPath))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(htmlPath);
                return;
            }
        }
    }

    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api-config.json", () => Results.Json(new { apiPort = settings.Port, webPort = settings.FrontendPort }));
app.Use(async (context, next) =>
{
    await next();

    if (context.Response.StatusCode < 200 || context.Response.StatusCode >= 300)
    {
        return;
    }

    var method = context.Request.Method;
    if (!(HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsDelete(method) || HttpMethods.IsPatch(method)))
    {
        return;
    }

    var path = context.Request.Path.Value ?? string.Empty;
    if (!IsPersonnelSetupMutationPath(path))
    {
        return;
    }

    PersistPersonnelSetupToDb(postgresConnectionString, personnelStore);
});
app.MapFallback((HttpContext context) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/iclock", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, "/api-config.json", StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound(new { message = $"Endpoint '{path}' not found." });
    }
    return Results.File(Path.Combine(webRootPath, "index.html"), "text/html");
});
// Database init runs in background so the Windows service can notify SCM quickly (avoids Error 1053).
// The API will return 503 for ~3 seconds while DB init completes; after that it's fully operational.
_ = Task.Run(async () =>
{
    // Small delay so app.Run() below can signal the SCM first
    await Task.Delay(TimeSpan.FromSeconds(3));
    try
    {
        using var scope = app.Services.CreateScope();
        var productionDb = scope.ServiceProvider.GetRequiredService<PbAttendanceDbContext>();
        productionDb.Database.EnsureCreated();
        // Run again after EnsureCreated so legacy tables are cleaned up
        EnsureDefaultSoftwareLoginInDb(postgresConnectionString);
        Console.WriteLine("[Startup] Database initialization completed in background.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"[Startup] Background database initialization failed. Service running in degraded mode. Details: {ex.Message}");
    }
});

var requestCount = 0L;
var requestLog = new ConcurrentQueue<string>();
var serverRunning = true;
var discoveredDevices = new ConcurrentDictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
// Persist device discovery state (user/fp/face/txn counts) to ProgramData so the dashboard shows
// real numbers immediately after a service restart / PC reboot instead of 0 until each device next
// reports in. The authoritative attendance/employee data lives in PostgreSQL; this is just a cache
// of the device-reported counts so they survive a restart.
var discoveredDevicesPath = Path.Combine(appDataRoot, "discovered-devices.json");
try
{
    if (File.Exists(discoveredDevicesPath))
    {
        var savedJson = File.ReadAllText(discoveredDevicesPath);
        var saved = System.Text.Json.JsonSerializer.Deserialize<List<DiscoveredDevice>>(savedJson);
        if (saved is not null)
        {
            foreach (var dev in saved)
            {
                if (!string.IsNullOrWhiteSpace(dev.SerialNumber))
                {
                    // Keep last-known counts; mark offline until the device actually reconnects.
                    discoveredDevices[dev.SerialNumber.Trim()] = dev with { IsOnline = false };
                }
            }
        }
    }
}
catch { /* non-fatal — start with an empty discovery cache */ }
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(60));
        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-15);
            var staleSerials = discoveredDevices.Values
                .Where(d => d.LastActivityUtc < cutoff)
                .Select(d => d.SerialNumber)
                .ToList();
            foreach (var serial in staleSerials)
            {
                discoveredDevices.TryRemove(serial, out _);
            }
            var snapshot = discoveredDevices.Values.ToList();
            File.WriteAllText(discoveredDevicesPath, System.Text.Json.JsonSerializer.Serialize(snapshot));
        }
        catch { /* ignore transient IO errors */ }
    }
});
var metadataProbeUtcBySerial = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
var commandSerialById = new ConcurrentDictionary<Guid, string>();
var autoUserFetchUtcBySerial = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
// Last time a full fetch-all was queued per device. The dashboard re-POSTs /fetch-all on its poll
// loop (and device-add + enable-registration auto-trigger more), each re-queuing the whole DATA QUERY
// batch and clearing pending commands — an endless re-queue loop that also wipes the pending
// AccUnLockComb before it can be delivered. One fetch-all per 60s already pulls everything.
var fetchAllUtcBySerial = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
// Last time an attendance-only fetch was auto-queued per NON-registration device (Yes/No = No).
// Registration devices (Yes) get a full fetch-all on add/enable; non-registration devices should
// still auto-pull attendance logs on connect so the dashboard/reports stay current. Throttled 60s.
var attLogFetchUtcBySerial = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
// Last time a clock sync was auto-queued per device. Previously the SHELL date-s/hwclock-w/SET
// OPTION DateTime trio only ran inside QueueFetchAllCommandsAsync (device add, manual "Sync Data",
// or toggling registration-device on) — a device that simply reboots and reconnects (its own
// options=all handshake) never got its clock re-synced, so its clock drifted after every restart.
// Queue the same trio on every options=all handshake instead, throttled so a device that hits this
// endpoint repeatedly in a short window doesn't get spammed with redundant SHELL commands.
var timeSyncUtcBySerial = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
// High-water mark for incremental ATTLOG pulls: the EndTime used by the previous DATA QUERY ATTLOG
// Devices that have already received one full employee biometric push. On a device's first connect
// every active employee is pushed (SyncedAt is the existing "needs (re)sync" marker — it's reset to
// null wherever an employee's photo/fingerprint/template changes); on every later connect only
// employees with SyncedAt == null are pushed, instead of re-sending everyone's full photo/fingerprint/
// face-template payload on every reconnect — the biggest driver of avoidable command-queue traffic.
var employeePushedOnceBySerial = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
var fullDataResetUtcBySerial = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
// Serials marked "clear all data on add": on the device's next connect we send CLEAR DATA and
// suppress the user/biometric real-time upload (so it does NOT fetch), then consume the flag.
var clearAllDataPendingBySerial = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
var manualUserSyncUntilUtcBySerial = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
var transactionDetailsQueuedRequests = 0L;
var transactionDetailsPersistedRequests = 0L;
var transactionDetailsPersistFailureCount = 0L;
var transactionDetailsLastPersistError = string.Empty;

var transactionDetailsPersistChannel = Channel.CreateUnbounded<TransactionDetailPersistRequest>(new UnboundedChannelOptions
{
    SingleReader = true,
    SingleWriter = false
});

_ = Task.Run(() => RunAppLogDbWriterAsync(postgresConnectionString, appLogPersistChannel.Reader, app.Lifetime.ApplicationStopping));
_ = Task.Run(() => RunTransactionDetailsDbWriterAsync(
    postgresConnectionString,
    transactionDetailsPersistChannel.Reader,
    app.Lifetime.ApplicationStopping,
    persistedCount =>
    {
        Interlocked.Add(ref transactionDetailsPersistedRequests, persistedCount);
        transactionDetailsLastPersistError = string.Empty;
    },
    errorMessage =>
    {
        Interlocked.Increment(ref transactionDetailsPersistFailureCount);
        transactionDetailsLastPersistError = errorMessage ?? "Unknown punch persistence error.";
    }));

void AddLog(string level, string source, string message, string? detail = null)
{
    var entry = new AppLogEntry(DateTime.UtcNow, level, source, message, detail);
    appLogs.Enqueue(entry);
    while (appLogs.Count > 2000)
    {
        appLogs.TryDequeue(out _);
    }

    _ = appLogPersistChannel.Writer.TryWrite(entry);
    AppEventLogger.Log(level, source, message, detail);
}

// Memory-only variant (never hits the DB) for high-frequency, zero-diagnostic-value entries —
// routine heartbeats (idle getrequest polls, ping heartbeats, the options=all handshake) that
// would otherwise flood the AppLogs table at scale (e.g. 100 devices polling every 15-30s each).
// Still shows up in the live in-memory view (capped at 2000 entries) for real-time "is this
// device still talking to us" visibility. Kept as a separate function (not an AddLog parameter)
// because AddLog's method group is passed around as a bare Action<string,string,string,string?>
// delegate elsewhere, which breaks if AddLog's signature changes.
void AddLogTransient(string level, string source, string message, string? detail = null)
{
    var entry = new AppLogEntry(DateTime.UtcNow, level, source, message, detail);
    appLogs.Enqueue(entry);
    while (appLogs.Count > 2000)
    {
        appLogs.TryDequeue(out _);
    }
}

var latestDeviceCardSwipes = new ConcurrentDictionary<string, (string CardNumber, DateTime TimestampUtc)>(StringComparer.OrdinalIgnoreCase);

void RecordPotentialCardSwipe(string? serialNumber, string payload)
{
    if (string.IsNullOrWhiteSpace(serialNumber) || string.IsNullOrWhiteSpace(payload))
    {
        return;
    }

    var lines = payload.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var line in lines)
    {
        if (line.StartsWith("OK", StringComparison.OrdinalIgnoreCase) || line.StartsWith("DATA ", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (TryParseAttendanceLine(line, out var empCode, out _, out _, out _, out _, out _))
        {
            if (!string.IsNullOrWhiteSpace(empCode))
            {
                latestDeviceCardSwipes[serialNumber.Trim()] = (empCode.Trim(), DateTime.UtcNow);
                AddLog("INFO", "CardEnrollment", $"Card swipe detected on {serialNumber.Trim()}: {empCode.Trim()}");
            }
        }
        else
        {
            var parts = line.Split('\t', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && !parts[0].StartsWith("OPLOG", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = parts[0].Trim();
                if (candidate.StartsWith("PIN=", StringComparison.OrdinalIgnoreCase))
                {
                    candidate = candidate[4..].Trim();
                }
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    latestDeviceCardSwipes[serialNumber.Trim()] = (candidate, DateTime.UtcNow);
                    AddLog("INFO", "CardEnrollment", $"Card swipe detected on {serialNumber.Trim()}: {candidate}");
                }
            }
            if (line.Contains("Card=", StringComparison.OrdinalIgnoreCase))
            {
                var map = ParseOptionPairs(line);
                var card = FirstValue(map, "Card", "CardNo", "CardNumber", "ViceCard");
                if (!string.IsNullOrWhiteSpace(card))
                {
                    latestDeviceCardSwipes[serialNumber.Trim()] = (card.Trim(), DateTime.UtcNow);
                    AddLog("INFO", "CardEnrollment", $"Card data detected on {serialNumber.Trim()}: {card.Trim()}");
                }
            }
        }
    }
}

void QueueTransactionDetails(string? serialNumber, string sourceTable, string payload)
{
    if (string.IsNullOrWhiteSpace(payload))
    {
        return;
    }

    RecordPotentialCardSwipe(serialNumber, payload);

    if (transactionDetailsPersistChannel.Writer.TryWrite(new TransactionDetailPersistRequest(
        serialNumber?.Trim(),
        sourceTable,
        payload)))
    {
        Interlocked.Increment(ref transactionDetailsQueuedRequests);
    }
}

object BuildLicenseStatusPayload(LicenseAccessSnapshot state)
{
    var validation = state.Validation;
    var licenseDaysRemaining = CalculateDaysRemaining(state.CheckedAtUtc, validation.ExpiryUtc);
    var licenseActivatedAtUtc = ResolveActivationTimestampUtc(validation.KeyPath);
    var currentDeviceCount = 0;
    try
    {
        currentDeviceCount = service.ListDevices().Count();
    }
    catch
    {
        // Database might not be ready during startup.
    }
    var effectiveMaxDevices = ResolveEffectiveMaxDevices(state);
    var effectiveLicenseType = state.IsTrialActive ? "Trial" : validation.LicenseType;
    return new
    {
        isValid = validation.IsValid,
        isAccessGranted = state.IsAccessGranted,
        activationMode = state.ActivationMode,
        activationLabel = state.ActivationLabel,
        activationMessage = state.ActivationMessage,
        code = validation.Code,
        message = state.ActivationMessage,
        validationMessage = validation.Message,
        checkedAtUtc = state.CheckedAtUtc,
        machineHash = validation.MachineHash,
        licenseMachineHash = validation.LicenseMachineHash,
        licenseActivatedAtUtc,
        expiryUtc = state.EffectiveExpiryUtc,
        licenseExpiryUtc = validation.ExpiryUtc,
        licenseDaysRemaining,
        trialExpiryUtc = state.TrialExpiryUtc,
        customerName = validation.CustomerName,
        licenseType = effectiveLicenseType,
        contactNumber = validation.ContactNumber,
        email = validation.Email,
        maxDevices = validation.MaxDevices,
        effectiveMaxDevices,
        currentDeviceCount,
        licenseTypeWithDeviceLimit = FormatLicenseTypeWithDeviceLimit(effectiveLicenseType, effectiveMaxDevices),
        trial = new
        {
            isActive = state.IsTrialActive,
            totalDays = state.TrialDaysTotal,
            startedUtc = state.TrialStartedUtc,
            expiryUtc = state.TrialExpiryUtc,
            daysRemaining = state.TrialDaysRemaining
        },
        files = new
        {
            licenseData = validation.LicenseDataPath,
            key = validation.KeyPath,
            publicKey = validation.PublicKeyPath
        },
        paths = new
        {
            licenseDirectory = state.LicenseDirectory,
            trialState = trialStatePath
        }
    };
}

static DateTimeOffset? ResolveActivationTimestampUtc(string? keyPath)
{
    if (string.IsNullOrWhiteSpace(keyPath))
    {
        return null;
    }

    try
    {
        if (File.Exists(keyPath))
        {
            return File.GetLastWriteTimeUtc(keyPath);
        }
    }
    catch
    {
        // Ignore filesystem timestamp read failures.
    }

    return null;
}

static int CalculateDaysRemaining(DateTimeOffset nowUtc, DateTimeOffset? expiryUtc)
{
    if (!expiryUtc.HasValue)
    {
        return 0;
    }

    var remaining = expiryUtc.Value - nowUtc;
    if (remaining <= TimeSpan.Zero)
    {
        return 0;
    }

    return Math.Max(1, (int)Math.Ceiling(remaining.TotalDays));
}

int ResolveEffectiveMaxDevices(LicenseAccessSnapshot state)
{
    if (state.Validation.IsValid)
    {
        if (state.Validation.MaxDevices.HasValue)
        {
            return state.Validation.MaxDevices.Value;
        }

        var mapped = ResolveMaxDevicesFromLicenseType(state.Validation.LicenseType);
        return mapped ?? TrialMaxDevices;
    }

    if (state.IsTrialActive)
    {
        return TrialMaxDevices;
    }

    return 0;
}

int? ResolveMaxDevicesFromLicenseType(string? licenseType)
{
    var normalized = (licenseType ?? string.Empty).Trim().ToLowerInvariant();
    return normalized switch
    {
        "basic" => 2,
        "premium" => 5,
        "enterprise" => 10,
        "unlimited" => -1,
        _ => null
    };
}

static string FormatLicenseTypeWithDeviceLimit(string? licenseType, int maxDevices)
{
    var typeLabel = string.IsNullOrWhiteSpace(licenseType) ? "Unknown" : licenseType.Trim();
    if (maxDevices < 0)
    {
        return $"{typeLabel} (Max: Unlimited devices)";
    }

    return $"{typeLabel} (Max: {maxDevices} devices)";
}

app.MapGet("/api/license/status", () =>
{
    lock (licenseRefreshSync)
    {
        return Results.Json(BuildLicenseStatusPayload(licenseState));
    }
});

app.MapGet("/api/license/machine", () => Results.Json(new
{
    machineHash = licenseGuard.GetMachineHash()
}));

app.MapPost("/api/license/reload", () =>
{
    lock (licenseRefreshSync)
    {
        licenseState = BuildLicenseAccessSnapshot(licenseGuard, trialStatePath, TrialDurationDays, licenseDirectory);
        lastLicenseRefreshUtc = licenseState.CheckedAtUtc;
    }

    AddLog(
        licenseState.IsAccessGranted ? "INFO" : "WARN",
        "License",
        licenseState.IsAccessGranted ? "License/trial reloaded and validated." : "License/trial access reload failed.",
        $"{licenseState.Validation.Code}: {licenseState.ActivationMessage}");

    return Results.Json(BuildLicenseStatusPayload(licenseState));
});

app.MapPost("/api/license/activate", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Use multipart/form-data and upload a .key activation file (licenseData optional for legacy mode)."
        });
    }

    IFormCollection form;
    try
    {
        form = await request.ReadFormAsync();
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, message = $"Unable to read upload form: {ex.Message}" });
    }

    var licenseDataFile = form.Files.GetFile("licenseData")
        ?? form.Files.GetFile("licenseDat")
        ?? form.Files.FirstOrDefault(static file =>
            string.Equals(Path.GetFileName(file.FileName), "license.dat", StringComparison.OrdinalIgnoreCase));
    var licenseKeyFile = form.Files.GetFile("licenseKey")
        ?? form.Files.GetFile("key")
        ?? form.Files.FirstOrDefault(static file =>
            string.Equals(Path.GetExtension(file.FileName), ".key", StringComparison.OrdinalIgnoreCase));

    if (licenseKeyFile is null)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = ".key file is required."
        });
    }

    try
    {
        Directory.CreateDirectory(licenseDirectory);
        if (licenseDataFile is not null)
        {
            var licenseDataPath = Path.Combine(licenseDirectory, "license.dat");
            await using var output = File.Create(licenseDataPath);
            await licenseDataFile.CopyToAsync(output);
        }

        foreach (var existingKey in Directory.EnumerateFiles(licenseDirectory, "*.key", SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(existingKey);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        var keyFileName = SanitizeLicenseKeyFileName(licenseKeyFile.FileName);
        var keyPath = Path.Combine(licenseDirectory, keyFileName);
        await using (var output = File.Create(keyPath))
        {
            await licenseKeyFile.CopyToAsync(output);
        }
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = $"Failed to save uploaded license files: {ex.Message}"
        });
    }

    lock (licenseRefreshSync)
    {
        licenseState = BuildLicenseAccessSnapshot(licenseGuard, trialStatePath, TrialDurationDays, licenseDirectory);
        lastLicenseRefreshUtc = licenseState.CheckedAtUtc;
    }

    AddLog(
        licenseState.Validation.IsValid ? "INFO" : "WARN",
        "License",
        licenseState.Validation.IsValid ? "License activated from uploaded files." : "Uploaded license failed validation.",
        $"{licenseState.Validation.Code}: {licenseState.ActivationMessage}");

    return Results.Json(new
    {
        success = licenseState.Validation.IsValid,
        message = licenseState.ActivationMessage,
        code = licenseState.Validation.Code,
        status = BuildLicenseStatusPayload(licenseState)
    });
});

app.MapPost("/api/license/deactivate", () =>
{
    var removed = new List<string>();
    try
    {
        Directory.CreateDirectory(licenseDirectory);
        var datPath = Path.Combine(licenseDirectory, "license.dat");
        if (File.Exists(datPath))
        {
            File.Delete(datPath);
            removed.Add(Path.GetFileName(datPath));
        }

        foreach (var keyPath in Directory.EnumerateFiles(licenseDirectory, "*.key", SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(keyPath);
                removed.Add(Path.GetFileName(keyPath));
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = $"Failed to deactivate license: {ex.Message}"
        });
    }

    lock (licenseRefreshSync)
    {
        licenseState = BuildLicenseAccessSnapshot(licenseGuard, trialStatePath, TrialDurationDays, licenseDirectory);
        lastLicenseRefreshUtc = licenseState.CheckedAtUtc;
    }

    AddLog(
        "WARN",
        "License",
        "License deactivated. Trial/lock mode applied.",
        $"removed={string.Join(", ", removed)}");

    return Results.Json(new
    {
        success = true,
        removedFiles = removed,
        status = BuildLicenseStatusPayload(licenseState)
    });
});

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (path.StartsWith("/iclock", StringComparison.OrdinalIgnoreCase))
    {
        if (!serverRunning)
        {
            serverRunning = true;
        }

        var entry = $"{DateTime.Now:HH:mm:ss} {context.Request.Method} {path} from {context.Connection.RemoteIpAddress}";
        requestLog.Enqueue(entry);
        while (requestLog.Count > 200)
        {
            requestLog.TryDequeue(out _);
        }

        Interlocked.Increment(ref requestCount);

        var sn = context.Request.Query.TryGetValue("SN", out var qsn) ? qsn.ToString() : context.Request.Headers["SN"].ToString();
        if (!string.IsNullOrWhiteSpace(sn))
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
            discoveredDevices.AddOrUpdate(
                sn.Trim(),
                _ => new DiscoveredDevice(sn.Trim(), sn.Trim(), string.Empty, ip, string.Empty, DateTime.UtcNow, true, 0, 0, 0, 0, -1),
                (_, old) => old with
                {
                    IpAddress = string.IsNullOrWhiteSpace(ip) ? old.IpAddress : ip,
                    LastActivityUtc = DateTime.UtcNow,
                    IsOnline = true
                });
        }
    }

    await next();
});

app.MapGet("/api/server/stats", () =>
{
    var recent = requestLog.Reverse().Take(30).ToArray();
    var pendingCommandCount = service.ListDevices()
        .Select(d => d.SerialNumber)
        .Where(sn => !string.IsNullOrWhiteSpace(sn))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Sum(sn => service.GetPendingCommands(sn, 2000).Count);
    return Results.Json(new
    {
        isRunning = serverRunning,
        requestCount = Interlocked.Read(ref requestCount),
        pendingCommandCount,
        configuredPort = settings.Port,
        configuredApiPort = settings.Port,
        configuredWebPort = settings.FrontendPort,
        listenUrls = BuildPublicListenUrls(settings.Port, settings.FrontendPort),
        recent
    });
});

// Public (no login required) — lets the login page, support staff, and the installer/updater all
// confirm which build is actually running without needing a session. Single source of truth is
// the assembly version baked in from E.Attendance.Web.csproj's <Version> at build time; the
// installer (.iss) reads that same compiled value via GetVersionNumbersString so there is exactly
// one place to bump the version instead of several hand-maintained copies drifting out of sync.
app.MapGet("/api/version", () =>
{
    var asmVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
    var version = asmVersion is null ? "0.0.0" : $"{asmVersion.Major}.{asmVersion.Minor}.{asmVersion.Build}";
    var buildUtc = System.IO.File.GetLastWriteTimeUtc(System.Reflection.Assembly.GetExecutingAssembly().Location);
    return Results.Json(new { version, buildUtc });
});

app.MapGet("/health", (IServiceProvider services) =>
{
    try
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PbAttendanceDbContext>();
        var ok = db.Database.CanConnect();
        return ok
            ? Results.Ok(new { status = "Healthy", database = "PostgreSQL", timestampUtc = DateTime.UtcNow })
            : Results.Problem("Database connection failed.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/services/status", (HttpContext context) =>
{
    if (!IsCallerAdmin(context))
    {
        return Results.Json(new { message = "Only Admin can view service status." }, statusCode: StatusCodes.Status403Forbidden);
    }

    return Results.Json(BuildEAttServicesSnapshot());
});

app.MapGet("/api/logs", (string? search, string? level, string? source, int? take, int? page, int? pageSize, string? from, string? to) =>
{
    // Server-side pagination mode
    if (page.HasValue || pageSize.HasValue)
    {
        var pgSize = Math.Min(Math.Max(pageSize.GetValueOrDefault(100), 1), 500);
        var pgNum = Math.Max(page.GetValueOrDefault(1), 1);
        var skip = (pgNum - 1) * pgSize;
        DateTime? fromUtc = DateOnly.TryParse(from, out var fd) ? fd.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime() : null;
        DateTime? toUtc = DateOnly.TryParse(to, out var td) ? td.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Local).ToUniversalTime() : null;
        var (dataLogs, totalLogs) = LoadAppLogsPaged(postgresConnectionString, search, level, source, fromUtc, toUtc, pgSize, skip);
        return Results.Json(new { data = dataLogs, total = totalLogs, page = pgNum, pageSize = pgSize });
    }

    var limit = take.GetValueOrDefault(200);
    if (limit is < 1 or > 2000)
    {
        limit = 200;
    }
    var logs = LoadAppLogs(postgresConnectionString, search, level, source, limit);

    IEnumerable<AppLogEntry> fallback = appLogs.Reverse();
    if (!string.IsNullOrWhiteSpace(search))
    {
        var token = search.Trim();
        fallback = fallback.Where(l =>
            l.Message.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(l.Detail) && l.Detail.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }
    if (!string.IsNullOrWhiteSpace(level))
    {
        fallback = fallback.Where(l => string.Equals(l.Level, level.Trim(), StringComparison.OrdinalIgnoreCase));
    }
    if (!string.IsNullOrWhiteSpace(source))
    {
        fallback = fallback.Where(l => string.Equals(l.Source, source.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    static string BuildDedupKey(AppLogEntry entry)
        => string.Join(
            '|',
            entry.TimestampUtc.ToString("O"),
            entry.Level ?? string.Empty,
            entry.Source ?? string.Empty,
            entry.Message ?? string.Empty,
            entry.Detail ?? string.Empty);

    var seen = new HashSet<string>(StringComparer.Ordinal);
    var merged = new List<AppLogEntry>(limit);
    foreach (var item in logs.Concat(fallback).OrderByDescending(l => l.TimestampUtc))
    {
        if (!seen.Add(BuildDedupKey(item)))
        {
            continue;
        }

        merged.Add(item);
        if (merged.Count >= limit)
        {
            break;
        }
    }

    return Results.Json(merged);
});

app.MapDelete("/api/logs", () =>
{
    while (appLogs.TryDequeue(out _))
    {
    }

    ClearAppLogs(postgresConnectionString);

    return Results.Ok(new { success = true });
});

app.MapGet("/api/logs/export", (string? search, string? level, string? source) =>
{
    var logs = LoadAppLogs(postgresConnectionString, search, level, source, 2000);

    static string Esc(string? value)
    {
        var s = value ?? string.Empty;
        if (s.Contains('"'))
        {
            s = s.Replace("\"", "\"\"");
        }

        return (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            ? $"\"{s}\""
            : s;
    }

    var csv = new StringBuilder();
    csv.AppendLine("TimestampUtc,Level,Source,Message,Detail");
    foreach (var item in logs)
    {
        csv.Append(item.TimestampUtc.ToString("O")).Append(',')
           .Append(Esc(item.Level)).Append(',')
           .Append(Esc(item.Source)).Append(',')
           .Append(Esc(item.Message)).Append(',')
           .Append(Esc(item.Detail))
           .AppendLine();
    }

    return Results.Text(csv.ToString(), "text/csv");
});

app.MapPost("/api/server/start", () =>
{
    if (serverRunning)
    {
        return Results.Ok(new { isRunning = true, changed = false });
    }

    serverRunning = true;
    AddLog("INFO", "Server", "Push server started.");
    return Results.Ok(new { isRunning = true, changed = true });
});

app.MapPost("/api/server/stop", () =>
{
    if (!serverRunning)
    {
        return Results.Ok(new { isRunning = false, changed = false });
    }

    serverRunning = false;
    AddLog("INFO", "Server", "Push server stopped.");
    return Results.Ok(new { isRunning = false, changed = true });
});

app.MapPost("/api/server/restart", () =>
{
    var wasRunning = serverRunning;
    serverRunning = true;
    AddLog("INFO", "Server", wasRunning ? "Push server restarted." : "Push server started.");
    return Results.Ok(new { isRunning = true, changed = true, restarted = wasRunning });
});

app.MapGet("/api/settings", () => Results.Json(new
{
    settings.Port,
    settings.FrontendPort,
    serverIp = ResolvePreferredServerIp()
}));

app.MapPut("/api/settings/port", async (HttpRequest request) =>
{
    if (serverRunning)
    {
        return Results.BadRequest(new { message = "Stop server before changing port." });
    }

    var payload = await request.ReadFromJsonAsync<UpdatePortRequest>();
    if (payload is null || payload.Port is < 1 or > 65535)
    {
        return Results.BadRequest(new { message = "Port must be between 1 and 65535." });
    }

    var newFrontendPort = payload.FrontendPort ?? settings.FrontendPort;
    var changed = payload.Port != settings.Port || newFrontendPort != settings.FrontendPort;
    if (!changed)
    {
        return Results.Ok(new
        {
            message = "Port is unchanged.",
            restartRequired = false,
            port = settings.Port,
            frontendPort = settings.FrontendPort
        });
    }

    settings = settings with { Port = payload.Port, FrontendPort = newFrontendPort };
    settings.Save(settingsPath);

    return Results.Ok(new
    {
        message = $"Port updated to {settings.Port}, web port {settings.FrontendPort}. Restart application to apply.",
        restartRequired = true,
        port = settings.Port,
        frontendPort = settings.FrontendPort
    });
});

app.MapGet("/api/app-config/settings", () =>
{
    var json = ReadAppConfigJson(postgresConnectionString, "settings");
    return Results.Content(json, "application/json");
});

app.MapPut("/api/app-config/settings", async (HttpRequest request) =>
{
    try
    {
        using var document = await JsonDocument.ParseAsync(request.Body);
        var rawJson = document.RootElement.GetRawText();
        SaveAppConfigJson(postgresConnectionString, "settings", rawJson);
        return Results.Content(rawJson, "application/json");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"Failed to save settings: {ex.Message}" });
    }
});

app.MapGet("/api/app-config/shifts", () =>
{
    var json = ReadAppConfigJson(postgresConnectionString, "shifts");
    return Results.Content(json, "application/json");
});

app.MapPut("/api/app-config/shifts", async (HttpRequest request, ConfigSyncService syncService) =>
{
    try
    {
        using var document = await JsonDocument.ParseAsync(request.Body);
        var rawJson = document.RootElement.GetRawText();
        SaveAppConfigJson(postgresConnectionString, "shifts", rawJson);
        await syncService.SyncShiftsAsync(rawJson);
        return Results.Content(rawJson, "application/json");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"Failed to save shift configuration: {ex.Message}" });
    }
});

app.MapGet("/api/app-config/access-control", async Task<IResult> (ConfigSyncService syncService) =>
{
    lock (licenseRefreshSync)
    {
        if (!licenseState.Validation.IsValid)
        {
            return Results.Json(new { error = "access_control_locked", message = "Access Control requires a valid activated license and is not available in trial mode." }, statusCode: StatusCodes.Status423Locked);
        }
    }
    await EnsureAccessControlDefaultsForAllAreas(postgresConnectionString, service, personnelStore, syncService, syncWhenUnchangedWithDevices: false);
    var json = ReadAppConfigJson(postgresConnectionString, "access-control");
    return Results.Content(json, "application/json");
});

app.MapPut("/api/app-config/access-control", async Task<IResult> (HttpRequest request, ConfigSyncService syncService) =>
{
    lock (licenseRefreshSync)
    {
        if (!licenseState.Validation.IsValid)
        {
            return Results.Json(new { error = "access_control_locked", message = "Access Control requires a valid activated license and is not available in trial mode." }, statusCode: StatusCodes.Status423Locked);
        }
    }
    try
    {
        using var document = await JsonDocument.ParseAsync(request.Body);
        var rawJson = document.RootElement.GetRawText();
        SaveAppConfigJson(postgresConnectionString, "access-control", rawJson);
        await EnsureAccessControlDefaultsForAllAreas(
            postgresConnectionString,
            service,
            personnelStore,
            syncService,
            syncWhenUnchangedWithDevices: false,
            syncWhenChanged: false);
        var repairedJson = ReadAppConfigJson(postgresConnectionString, "access-control");
        await syncService.SyncAccessControlAsync(repairedJson);
        if (ApplyAccessControlPrivilegesToEmployees(repairedJson, repository, personnelStore))
        {
            SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
        }
        return Results.Content(repairedJson, "application/json");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"Failed to save access control configuration: {ex.Message}" });
    }
});

app.MapPost("/api/auth/login", async (HttpRequest request) =>
{
    var payload = await request.ReadFromJsonAsync<AuthLoginRequest>();
    var userName = (payload?.Username ?? string.Empty).Trim();
    var password = (payload?.Password ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
    {
        return Results.BadRequest(new { message = "Username and password are required." });
    }

    try
    {
        using var conn = new NpgsqlConnection(postgresConnectionString);
        conn.Open();
        // Self-heal: if the background schema bootstrap hasn't created auth_users yet (fresh
        // install / service started before PostgreSQL was ready), create it now so login works.
        if (!AuthUsersTableExists(conn))
        {
            EnsureAuthUsersTable(conn);
        }
        EnsureAuthLoginAttemptTable(conn);

        var nowUtc = DateTimeOffset.UtcNow;
        var loginAttemptState = LoadAuthLoginAttemptState(conn, userName);
        if (loginAttemptState.LockedUntilUtc is DateTimeOffset lockedUntilUtc &&
            lockedUntilUtc > nowUtc)
        {
            return Results.Json(
                new { message = BuildAuthLoginLockedMessage(lockedUntilUtc) },
                statusCode: StatusCodes.Status423Locked);
        }

        using var cmd = new NpgsqlCommand(
            """
            SELECT
                "Id",
                "UserName",
                COALESCE(NULLIF(TRIM("FullName"), ''), TRIM("UserName")) AS "FullName",
                COALESCE(NULLIF(TRIM("Role"), ''), 'Operator') AS "Role",
                COALESCE(NULLIF(TRIM("Status"), ''), 'active') AS "Status",
                COALESCE("FeatureAccessJson", '{}'::jsonb)::text AS "FeatureAccessJson",
                COALESCE("PasswordHash", '') AS "PasswordHash",
                "CreatedAtUtc",
                "UpdatedAtUtc"
            FROM "E_Attendace"."auth_users"
            WHERE LOWER(TRIM("UserName")) = LOWER(@userName)
            LIMIT 1;
            """,
            conn);
        cmd.Parameters.AddWithValue("userName", userName);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            reader.Close();
            var failedAttempt = RegisterFailedAuthLoginAttempt(conn, userName, nowUtc);
            return BuildAuthLoginFailedResult(failedAttempt);
        }

        var passwordHash = (reader["PasswordHash"]?.ToString() ?? string.Empty).Trim();
        if (!string.Equals(passwordHash, NormalizePasswordHash(password), StringComparison.OrdinalIgnoreCase))
        {
            reader.Close();
            var failedAttempt = RegisterFailedAuthLoginAttempt(conn, userName, nowUtc);
            return BuildAuthLoginFailedResult(failedAttempt);
        }

        var id = Convert.ToString(reader["Id"], CultureInfo.InvariantCulture) ?? string.Empty;
        var storedUserName = (reader["UserName"]?.ToString() ?? string.Empty).Trim();
        var fullName = (reader["FullName"]?.ToString() ?? storedUserName).Trim();
        var role = reader["Role"]?.ToString();
        var featureAccessJson = reader["FeatureAccessJson"]?.ToString();
        var createdAtUtc = ToIsoUtcString(reader["CreatedAtUtc"]);
        var updatedAtUtc = ToIsoUtcString(reader["UpdatedAtUtc"]);
        var isAdmin = string.Equals(storedUserName, "admin", StringComparison.OrdinalIgnoreCase);
        var isActive = !string.Equals(reader["Status"]?.ToString(), "inactive", StringComparison.OrdinalIgnoreCase);
        reader.Close();

        if (!isActive)
        {
            return Results.Json(new { message = "This user is inactive." }, statusCode: StatusCodes.Status403Forbidden);
        }

        ResetAuthLoginAttempts(conn, userName);

        var normalizedRole = NormalizeAuthRole(role, isAdmin);
        var token = GenerateJwtToken(jwtSigningKey, JwtIssuer, JwtAudience, id, storedUserName, normalizedRole, TimeSpan.FromHours(12));

        var response = new AuthUserResponse(
            id,
            storedUserName,
            fullName,
            string.Empty,
            normalizedRole,
            true,
            isAdmin
                ? CreateSoftwareFeatureAccess(true)
                : DeserializeSoftwareFeatureAccess(featureAccessJson),
            createdAtUtc,
            updatedAtUtc,
            token);

        return Results.Json(response);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"Login failed: {ex.Message}" });
    }
});

app.MapGet("/api/auth/users", (HttpContext context) =>
{
    if (!IsCallerAdmin(context))
    {
        return Results.Json(new { message = "Only Admin can view software users." }, statusCode: StatusCodes.Status403Forbidden);
    }
    try
    {
        return Results.Json(LoadAuthUsersFromDb(postgresConnectionString));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"Failed to load software users: {ex.Message}" });
    }
});

app.MapPut("/api/auth/users", async (HttpContext context, HttpRequest request) =>
{
    if (!IsCallerAdmin(context))
    {
        return Results.Json(new { message = "Only Admin can create, edit, or delete software users." }, statusCode: StatusCodes.Status403Forbidden);
    }
    var payload = await request.ReadFromJsonAsync<List<AuthUserSaveRequest>>();
    if (payload is null)
    {
        return Results.BadRequest(new { message = "Invalid user payload." });
    }

    try
    {
        var savedUsers = ReplaceAuthUsersInDb(postgresConnectionString, payload);
        return Results.Json(savedUsers);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"Failed to save software users: {ex.Message}" });
    }
});

app.MapGet("/api/auth/session", (string? userId, string? username) =>
{
    try
    {
        var user = LoadAuthUserByIdentity(postgresConnectionString, userId, username);
        return user is null
            ? Results.NotFound(new { message = "User not found." })
            : Results.Json(user);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"Failed to load user session: {ex.Message}" });
    }
});

app.MapGet("/api/backup/settings", (HttpContext context) =>
{
    if (!IsCallerAdmin(context))
    {
        return Results.Json(new { message = "Only Admin can view backup settings." }, statusCode: StatusCodes.Status403Forbidden);
    }
    try
    {
        return Results.Json(LoadBackupScheduleFromDb(postgresConnectionString));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"Failed to load backup settings: {ex.Message}" });
    }
});

app.MapPut("/api/backup/settings", async (HttpContext context, HttpRequest request) =>
{
    if (!IsCallerAdmin(context))
    {
        return Results.Json(new { message = "Only Admin can change backup settings." }, statusCode: StatusCodes.Status403Forbidden);
    }
    var payload = await request.ReadFromJsonAsync<BackupScheduleSaveRequest>();
    if (payload is null)
    {
        return Results.BadRequest(new { message = "Invalid payload." });
    }
    try
    {
        var saved = SaveBackupScheduleToDb(postgresConnectionString, payload.Enabled, payload.Frequency, payload.RunTime, payload.BackupLocation, payload.RetentionCount);
        return Results.Json(saved);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"Failed to save backup settings: {ex.Message}" });
    }
});

app.MapGet("/api/backup/history", (HttpContext context, int? page, int? pageSize) =>
{
    if (!IsCallerAdmin(context))
    {
        return Results.Json(new { message = "Only Admin can view backup history." }, statusCode: StatusCodes.Status403Forbidden);
    }
    try
    {
        var (items, total) = LoadBackupHistoryFromDb(postgresConnectionString, page ?? 1, pageSize ?? 20);
        return Results.Json(new { data = items, total });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"Failed to load backup history: {ex.Message}" });
    }
});

app.MapPost("/api/backup/run", async (HttpContext context) =>
{
    if (!IsCallerAdmin(context))
    {
        return Results.Json(new { message = "Only Admin can run a backup." }, statusCode: StatusCodes.Status403Forbidden);
    }
    try
    {
        // A full dump is typically seconds to a couple of minutes even for large installs, so this
        // runs synchronously and returns the finished row — no polling/fire-and-forget needed.
        var entry = await RunAndRecordBackupAsync(postgresConnectionString, "manual", AddLog);
        return Results.Json(entry);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"Backup failed: {ex.Message}" });
    }
});

app.MapGet("/api/backup/download/{id}", (HttpContext context, string id) =>
{
    if (!IsCallerAdmin(context))
    {
        return Results.Json(new { message = "Only Admin can download backups." }, statusCode: StatusCodes.Status403Forbidden);
    }
    try
    {
        var filePath = LoadBackupFilePathById(postgresConnectionString, id);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return Results.NotFound(new { message = "Backup file not found." });
        }
        var stream = System.IO.File.OpenRead(filePath);
        return Results.File(stream, "application/octet-stream", Path.GetFileName(filePath));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"Failed to download backup: {ex.Message}" });
    }
});

app.MapGet("/api/employees", () =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var items = service.ListEmployees().Select(e => new
    {
        e.Id,
        e.EmployeeCode,
        e.FullName,
        e.Department,
        e.IsActive
    });
    return Results.Json(items);
});

app.MapPost("/api/employees/{id:guid}/department", async (Guid id, HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<EmployeeDepartmentRequest>();
    if (payload is null || string.IsNullOrWhiteSpace(payload.Department))
    {
        return Results.BadRequest(new { message = "Department is required." });
    }

    var employee = repository.GetEmployees().FirstOrDefault(e => e.Id == id);
    if (employee is null)
    {
        return Results.NotFound(new { message = "Employee not found." });
    }

    var resolvedAssignments = ResolveEmployeeAssignmentsFromInputs(
        personnelStore,
        [payload.Department],
        DefaultAreaCode,
        DefaultAreaName,
        DefaultDepartmentCode,
        DefaultDepartmentName);
    var updated = ApplyEmployeeAssignments(
        repository,
        personnelStore,
        employee,
        resolvedAssignments,
        DefaultAreaCode,
        DefaultAreaName,
        DefaultDepartmentCode,
        DefaultDepartmentName);
    if (updated)
    {
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    }
    return updated
        ? Results.Ok(new { success = true })
        : Results.NotFound(new { message = "Employee not found." });
});

app.MapPost("/api/employees", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<EmployeeCreateRequest>();
    if (payload is null)
    {
        return Results.BadRequest(new { message = "Invalid payload." });
    }
    var passwordError = ValidateEmployeePassword(payload.Password, required: true);
    if (passwordError is not null)
    {
        return Results.BadRequest(new { message = passwordError });
    }

    try
    {
        var normalizedPasswordInput = NormalizeEmployeePassword(payload.Password);
        var employee = service.CreateEmployee(payload.EmployeeCode ?? string.Empty, payload.FullName ?? string.Empty, payload.Department ?? string.Empty);
        employee.Password = NormalizePasswordHash(normalizedPasswordInput);
        employee.VerifyMode = ProgramHelpers.NormalizeEmployeeVerifyMode(payload.VerifyMode);
        repository.UpdateEmployee(employee);
        ProgramHelpers.UpsertEmployeeProfile(
            personnelStore,
            employee.EmployeeCode,
            null,
            null,
            null,
            null,
            null,
            null,
            normalizedPasswordInput,
            verifyMode: employee.VerifyMode);
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
        return Results.Json(employee);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/employees/{id:guid}/deactivate", (Guid id) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var ok = service.DeactivateEmployee(id);
    return ok ? Results.Ok(new { success = true }) : Results.NotFound(new { success = false });
});

app.MapGet("/api/personnel/employees", (string? search, string? status, int page = 1, int pageSize = 200) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var authUserNames = LoadAuthUserNamesFromDb(postgresConnectionString);
    // Do not auto-backfill from local profile cache on read; this can resurrect stale rows.
    NormalizeStoredPasswordsToMd5(
        repository,
        personnelStore,
        personnelPath,
        postgresConnectionString);
    NormalizeEmployeeDepartmentsToSetup(
        repository,
        personnelStore,
        DefaultAreaCode,
        DefaultAreaName,
        DefaultDepartmentCode,
        DefaultDepartmentName);
    NormalizeEmployeePositionsToSetup(
        repository,
        personnelStore);
    NormalizeEmployeePositionsToSetup(
        repository,
        personnelStore);
    // Do not auto-backfill from unified-user cache on read; only explicit sync should create employees.

    var query = service.ListEmployees().AsEnumerable();
    if (authUserNames.Count > 0)
    {
        query = query.Where(e =>
        {
            var code = e.EmployeeCode?.Trim();
            return string.IsNullOrWhiteSpace(code) || !authUserNames.Contains(code);
        });
    }
    if (!string.IsNullOrWhiteSpace(search))
    {
        var token = search.Trim();
        query = query.Where(e =>
            (e.EmployeeCode ?? string.Empty).Contains(token, StringComparison.OrdinalIgnoreCase) ||
            (e.FullName ?? string.Empty).Contains(token, StringComparison.OrdinalIgnoreCase) ||
            (e.Department ?? string.Empty).Contains(token, StringComparison.OrdinalIgnoreCase) ||
            (e.Position ?? string.Empty).Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    if (!string.IsNullOrWhiteSpace(status))
    {
        var s = status.Trim().ToLowerInvariant();
        if (s == "active")
        {
            query = query.Where(e => e.IsActive);
        }
        else if (s == "inactive")
        {
            query = query.Where(e => !e.IsActive);
        }
    }

    var allRows = query.OrderBy(e => e.EmployeeCode, StringComparer.OrdinalIgnoreCase).ToList();
    var totalCount = allRows.Count;
    var rows = allRows.Skip((page - 1) * pageSize).Take(pageSize).ToList();

    var pins = rows
        .Select(e => e.EmployeeCode?.Trim())
        .Where(static pin => !string.IsNullOrWhiteSpace(pin))
        .Cast<string>()
        .ToList();
    var unifiedByPin = repository.GetUnifiedUsersByPins(pins);
    var templateByPin = repository.GetBiometricTemplatesByPins(pins);
    var photoByPin = repository.GetBiometricPhotosByPins(pins);

    var items = rows.Select(e =>
    {
        var profile = personnelStore.Profiles.FirstOrDefault(p => string.Equals(p.EmployeeCode, e.EmployeeCode, StringComparison.OrdinalIgnoreCase));
        var pin = e.EmployeeCode?.Trim() ?? string.Empty;
        unifiedByPin.TryGetValue(pin, out var unified);
        templateByPin.TryGetValue(pin, out var templates);
        photoByPin.TryGetValue(pin, out var photos);
        templates ??= [];
        photos ??= [];

        var visibleTemplate = !string.IsNullOrWhiteSpace(e.FaceTemplateVisible) && !IsLikelyPhotoPayload(e.FaceTemplateVisible)
            ? e.FaceTemplateVisible
            : templates.FirstOrDefault(t => t.Type == 9)?.TemplateData;
        var infraredTemplate = !string.IsNullOrWhiteSpace(e.FaceTemplateInfrared) && !IsLikelyPhotoPayload(e.FaceTemplateInfrared)
            ? e.FaceTemplateInfrared
            : templates.FirstOrDefault(t => t.Type == 2)?.TemplateData;
        var fingerprintRows = templates
            .Where(t => t.Type != 9 && t.Type != 2 && !string.IsNullOrWhiteSpace(t.TemplateData))
            .ToList();

        var photoContent = string.IsNullOrWhiteSpace(e.ProfilePhoto)
            ? (photos.FirstOrDefault(p => p.Type == 9)?.Content ?? profile?.PictureDataUrl ?? string.Empty)
            : e.ProfilePhoto;
        var normalizedPhoto = string.IsNullOrWhiteSpace(photoContent)
            ? string.Empty
            : NormalizeBiometricPhotoContent(photoContent);

        var effectivePrivilegeRaw = unified?.Privilege ?? (int)e.Privilege;
        var effectivePassword = ResolveUiPassword(profile?.Password);
        var effectiveCard = !string.IsNullOrWhiteSpace(e.CardNumber) ? e.CardNumber : (unified?.PrimaryCard ?? profile?.CardNumber ?? string.Empty);
        var effectiveMobile = !string.IsNullOrWhiteSpace(e.MobileNumber) ? e.MobileNumber : (unified?.Phone ?? profile?.MobileNumber ?? string.Empty);
        var effectiveGender = !string.IsNullOrWhiteSpace(e.Gender) ? e.Gender : NormalizeGenderToken(unified?.Gender, profile?.Gender);
        var effectiveFingerprintTemplates = !string.IsNullOrWhiteSpace(e.FingerprintTemplates)
            ? e.FingerprintTemplates
            : BuildFingerprintTemplateJson(fingerprintRows);
        var effectiveVerifyMode = ProgramHelpers.NormalizeEmployeeVerifyMode(
            profile?.VerifyMode,
            ProgramHelpers.NormalizeEmployeeVerifyMode(unified?.VerifyMode, e.VerifyMode));
        var assignmentDetails = GetResolvedEmployeeAssignments(
            personnelStore,
            e,
            profile,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName);
        var primaryAssignment = assignmentDetails[0];
        var departmentNames = assignmentDetails
            .Select(x => x.Department.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var departmentCodes = assignmentDetails
            .Select(x => x.Department.Code)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var areaNames = assignmentDetails
            .Select(x => x.Area.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var areaCodes = assignmentDetails
            .Select(x => x.Area.Code)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new
        {
            e.Id,
            userId = pin,
            e.EmployeeCode,
            e.FullName,
            department = primaryAssignment.Department.Name,
            departments = departmentNames,
            position = string.IsNullOrWhiteSpace(e.Position) ? (profile?.Position ?? string.Empty) : e.Position,
            areaCode = primaryAssignment.Area.Code,
            areaCodes,
            areas = areaNames,
            departmentCode = primaryAssignment.Department.Code,
            departmentCodes,
            assignments = assignmentDetails.Select(x => new
            {
                areaCode = x.Area.Code,
                areaName = x.Area.Name,
                departmentCode = x.Department.Code,
                departmentName = x.Department.Name
            }),
            e.IsActive,
            privilege = DescribePrivilegeLabel(effectivePrivilegeRaw),
            privilegeRaw = effectivePrivilegeRaw,
            cardNumber = effectiveCard,
            password = effectivePassword,
            mobileNumber = effectiveMobile,
            gender = effectiveGender,
            hireDate = profile?.HireDate ?? string.Empty,
            pictureDataUrl = normalizedPhoto,
            faceTemplateVisible = visibleTemplate ?? string.Empty,
            faceTemplateInfrared = infraredTemplate ?? string.Empty,
            fingerprintTemplates = effectiveFingerprintTemplates ?? string.Empty,
            fingerprintCount = fingerprintRows.Count,
            faceCount = templates.Count(t => t.Type == 9 || t.Type == 2),
            photoCount = photos.Count,
            verifyMode = effectiveVerifyMode,
            groupId = !e.IsActive ? 0 : (unified?.GroupId ?? 1),
            timezoneProfile = unified?.TimezoneProfile ?? string.Empty,
            validFrom = unified?.ValidFrom,
            validTo = unified?.ValidTo,
            expiresEnabled = unified?.ExpiresEnabled,
            idNumber = unified?.IdNumber ?? string.Empty,
            secondaryCard = unified?.SecondaryCard ?? string.Empty,
            biometricTemplates = templates.Select(t => new
            {
                type = t.Type,
                no = t.No,
                idx = t.Index,
                valid = t.Valid,
                duress = t.Duress,
                majorVersion = t.MajorVersion,
                minorVersion = t.MinorVersion,
                format = t.Format,
                templateData = t.TemplateData,
                updatedAt = t.UpdatedAt
            }),
            biometricPhotos = photos.Select(p => new
            {
                type = p.Type,
                no = p.No,
                idx = p.Index,
                format = p.Format,
                content = NormalizeBiometricPhotoContent(p.Content),
                updatedAt = p.UpdatedAt
            })
        };
    });

    return Results.Ok(new { totalCount, page, pageSize, items });
});

app.MapPost("/api/personnel/employees", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<EmployeeCreateRequest>();
    if (payload is null)
    {
        return Results.BadRequest(new { message = "Invalid payload." });
    }

    try
    {
        var passwordError = ValidateEmployeePassword(payload.Password, required: false);
        if (passwordError is not null)
        {
            return Results.BadRequest(new { message = passwordError });
        }

        var normalizedEmployeeCode = NormalizeEmployeeCode(payload.EmployeeCode);
        var normalizedCardNumber = NormalizeCardNumber(payload.CardNumber);
        var normalizedPhotoInput = payload.PictureDataUrl?.Trim() ?? string.Empty;
        var normalizedPhotoForCompare = NormalizePictureDataUrlForComparison(normalizedPhotoInput);
        var normalizedPasswordInput = NormalizeEmployeePassword(payload.Password);
        var normalizedFullName = string.IsNullOrWhiteSpace(payload.FullName)
            ? normalizedEmployeeCode
            : payload.FullName.Trim();
        var effectivePasswordInput = string.IsNullOrWhiteSpace(normalizedPasswordInput)
            ? normalizedEmployeeCode
            : normalizedPasswordInput;
        var employees = repository.GetEmployees().ToList();
        var unifiedUsers = string.IsNullOrWhiteSpace(normalizedCardNumber)
            ? Array.Empty<UnifiedUserSnapshot>()
            : repository.GetAllUnifiedUsers();
        var duplicateMessage = ValidateEmployeeUniqueness(
            employees,
            personnelStore,
            unifiedUsers,
            null,
            normalizedEmployeeCode,
            normalizedCardNumber,
            normalizedPhotoForCompare);
        if (duplicateMessage is not null)
        {
            return Results.BadRequest(new { message = duplicateMessage });
        }

        var resolvedAssignments = ResolveEmployeeAssignmentsFromInputs(
            personnelStore,
            payload.DepartmentCodes?.Cast<string?>() ?? [payload.Department],
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName);
        var primaryDepartment = ResolveDepartmentFromSetup(
            personnelStore,
            resolvedAssignments[0].DepartmentCode,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName);
        var normalizedDepartment = primaryDepartment.Name;
        var normalizedPosition = ResolvePositionNameFromSetup(
            personnelStore,
            payload.Position,
            normalizedDepartment);
        var employee = service.CreateEmployee(normalizedEmployeeCode, normalizedFullName, normalizedDepartment);
        employee.Position = normalizedPosition;
        employee.CardNumber = normalizedCardNumber;
        employee.Password = NormalizePasswordHash(effectivePasswordInput);
        employee.MobileNumber = payload.MobileNumber?.Trim() ?? string.Empty;
        employee.Gender = payload.Gender?.Trim() ?? string.Empty;
        employee.Privilege = ResolveConfiguredEmployeePrivilege(payload.Privilege);
        employee.VerifyMode = ProgramHelpers.NormalizeEmployeeVerifyMode(payload.VerifyMode);
        employee.ProfilePhoto = string.IsNullOrWhiteSpace(normalizedPhotoInput) ? null : normalizedPhotoInput;
        var effectiveGroupId = (payload.IsActive.HasValue && !payload.IsActive.Value) ? 0 : (payload.GroupId ?? 1);
        employee.SyncedAt = null;
        repository.UpdateEmployee(employee);
        ProgramHelpers.UpsertEmployeeProfile(
            personnelStore,
            employee.EmployeeCode,
            payload.Privilege,
            normalizedCardNumber,
            normalizedPhotoInput,
            payload.MobileNumber,
            payload.Gender,
            payload.HireDate,
            effectivePasswordInput,
            normalizedPosition,
            resolvedAssignments[0].AreaCode,
            resolvedAssignments[0].DepartmentCode,
            groupId: effectiveGroupId,
            timezoneProfile: effectiveGroupId == 0 ? "0000000000000000" : payload.TimezoneProfile,
            verifyMode: employee.VerifyMode,
            assignments: resolvedAssignments);
        // Also persist groupId to unified user table — must run BEFORE UpsertProfilePhotoSnapshot
        // because biometric_photos has a FK on users(pin) and the photo snapshot would fail with
        // a 23503 FK-violation if the user row does not exist yet.
        repository.UpsertUnifiedUser(
            pin: employee.EmployeeCode, name: employee.FullName, privilege: (int)employee.Privilege,
            password: null, primaryCard: normalizedCardNumber, secondaryCard: null,
            groupId: effectiveGroupId, timezoneProfile: effectiveGroupId == 0 ? "0000000000000000" : payload.TimezoneProfile, verifyMode: employee.VerifyMode,
            validFrom: null, validTo: null, expiresEnabled: effectiveGroupId == 0, phone: employee.MobileNumber,
            gender: null, nation: null, idNumber: null);
        UpsertProfilePhotoSnapshot(repository, employee);
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);

        // Sync new employee to all assigned devices immediately, then wake those devices so
        // they pick up the change in near-real-time instead of waiting for their next poll.
        // Wrapped in try/catch: the employee is already saved, so a transient sync/queue error
        // must NOT turn a successful create into an error response (which would otherwise cause
        // the client to retry and report a false "already exists").
        if (employee.IsActive)
        {
            try
            {
                var newProfile = personnelStore.Profiles.FirstOrDefault(p => string.Equals(p.EmployeeCode, employee.EmployeeCode, StringComparison.OrdinalIgnoreCase));
                var devicesToWake = new List<Device>();
                foreach (var device in service.ListDevices())
                {
                    var devSetup = ResolveDeviceSetupForSerial(personnelStore, device.SerialNumber, device.Location, DefaultAreaCode, DefaultAreaName, DefaultDepartmentCode, DefaultDepartmentName);
                    var devDept = ResolveEmployeeDepartmentForDevice(personnelStore, employee, newProfile, devSetup.Department, DefaultAreaCode, DefaultAreaName, DefaultDepartmentCode, DefaultDepartmentName);
                    if (string.IsNullOrWhiteSpace(devDept)) continue;
                    var syncEmp = CreateEmployeeForDeviceSync(employee, newProfile, devDept);
                    syncEmp.DepartmentId = effectiveGroupId;
                    syncEmp.VerifyMode = employee.VerifyMode;
                    service.QueueDeviceCommand(device.SerialNumber, protocolService.CreateUserInfoCommand(syncEmp, device));
                    devicesToWake.Add(device);
                }

                var queuedPhotoCommands = QueueEmployeePhotoSyncToAssignedDevices(
                    service,
                    protocolService,
                    personnelStore,
                    [employee],
                    DefaultAreaCode,
                    DefaultAreaName,
                    DefaultDepartmentCode,
                    DefaultDepartmentName);
                if (queuedPhotoCommands > 0)
                {
                    AddLog("INFO", "Sync", $"Queued {queuedPhotoCommands} photo sync command(s) for employee {employee.EmployeeCode}.");
                }

                foreach (var device in devicesToWake)
                {
                    _ = TryWakeDeviceAsync(device.IpAddress, device.SerialNumber, settings.Port);
                }
            }
            catch (Exception syncEx)
            {
                AddLog("WARN", "Sync", $"Employee {employee.EmployeeCode} saved, but real-time device sync hit an error.", syncEx.ToString());
            }
        }
        return Results.Json(employee);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPut("/api/personnel/employees/{id:guid}", async (Guid id, HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<EmployeeUpdateRequest>();
    if (payload is null || string.IsNullOrWhiteSpace(payload.EmployeeCode))
    {
        return Results.BadRequest(new { message = "Employee code is required." });
    }

    var employees = repository.GetEmployees().ToList();
    var employee = employees.FirstOrDefault(e => e.Id == id);
    if (employee is null)
    {
        return Results.NotFound(new { message = "Employee not found." });
    }

    // Capture the employee's area(s) BEFORE any field on `employee` is mutated below, so that if
    // this edit moves them to a different area we can tell which devices they need to be removed
    // from (previously in scope, no longer assigned) vs. which they're newly pushed to.
    var previousProfileForAreaDiff = personnelStore.Profiles.FirstOrDefault(p =>
        string.Equals(p.EmployeeCode, employee.EmployeeCode, StringComparison.OrdinalIgnoreCase));
    var previousAreaCodes = GetResolvedEmployeeAssignments(
            personnelStore,
            employee,
            previousProfileForAreaDiff,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName)
        .Select(a => a.Area.Code)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var normalizedEmployeeCode = NormalizeEmployeeCode(payload.EmployeeCode);
    var normalizedCardNumber = NormalizeCardNumber(payload.CardNumber);
    var normalizedPhotoInput = payload.PictureDataUrl?.Trim() ?? string.Empty;
    var normalizedPhotoForCompare = NormalizePictureDataUrlForComparison(normalizedPhotoInput);
    var normalizedPasswordInput = NormalizeEmployeePassword(payload.Password);
    var normalizedFullName = string.IsNullOrWhiteSpace(payload.FullName)
        ? normalizedEmployeeCode
        : payload.FullName.Trim();
    var unifiedUsers = string.IsNullOrWhiteSpace(normalizedCardNumber)
        ? Array.Empty<UnifiedUserSnapshot>()
        : repository.GetAllUnifiedUsers();
    var duplicateMessage = ValidateEmployeeUniqueness(
        employees,
        personnelStore,
        unifiedUsers,
        id,
        normalizedEmployeeCode,
        normalizedCardNumber,
        normalizedPhotoForCompare);
    if (duplicateMessage is not null)
    {
        return Results.BadRequest(new { message = duplicateMessage });
    }
    var passwordError = ValidateEmployeePassword(payload.Password, required: false);
    if (passwordError is not null)
    {
        return Results.BadRequest(new { message = passwordError });
    }

    var effectivePasswordInput = !string.IsNullOrWhiteSpace(normalizedPasswordInput)
        ? normalizedPasswordInput
        : (!string.IsNullOrWhiteSpace(employee.Password) ? employee.Password : normalizedEmployeeCode);
    var normalizedPassword = NormalizePasswordHash(effectivePasswordInput);
    if (string.IsNullOrWhiteSpace(normalizedPassword))
    {
        return Results.BadRequest(new { message = "Password is required." });
    }

    employee.EmployeeCode = normalizedEmployeeCode;
    employee.FullName = normalizedFullName;
    var resolvedAssignments = ResolveEmployeeAssignmentsFromInputs(
        personnelStore,
        payload.DepartmentCodes?.Cast<string?>() ?? [payload.Department],
        DefaultAreaCode,
        DefaultAreaName,
        DefaultDepartmentCode,
        DefaultDepartmentName);
    var primaryDepartment = ResolveDepartmentFromSetup(
        personnelStore,
        resolvedAssignments[0].DepartmentCode,
        DefaultAreaCode,
        DefaultAreaName,
        DefaultDepartmentCode,
        DefaultDepartmentName);
    employee.Department = primaryDepartment.Name;
    employee.Position = ResolvePositionNameFromSetup(
        personnelStore,
        payload.Position,
        employee.Department);
    employee.CardNumber = normalizedCardNumber;
    employee.Password = normalizedPassword;
    employee.MobileNumber = payload.MobileNumber?.Trim() ?? string.Empty;
    employee.Gender = payload.Gender?.Trim() ?? string.Empty;
    employee.Privilege = ResolveConfiguredEmployeePrivilege(payload.Privilege);
    employee.VerifyMode = ProgramHelpers.NormalizeEmployeeVerifyMode(payload.VerifyMode, employee.VerifyMode);
    employee.ProfilePhoto = string.IsNullOrWhiteSpace(normalizedPhotoInput) ? null : normalizedPhotoInput;
    if (IsLikelyPhotoPayload(employee.FaceTemplateVisible))
    {
        employee.FaceTemplateVisible = null;
    }
    if (IsLikelyPhotoPayload(employee.FaceTemplateInfrared))
    {
        employee.FaceTemplateInfrared = null;
    }
    employee.SyncedAt = null;
    employee.IsActive = payload.IsActive;

    var ok = repository.UpdateEmployee(employee);
    if (ok)
    {
        var effectiveGroupId = !employee.IsActive ? 0 : (payload.GroupId ?? 1);
        // Persist groupId and verifyMode to the unified user table so the API response reflects them.
        repository.UpsertUnifiedUser(
            pin: employee.EmployeeCode,
            name: employee.FullName,
            privilege: (int)employee.Privilege,
            password: null,
            primaryCard: normalizedCardNumber,
            secondaryCard: null,
            groupId: effectiveGroupId,
            timezoneProfile: effectiveGroupId == 0 ? "0000000000000000" : payload.TimezoneProfile,
            verifyMode: employee.VerifyMode,
            validFrom: null,
            validTo: null,
            expiresEnabled: effectiveGroupId == 0,
            phone: employee.MobileNumber,
            gender: null,
            nation: null,
            idNumber: null);
        UpsertProfilePhotoSnapshot(repository, employee);
        ProgramHelpers.UpsertEmployeeProfile(
            personnelStore,
            employee.EmployeeCode,
            payload.Privilege,
            normalizedCardNumber,
            normalizedPhotoInput,
            payload.MobileNumber,
            payload.Gender,
            payload.HireDate,
            normalizedPasswordInput,
            employee.Position,
            resolvedAssignments[0].AreaCode,
            resolvedAssignments[0].DepartmentCode,
            groupId: effectiveGroupId,
            timezoneProfile: effectiveGroupId == 0 ? "0000000000000000" : payload.TimezoneProfile,
            verifyMode: employee.VerifyMode,
            assignments: resolvedAssignments);
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);

        // Push updated USERINFO (Grp, Verify, etc.) to all devices immediately, then wake them
        // for near-real-time delivery. Wrapped so a transient sync error never fails the update.
        try
        {
            var updatedProfile = personnelStore.Profiles.FirstOrDefault(p => string.Equals(p.EmployeeCode, employee.EmployeeCode, StringComparison.OrdinalIgnoreCase));
            var newAreaCodes = resolvedAssignments
                .Select(a => a.AreaCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var devicesToWake = new List<Device>();
            var safeCodeForAreaRemoval = SanitizeCommandValue(employee.EmployeeCode);
            foreach (var device in service.ListDevices())
            {
                var setup = ResolveDeviceSetupForSerial(personnelStore, device.SerialNumber, device.Location, DefaultAreaCode, DefaultAreaName, DefaultDepartmentCode, DefaultDepartmentName);
                var deptName = ResolveEmployeeDepartmentForDevice(personnelStore, employee, updatedProfile, setup.Department, DefaultAreaCode, DefaultAreaName, DefaultDepartmentCode, DefaultDepartmentName);
                if (string.IsNullOrWhiteSpace(deptName))
                {
                    // This edit may have moved the employee OUT of a device's area (e.g. Area A -> Area
                    // B). Without this, the employee's old enrollment silently lingers forever on
                    // devices in areas they're no longer assigned to, since nothing else ever revisits
                    // devices the employee isn't currently mapped to.
                    if (!string.IsNullOrWhiteSpace(safeCodeForAreaRemoval) &&
                        previousAreaCodes.Contains(setup.Area.Code) &&
                        !newAreaCodes.Contains(setup.Area.Code))
                    {
                        service.QueueDeviceCommand(device.SerialNumber, "CHECK");
                        service.QueueDeviceCommand(device.SerialNumber, $"DATA DELETE USERINFO PIN={safeCodeForAreaRemoval}");
                        devicesToWake.Add(device);
                    }
                    continue;
                }
                if (!employee.IsActive)
                {
                    // Employee is inactive: delete from device so device cannot accept punches
                    service.QueueDeviceCommand(device.SerialNumber, "CHECK");
                    service.QueueDeviceCommand(device.SerialNumber, protocolService.CreateDeleteUserCommand(safeCodeForAreaRemoval));
                    devicesToWake.Add(device);
                }
                else
                {
                    var syncEmp = CreateEmployeeForDeviceSync(employee, updatedProfile, deptName);
                    syncEmp.DepartmentId = effectiveGroupId;
                    syncEmp.VerifyMode = employee.VerifyMode;
                    service.QueueDeviceCommand(device.SerialNumber, protocolService.CreateUserInfoCommand(syncEmp, device));
                    var bioCmds = protocolService.CreateEmployeeBiometricSyncCommands(syncEmp, device);
                    foreach (var bioCmd in bioCmds)
                    {
                        service.QueueDeviceCommand(device.SerialNumber, bioCmd.command);
                    }
                    devicesToWake.Add(device);
                }
            }

            if (employee.IsActive)
            {
                var queuedPhotoCommands = QueueEmployeePhotoSyncToAssignedDevices(
                    service,
                    protocolService,
                    personnelStore,
                    [employee],
                    DefaultAreaCode,
                    DefaultAreaName,
                    DefaultDepartmentCode,
                    DefaultDepartmentName);
                if (queuedPhotoCommands > 0)
                {
                    AddLog("INFO", "Sync", $"Queued {queuedPhotoCommands} photo sync command(s) for employee {employee.EmployeeCode}.");
                }
            }

            foreach (var device in devicesToWake)
            {
                _ = TryWakeDeviceAsync(device.IpAddress, device.SerialNumber, settings.Port);
            }
        }
        catch (Exception syncEx)
        {
            AddLog("WARN", "Sync", $"Employee {employee.EmployeeCode} updated, but real-time device sync hit an error.", syncEx.ToString());
        }
    }
    return ok ? Results.Ok(new { success = true }) : Results.NotFound(new { message = "Employee not found." });
});

app.MapDelete("/api/personnel/employees/{id:guid}", (Guid id) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var employee = repository.GetEmployees().FirstOrDefault(e => e.Id == id);
    if (employee is null)
    {
        return Results.NotFound(new { message = "Employee not found." });
    }

    var ok = repository.DeleteEmployee(id);
    var deletedDuplicateRows = 0;
    var deletedArtifactRows = 0;
    var queuedDeviceDeletes = 0;
    var targetedDevices = 0;
    if (ok)
    {
        var normalizedCode = employee.EmployeeCode?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(normalizedCode))
        {
            deletedArtifactRows += repository.DeleteEmployeeArtifactsByCode(normalizedCode);
            deletedArtifactRows += DeletePersonnelArtifactsInPostgres(postgresConnectionString, normalizedCode);

            var duplicateIds = repository.GetEmployees()
                .Where(e => e.Id != id && string.Equals(e.EmployeeCode, normalizedCode, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Id)
                .ToList();
            foreach (var duplicateId in duplicateIds)
            {
                if (repository.DeleteEmployee(duplicateId))
                {
                    deletedDuplicateRows++;
                }
            }
        }

        personnelStore.Profiles.RemoveAll(p => string.Equals(p.EmployeeCode, employee.EmployeeCode, StringComparison.OrdinalIgnoreCase));
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);

        var safeCode = SanitizeCommandValue(employee.EmployeeCode);
        if (!string.IsNullOrWhiteSpace(safeCode))
        {
            foreach (var serial in service.ListDevices()
                         .Select(d => d.SerialNumber?.Trim())
                         .Where(s => !string.IsNullOrWhiteSpace(s))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Cast<string>())
            {
                service.QueueDeviceCommand(serial, "CHECK");
                service.QueueDeviceCommand(serial, $"DATA DELETE USERINFO PIN={safeCode}");
                service.QueueDeviceCommand(serial, $"DATA DELETE ATTLOG PIN={safeCode}");
                queuedDeviceDeletes++;
                targetedDevices++;
            }
        }

        AddLog(
            "INFO",
            "Personnel",
            $"Deleted employee {employee.EmployeeCode} from DB and queued device delete.",
            $"deviceDeleteCommands={queuedDeviceDeletes}; devices={targetedDevices}; duplicateRowsDeleted={deletedDuplicateRows}; artifactRowsDeleted={deletedArtifactRows}");
    }
    return ok
        ? Results.Ok(new { success = true, queuedDeviceDeletes, targetedDevices, deletedDuplicateRows, deletedArtifactRows })
        : Results.NotFound(new { message = "Employee not found." });
});

app.MapPost("/api/personnel/employees/delete", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<EmployeeDeleteRequest>();
    if (payload is null || payload.Ids.Count == 0)
    {
        return Results.BadRequest(new { message = "Select employee(s) first." });
    }

    var employeeById = repository.GetEmployees().ToDictionary(e => e.Id);
    var removed = 0;
    var removedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var deletedArtifactRows = 0;
    foreach (var id in payload.Ids)
    {
        if (!employeeById.TryGetValue(id, out var employee))
        {
            continue;
        }

        if (repository.DeleteEmployee(id))
        {
            removed++;
            var code = employee.EmployeeCode?.Trim();
            if (!string.IsNullOrWhiteSpace(code))
            {
                removedCodes.Add(code);
            }
        }
    }

    if (removedCodes.Count > 0)
    {
        foreach (var code in removedCodes)
        {
            deletedArtifactRows += repository.DeleteEmployeeArtifactsByCode(code);
            deletedArtifactRows += DeletePersonnelArtifactsInPostgres(postgresConnectionString, code);
        }

        personnelStore.Profiles.RemoveAll(p => removedCodes.Contains(p.EmployeeCode));
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    }

    var queuedDeviceDeletes = 0;
    var targetedDevices = 0;
    if (removedCodes.Count > 0)
    {
        var serials = service.ListDevices()
            .Select(d => d.SerialNumber?.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();

        foreach (var serial in serials)
        {
            service.QueueDeviceCommand(serial, "CHECK");
            targetedDevices++;
            foreach (var code in removedCodes)
            {
                var safeCode = SanitizeCommandValue(code);
                if (string.IsNullOrWhiteSpace(safeCode))
                {
                    continue;
                }

                service.QueueDeviceCommand(serial, $"DATA DELETE USERINFO PIN={safeCode}");
                queuedDeviceDeletes++;
            }
        }
    }

    AddLog(
        "INFO",
        "Personnel",
        $"Bulk delete completed. Removed users: {removed}.",
        $"deviceDeleteCommands={queuedDeviceDeletes}; devices={targetedDevices}; removedCodes={string.Join(",", removedCodes)}; artifactRowsDeleted={deletedArtifactRows}");

    return Results.Ok(new { success = true, removed, queuedDeviceDeletes, targetedDevices, deletedArtifactRows });
});

app.MapPost("/api/personnel/employees/credential/delete", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<EmployeeCredentialDeleteRequest>();
    if (payload is null)
    {
        return Results.BadRequest(new { message = "Invalid request payload." });
    }

    var credentialType = (payload.CredentialType ?? "").Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(credentialType))
    {
        return Results.BadRequest(new { message = "credentialType is required." });
    }

    var allEmployees = repository.GetEmployees();
    var targetEmployees = new List<Employee>();

    if (payload.EmployeeIds != null && payload.EmployeeIds.Count > 0)
    {
        var rawSet = new HashSet<string>(payload.EmployeeIds.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        foreach (var emp in allEmployees)
        {
            if (rawSet.Contains(emp.Id.ToString()) || (!string.IsNullOrWhiteSpace(emp.EmployeeCode) && rawSet.Contains(emp.EmployeeCode.Trim())))
            {
                targetEmployees.Add(emp);
            }
        }
    }
    else
    {
        Employee? employee = null;
        if (!string.IsNullOrWhiteSpace(payload.EmployeeId) && Guid.TryParse(payload.EmployeeId, out var empGuid))
        {
            employee = allEmployees.FirstOrDefault(e => e.Id == empGuid);
        }

        if (employee is null && !string.IsNullOrWhiteSpace(payload.EmployeeCode))
        {
            employee = allEmployees.FirstOrDefault(e => string.Equals(e.EmployeeCode?.Trim(), payload.EmployeeCode.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (employee != null)
        {
            targetEmployees.Add(employee);
        }
        else if (!string.IsNullOrWhiteSpace(payload.EmployeeCode))
        {
            targetEmployees.Add(new Employee { EmployeeCode = payload.EmployeeCode.Trim() });
        }
    }

    if (targetEmployees.Count == 0)
    {
        return Results.BadRequest(new { message = "No valid employees found." });
    }

    var serials = service.ListDevices()
        .Select(d => d.SerialNumber?.Trim())
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Cast<string>()
        .ToList();

    var targetCodes = targetEmployees.Select(e => e.EmployeeCode?.Trim()).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).Cast<string>().ToList();

    if (credentialType == "user")
    {
        var removedCount = 0;
        foreach (var emp in targetEmployees)
        {
            repository.DeleteEmployee(emp.Id);
            var code = emp.EmployeeCode?.Trim();
            if (!string.IsNullOrWhiteSpace(code))
            {
                repository.DeleteEmployeeArtifactsByCode(code);
                DeletePersonnelArtifactsInPostgres(postgresConnectionString, code);
                personnelStore.Profiles.RemoveAll(p => string.Equals(p.EmployeeCode, code, StringComparison.OrdinalIgnoreCase));
                var safeCode = SanitizeCommandValue(code);
                foreach (var serial in serials)
                {
                    service.QueueDeviceCommand(serial, $"DATA DELETE USERINFO PIN={safeCode}");
                }
                removedCount++;
            }
        }
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
        AddLog("INFO", "Personnel", $"Deleted {removedCount} employee(s)", $"devices={serials.Count}");
        return Results.Ok(new { success = true, credentialType = "user", count = removedCount, message = $"Deleted {removedCount} employee(s)." });
    }

    if (credentialType is "fp" or "all-biometrics")
    {
        if (!string.IsNullOrWhiteSpace(postgresConnectionString))
        {
            try
            {
                using var conn = new NpgsqlConnection(postgresConnectionString);
                conn.Open();
                foreach (var code in targetCodes)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"DELETE FROM ""E_Attendace"".""biometric_templates"" WHERE ""user_pin"" = @pin AND ""type"" = 1;";
                    cmd.Parameters.AddWithValue("pin", code);
                    cmd.ExecuteNonQuery();

                    using var cmd2 = conn.CreateCommand();
                    cmd2.CommandText = @"UPDATE ""E_Attendace"".""Employees"" SET ""FingerprintTemplates"" = NULL WHERE ""EmployeeCode"" = @pin;";
                    cmd2.Parameters.AddWithValue("pin", code);
                    cmd2.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                AddLog("WARN", "Personnel", $"Failed to delete fingerprint templates in PostgreSQL: {ex.Message}");
            }
        }

        foreach (var emp in targetEmployees)
        {
            emp.FingerprintTemplates = null;
            repository.UpdateEmployee(emp);
        }

        foreach (var serial in serials)
        {
            foreach (var code in targetCodes)
            {
                var safeCode = SanitizeCommandValue(code);
                if (!string.IsNullOrWhiteSpace(safeCode))
                {
                    service.QueueDeviceCommand(serial, $"DATA DELETE FINGERTMP PIN={safeCode}");
                }
            }
        }
    }

    if (credentialType is "face" or "all-biometrics")
    {
        if (!string.IsNullOrWhiteSpace(postgresConnectionString))
        {
            try
            {
                using var conn = new NpgsqlConnection(postgresConnectionString);
                conn.Open();
                foreach (var code in targetCodes)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"DELETE FROM ""E_Attendace"".""biometric_templates"" WHERE ""user_pin"" = @pin AND ""type"" IN (2, 9);";
                    cmd.Parameters.AddWithValue("pin", code);
                    cmd.ExecuteNonQuery();

                    try
                    {
                        using var cmdPhoto = conn.CreateCommand();
                        cmdPhoto.CommandText = @"DELETE FROM ""E_Attendace"".""biometric_photos"" WHERE ""user_pin"" = @pin;";
                        cmdPhoto.Parameters.AddWithValue("pin", code);
                        cmdPhoto.ExecuteNonQuery();
                    }
                    catch { }

                    using var cmd2 = conn.CreateCommand();
                    cmd2.CommandText = @"UPDATE ""E_Attendace"".""Employees"" SET ""FaceTemplateVisible"" = NULL, ""FaceTemplateInfrared"" = NULL WHERE ""EmployeeCode"" = @pin;";
                    cmd2.Parameters.AddWithValue("pin", code);
                    cmd2.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                AddLog("WARN", "Personnel", $"Failed to delete face templates in PostgreSQL: {ex.Message}");
            }
        }

        foreach (var emp in targetEmployees)
        {
            emp.FaceTemplateVisible = null;
            emp.FaceTemplateInfrared = null;
            repository.UpdateEmployee(emp);
        }

        foreach (var serial in serials)
        {
            foreach (var code in targetCodes)
            {
                var safeCode = SanitizeCommandValue(code);
                if (!string.IsNullOrWhiteSpace(safeCode))
                {
                    service.QueueDeviceCommand(serial, $"DATA DELETE BIODATA PIN={safeCode}\tType=9");
                    service.QueueDeviceCommand(serial, $"DATA DELETE BIODATA PIN={safeCode}\tType=2");
                    service.QueueDeviceCommand(serial, $"DATA DELETE FACE PIN={safeCode}");
                }
            }
        }
    }

    if (credentialType is "card" or "all-biometrics")
    {
        if (!string.IsNullOrWhiteSpace(postgresConnectionString))
        {
            try
            {
                using var conn = new NpgsqlConnection(postgresConnectionString);
                conn.Open();
                foreach (var code in targetCodes)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"UPDATE ""E_Attendace"".""Employees"" SET ""CardNumber"" = '' WHERE ""EmployeeCode"" = @pin;";
                    cmd.Parameters.AddWithValue("pin", code);
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                AddLog("WARN", "Personnel", $"Failed to clear cards in PostgreSQL: {ex.Message}");
            }
        }

        foreach (var emp in targetEmployees)
        {
            emp.CardNumber = "";
            repository.UpdateEmployee(emp);
        }

        foreach (var code in targetCodes)
        {
            var profileIdx = personnelStore.Profiles.FindIndex(p => string.Equals(p.EmployeeCode, code, StringComparison.OrdinalIgnoreCase));
            if (profileIdx >= 0)
            {
                personnelStore.Profiles[profileIdx] = personnelStore.Profiles[profileIdx] with { CardNumber = "" };
            }
        }
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);

        foreach (var serial in serials)
        {
            foreach (var code in targetCodes)
            {
                var safeCode = SanitizeCommandValue(code);
                if (!string.IsNullOrWhiteSpace(safeCode))
                {
                    service.QueueDeviceCommand(serial, $"DATA UPDATE USERINFO PIN={safeCode}\tCard=");
                }
            }
        }
    }

    if (credentialType is "palm" or "all-biometrics")
    {
        if (!string.IsNullOrWhiteSpace(postgresConnectionString))
        {
            try
            {
                using var conn = new NpgsqlConnection(postgresConnectionString);
                conn.Open();
                foreach (var code in targetCodes)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"DELETE FROM ""E_Attendace"".""biometric_templates"" WHERE ""user_pin"" = @pin AND ""type"" IN (7, 8);";
                    cmd.Parameters.AddWithValue("pin", code);
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                AddLog("WARN", "Personnel", $"Failed to delete palm templates in PostgreSQL: {ex.Message}");
            }
        }

        foreach (var serial in serials)
        {
            foreach (var code in targetCodes)
            {
                var safeCode = SanitizeCommandValue(code);
                if (!string.IsNullOrWhiteSpace(safeCode))
                {
                    service.QueueDeviceCommand(serial, $"DATA DELETE BIODATA PIN={safeCode}\tType=8");
                    service.QueueDeviceCommand(serial, $"DATA DELETE BIODATA PIN={safeCode}\tType=7");
                }
            }
        }
    }

    if (credentialType is "password" or "all-biometrics")
    {
        if (!string.IsNullOrWhiteSpace(postgresConnectionString))
        {
            try
            {
                using var conn = new NpgsqlConnection(postgresConnectionString);
                conn.Open();
                foreach (var code in targetCodes)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"UPDATE ""E_Attendace"".""Employees"" SET ""Password"" = '' WHERE ""EmployeeCode"" = @pin;";
                    cmd.Parameters.AddWithValue("pin", code);
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                AddLog("WARN", "Personnel", $"Failed to clear passwords in PostgreSQL: {ex.Message}");
            }
        }

        foreach (var emp in targetEmployees)
        {
            emp.Password = "";
            repository.UpdateEmployee(emp);
        }

        foreach (var code in targetCodes)
        {
            var profileIdx = personnelStore.Profiles.FindIndex(p => string.Equals(p.EmployeeCode, code, StringComparison.OrdinalIgnoreCase));
            if (profileIdx >= 0)
            {
                personnelStore.Profiles[profileIdx] = personnelStore.Profiles[profileIdx] with { Password = "" };
            }
        }
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);

        foreach (var serial in serials)
        {
            foreach (var code in targetCodes)
            {
                var safeCode = SanitizeCommandValue(code);
                if (!string.IsNullOrWhiteSpace(safeCode))
                {
                    service.QueueDeviceCommand(serial, $"DATA UPDATE USERINFO PIN={safeCode}\tPasswd=");
                }
            }
        }
    }

    if (credentialType is not ("fp" or "face" or "card" or "palm" or "password" or "all-biometrics" or "user"))
    {
        return Results.BadRequest(new { message = $"Unsupported credential type '{credentialType}'. Allowed: fp, face, card, palm, password, all-biometrics, user." });
    }

    AddLog("INFO", "Personnel", $"Deleted {credentialType} for {targetCodes.Count} employee(s)", $"devices={serials.Count}; codes={string.Join(",", targetCodes)}");
    return Results.Ok(new { success = true, credentialType, count = targetCodes.Count, message = $"Deleted {credentialType} for {targetCodes.Count} employee(s)." });
});

app.MapPost("/api/personnel/employees/status", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<EmployeeStatusRequest>();
    if (payload is null || payload.Ids.Count == 0)
    {
        return Results.BadRequest(new { message = "Select employee(s) first." });
    }

    var updated = 0;
    var byId = repository.GetEmployees().ToDictionary(e => e.Id);
    var updatedEmployees = new List<Employee>();
    foreach (var id in payload.Ids)
    {
        if (!byId.TryGetValue(id, out var employee))
        {
            continue;
        }

        employee.IsActive = payload.IsActive;
        if (repository.UpdateEmployee(employee))
        {
            updated++;
            updatedEmployees.Add(employee);
            var effectiveGrp = payload.IsActive ? (employee.DepartmentId > 0 ? employee.DepartmentId : 1) : 0;
            repository.UpsertUnifiedUser(
                pin: employee.EmployeeCode,
                name: employee.FullName,
                privilege: (int)employee.Privilege,
                password: null,
                primaryCard: employee.CardNumber,
                secondaryCard: null,
                groupId: effectiveGrp,
                timezoneProfile: payload.IsActive ? null : "0000000000000000",
                verifyMode: employee.VerifyMode,
                validFrom: null,
                validTo: null,
                expiresEnabled: !payload.IsActive,
                phone: employee.MobileNumber,
                gender: null,
                nation: null,
                idNumber: null);

            var profile = personnelStore.Profiles.FirstOrDefault(p => string.Equals(p.EmployeeCode, employee.EmployeeCode, StringComparison.OrdinalIgnoreCase));
            if (profile != null)
            {
                var idx = personnelStore.Profiles.IndexOf(profile);
                personnelStore.Profiles[idx] = profile with { GroupId = effectiveGrp };
            }
        }
    }

    if (updated > 0)
    {
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    }

    if (updatedEmployees.Count > 0)
    {
        var devices = service.ListDevices()
            .Where(d => !string.IsNullOrWhiteSpace(d.SerialNumber))
            .ToList();

        foreach (var device in devices)
        {
            foreach (var emp in updatedEmployees)
            {
                if (!payload.IsActive)
                {
                    // Inactive: delete user from device so the machine rejects punches
                    service.QueueDeviceCommand(device.SerialNumber, "CHECK");
                    service.QueueDeviceCommand(device.SerialNumber, protocolService.CreateDeleteUserCommand(emp.EmployeeCode));
                }
                else
                {
                    // Active: re-enroll user and all their biometric templates on the device
                    service.QueueDeviceCommand(device.SerialNumber, protocolService.CreateUserInfoCommand(emp, device));
                    var bioCmds = protocolService.CreateEmployeeBiometricSyncCommands(emp, device);
                    foreach (var bioCmd in bioCmds)
                    {
                        service.QueueDeviceCommand(device.SerialNumber, bioCmd.command);
                    }
                }
            }

            _ = TryWakeDeviceAsync(device.IpAddress, device.SerialNumber, settings.Port);
        }

        AddLog("INFO", "Personnel", $"Bulk employee status set to {(payload.IsActive ? "Active" : "Inactive")}", $"updated={updated}; devices={devices.Count}; codes={string.Join(",", updatedEmployees.Select(e => e.EmployeeCode))}");
    }

    return Results.Ok(new { success = true, updated });
});

app.MapPost("/api/personnel/employees/verify-mode", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<EmployeeVerifyModeRequest>();
    if (payload is null || payload.Ids.Count == 0)
    {
        return Results.BadRequest(new { message = "Select employee(s) first." });
    }

    var normalizedVerifyMode = ProgramHelpers.NormalizeEmployeeVerifyMode(payload.VerifyMode);
    var updated = 0;
    var byId = repository.GetEmployees().ToDictionary(e => e.Id);
    foreach (var id in payload.Ids)
    {
        if (!byId.TryGetValue(id, out var employee))
        {
            continue;
        }

        employee.VerifyMode = normalizedVerifyMode;
        employee.SyncedAt = null;
        if (!repository.UpdateEmployee(employee))
        {
            continue;
        }

        var profile = personnelStore.Profiles.FirstOrDefault(p =>
            string.Equals(p.EmployeeCode, employee.EmployeeCode, StringComparison.OrdinalIgnoreCase));
        ProgramHelpers.UpsertEmployeeProfile(
            personnelStore,
            employee.EmployeeCode,
            profile?.Privilege ?? ToProfilePrivilegeLabel(employee.Privilege),
            profile?.CardNumber ?? employee.CardNumber,
            profile?.PictureDataUrl ?? employee.ProfilePhoto,
            profile?.MobileNumber ?? employee.MobileNumber,
            profile?.Gender ?? employee.Gender,
            profile?.HireDate,
            ResolveDevicePassword(profile?.Password, employee.Password),
            string.IsNullOrWhiteSpace(profile?.Position) ? employee.Position : profile.Position,
            profile?.AreaCode,
            profile?.DepartmentCode,
            verifyMode: normalizedVerifyMode);

        // Sync verify mode to all devices in real time.
        var syncProfile = personnelStore.Profiles.FirstOrDefault(p =>
            string.Equals(p.EmployeeCode, employee.EmployeeCode, StringComparison.OrdinalIgnoreCase));
        foreach (var device in service.ListDevices())
        {
            var setup = ResolveDeviceSetupForSerial(personnelStore, device.SerialNumber, device.Location, DefaultAreaCode, DefaultAreaName, DefaultDepartmentCode, DefaultDepartmentName);
            var deptName = ResolveEmployeeDepartmentForDevice(personnelStore, employee, syncProfile, setup.Department, DefaultAreaCode, DefaultAreaName, DefaultDepartmentCode, DefaultDepartmentName);
            if (string.IsNullOrWhiteSpace(deptName)) continue;
            var syncEmp = CreateEmployeeForDeviceSync(employee, syncProfile, deptName);
            syncEmp.VerifyMode = normalizedVerifyMode;
            service.QueueDeviceCommand(device.SerialNumber, protocolService.CreateUserInfoCommand(syncEmp, device));
        }
        updated++;
    }

    if (updated > 0)
    {
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    }

    return Results.Ok(new { success = true, updated, verifyMode = normalizedVerifyMode });
});

app.MapPost("/api/personnel/employees/transfer", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<EmployeeTransferRequest>();
    if (payload is null || payload.Ids.Count == 0)
    {
        return Results.BadRequest(new { message = "Select employee(s) first." });
    }

    var resolvedAssignments = ResolveEmployeeAssignmentsFromInputs(
        personnelStore,
        payload.DepartmentCodes?.Cast<string?>() ?? [payload.Department],
        DefaultAreaCode,
        DefaultAreaName,
        DefaultDepartmentCode,
        DefaultDepartmentName);
    if (resolvedAssignments.Count == 0)
    {
        return Results.BadRequest(new { message = "Select target department(s) first." });
    }

    var updated = 0;
    var byId = repository.GetEmployees().ToDictionary(e => e.Id);
    foreach (var id in payload.Ids.Distinct())
    {
        if (!byId.TryGetValue(id, out var employee))
        {
            continue;
        }

        if (ApplyEmployeeAssignments(
                repository,
                personnelStore,
                employee,
                resolvedAssignments,
                DefaultAreaCode,
                DefaultAreaName,
                DefaultDepartmentCode,
                DefaultDepartmentName))
        {
            updated++;
        }
    }

    if (updated > 0)
    {
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    }

    return Results.Ok(new { success = true, updated });
});

app.MapPost("/api/personnel/employees/import", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<EmployeeImportRequest>();
    if (payload is null || payload.Items.Count == 0)
    {
        return Results.BadRequest(new { message = "No rows to import." });
    }

    var mode = (payload.Mode ?? "skip").Trim().ToLowerInvariant();
    var overwriteExisting = mode is "overwrite" or "update";

    PersonnelArea? FindAreaStrict(string token)
    {
        var t = token.Trim();
        if (string.IsNullOrWhiteSpace(t))
        {
            return null;
        }

        return personnelStore.Areas.FirstOrDefault(a =>
            string.Equals(a.Code, t, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Name, t, StringComparison.OrdinalIgnoreCase));
    }


    var employees = repository.GetEmployees().ToList();
    var employeeByCode = employees
        .Where(e => !string.IsNullOrWhiteSpace(e.EmployeeCode))
        .ToDictionary(e => e.EmployeeCode, StringComparer.OrdinalIgnoreCase);

    var added = 0;
    var updated = 0;
    var skipped = 0;
    var skippedItems = new List<object>();
    var updatedProfiles = false;

    for (var index = 0; index < payload.Items.Count; index++)
    {
        var row = payload.Items[index];
        var line = index + 2;

        var code = row.UserId?.Trim() ?? row.EmployeeCode?.Trim() ?? string.Empty;
        var name = row.FullName?.Trim() ?? string.Empty;
        var areaInput = row.AreaName?.Trim() ?? row.Area?.Trim() ?? row.AreaCode?.Trim() ?? string.Empty;
        var departmentInput = row.DepartmentName?.Trim() ?? row.Department?.Trim() ?? row.DepartmentCode?.Trim() ?? string.Empty;
        var cardNumber = NormalizeCardNumber(row.CardNumber);
        var passwordInput = NormalizeEmployeePassword(row.Password);

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(areaInput) || string.IsNullOrWhiteSpace(departmentInput))
        {
            skipped++;
            skippedItems.Add(new { line, userId = code, reason = "Missing mandatory field(s): UserId, Area, Department." });
            continue;
        }

        var resolvedArea = FindAreaStrict(areaInput);
        if (resolvedArea is null)
        {
            skipped++;
            skippedItems.Add(new { line, userId = code, reason = $"Area not found: {areaInput}" });
            continue;
        }

        var resolvedDepartment = personnelStore.Departments.FirstOrDefault(d =>
            string.Equals(d.AreaCode, resolvedArea.Code, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(d.Code, departmentInput, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(d.Name, departmentInput, StringComparison.OrdinalIgnoreCase)));

        if (resolvedDepartment is null)
        {
            skipped++;
            skippedItems.Add(new { line, userId = code, reason = $"Department not found for area '{resolvedArea.Name}': {departmentInput}" });
            continue;
        }

        var resolvedDepartmentName = resolvedDepartment.Name;

        if (employeeByCode.TryGetValue(code, out var existingEmployee))
        {
            if (!overwriteExisting)
            {
                skipped++;
                skippedItems.Add(new { line, userId = code, reason = "UserId already exists (skipped)." });
                continue;
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                existingEmployee.FullName = name;
            }

            existingEmployee.Department = resolvedDepartmentName;
            existingEmployee.Position = ResolvePositionNameFromSetup(personnelStore, existingEmployee.Position, resolvedDepartmentName);
            existingEmployee.IsActive = row.IsActive;
            if (!string.IsNullOrWhiteSpace(cardNumber))
            {
                existingEmployee.CardNumber = cardNumber;
            }
            if (!string.IsNullOrWhiteSpace(passwordInput))
            {
                existingEmployee.Password = NormalizePasswordHash(passwordInput);
            }
            existingEmployee.SyncedAt = null;

            if (!repository.UpdateEmployee(existingEmployee))
            {
                skipped++;
                skippedItems.Add(new { line, userId = code, reason = "Failed to update employee." });
                continue;
            }

            ProgramHelpers.UpsertEmployeeProfile(
                personnelStore,
                existingEmployee.EmployeeCode,
                existingEmployee.Privilege.ToString(),
                existingEmployee.CardNumber,
                existingEmployee.ProfilePhoto,
                existingEmployee.MobileNumber,
                existingEmployee.Gender,
                null,
                string.IsNullOrWhiteSpace(passwordInput) ? ResolveDevicePassword(null, existingEmployee.Password) : passwordInput,
                existingEmployee.Position,
                resolvedArea.Code,
                resolvedDepartment.Code,
                verifyMode: existingEmployee.VerifyMode,
                assignments: [new PersonnelEmployeeAssignment(resolvedArea.Code, resolvedDepartment.Code)]);
            updatedProfiles = true;

            updated++;
            continue;
        }

        var resolvedFullName = string.IsNullOrWhiteSpace(name) ? code : name;
        var employee = service.CreateEmployee(code, resolvedFullName, resolvedDepartmentName);
        employee.Position = ResolvePositionNameFromSetup(personnelStore, null, resolvedDepartmentName);
        employee.CardNumber = cardNumber;
        employee.Password = NormalizePasswordHash(string.IsNullOrWhiteSpace(passwordInput) ? code : passwordInput);
        employee.IsActive = row.IsActive;

        if (!repository.UpdateEmployee(employee))
        {
            skipped++;
            skippedItems.Add(new { line, userId = code, reason = "Failed to create employee." });
            continue;
        }

        ProgramHelpers.UpsertEmployeeProfile(
            personnelStore,
            employee.EmployeeCode,
            employee.Privilege.ToString(),
            employee.CardNumber,
            employee.ProfilePhoto,
            employee.MobileNumber,
            employee.Gender,
            null,
            string.IsNullOrWhiteSpace(passwordInput) ? code : passwordInput,
            employee.Position,
            resolvedArea.Code,
            resolvedDepartment.Code,
            verifyMode: employee.VerifyMode,
            assignments: [new PersonnelEmployeeAssignment(resolvedArea.Code, resolvedDepartment.Code)]);
        updatedProfiles = true;

        employeeByCode[code] = employee;
        added++;
    }

    if (updatedProfiles)
    {
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    }

    return Results.Ok(new { success = true, mode = overwriteExisting ? "overwrite" : "skip", added, updated, skipped, skippedItems });
});

app.MapPost("/api/personnel/employees/import-photos", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<EmployeePhotoImportRequest>();
    if (payload is null || payload.Items.Count == 0)
    {
        return Results.BadRequest(new { message = "No rows to import." });
    }

    var mode = (payload.Mode ?? "overwrite").Trim().ToLowerInvariant();
    var overwriteExisting = mode is "overwrite" or "update";

    var employees = repository.GetEmployees().ToList();
    var updated = 0;
    var skipped = 0;
    var skippedItems = new List<object>();
    var updatedEmployees = new List<Employee>();

    for (var i = 0; i < payload.Items.Count; i++)
    {
        var row = payload.Items[i];
        var line = i + 2;

        var code = row.EmployeeCode?.Trim() ?? string.Empty;
        var pictureDataUrl = row.PictureDataUrl?.Trim() ?? string.Empty;
        var normalizedPhotoForCompare = NormalizePictureDataUrlForComparison(pictureDataUrl);
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(normalizedPhotoForCompare))
        {
            skipped++;
            skippedItems.Add(new { line, userId = code, reason = "Missing UserId or PictureDataUrl." });
            continue;
        }

        var employee = employees.FirstOrDefault(e => string.Equals(e.EmployeeCode, code, StringComparison.OrdinalIgnoreCase));
        if (employee is null)
        {
            skipped++;
            skippedItems.Add(new { line, userId = code, reason = "Employee not found." });
            continue;
        }

        var profile = personnelStore.Profiles.FirstOrDefault(p => string.Equals(p.EmployeeCode, code, StringComparison.OrdinalIgnoreCase));
        var existingPicture = string.IsNullOrWhiteSpace(employee.ProfilePhoto) ? (profile?.PictureDataUrl ?? string.Empty) : employee.ProfilePhoto;
        var existingNormalized = NormalizePictureDataUrlForComparison(existingPicture);
        if (!overwriteExisting && !string.IsNullOrWhiteSpace(existingNormalized))
        {
            skipped++;
            skippedItems.Add(new { line, userId = code, reason = "Photo already exists (skipped)." });
            continue;
        }

        var duplicatePhoto = employees.Any(e =>
            e.Id != employee.Id &&
            NormalizePictureDataUrlForComparison(e.ProfilePhoto) == normalizedPhotoForCompare);
        if (duplicatePhoto)
        {
            skipped++;
            skippedItems.Add(new { line, userId = code, reason = "Same photo already used by another employee." });
            continue;
        }

        employee.ProfilePhoto = pictureDataUrl;
        if (IsLikelyPhotoPayload(employee.FaceTemplateVisible))
        {
            employee.FaceTemplateVisible = null;
        }
        if (IsLikelyPhotoPayload(employee.FaceTemplateInfrared))
        {
            employee.FaceTemplateInfrared = null;
        }
        employee.SyncedAt = null;
        if (!repository.UpdateEmployee(employee))
        {
            skipped++;
            skippedItems.Add(new { line, userId = code, reason = "Failed to update employee photo." });
            continue;
        }

        var index = employees.FindIndex(e => e.Id == employee.Id);
        if (index >= 0)
        {
            employees[index] = employee;
        }
        UpsertProfilePhotoSnapshot(repository, employee);

        ProgramHelpers.UpsertEmployeeProfile(
            personnelStore,
            code,
            profile?.Privilege ?? ToProfilePrivilegeLabel(employee.Privilege),
            profile?.CardNumber ?? employee.CardNumber,
            pictureDataUrl,
            profile?.MobileNumber ?? employee.MobileNumber,
            profile?.Gender ?? (string.IsNullOrWhiteSpace(employee.Gender) ? "Male" : employee.Gender),
            profile?.HireDate ?? string.Empty,
            ResolveDevicePassword(profile?.Password, employee.Password));

        updated++;
        updatedEmployees.Add(employee);
    }

    if (updated > 0)
    {
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
        var queuedPhotoCommands = QueueEmployeePhotoSyncToAssignedDevices(
            service,
            protocolService,
            personnelStore,
            updatedEmployees,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName);
        if (queuedPhotoCommands > 0)
        {
            AddLog("INFO", "Sync", $"Queued {queuedPhotoCommands} photo sync command(s) after employee photo import.");
            try
            {
                var activeDevices = service.ListDevices();
                foreach (var device in activeDevices)
                {
                    var devSetup = ResolveDeviceSetupForSerial(personnelStore, device.SerialNumber, device.Location, DefaultAreaCode, DefaultAreaName, DefaultDepartmentCode, DefaultDepartmentName);
                    var anyAssigned = false;
                    foreach (var emp in updatedEmployees)
                    {
                        var profile = personnelStore.Profiles.FirstOrDefault(p => string.Equals(p.EmployeeCode, emp.EmployeeCode, StringComparison.OrdinalIgnoreCase));
                        var deptName = ResolveEmployeeDepartmentForDevice(personnelStore, emp, profile, devSetup.Department, DefaultAreaCode, DefaultAreaName, DefaultDepartmentCode, DefaultDepartmentName);
                        if (!string.IsNullOrWhiteSpace(deptName))
                        {
                            anyAssigned = true;
                            break;
                        }
                    }
                    if (anyAssigned)
                    {
                        _ = TryWakeDeviceAsync(device.IpAddress, device.SerialNumber, settings.Port);
                    }
                }
            }
            catch (Exception wakeEx)
            {
                AddLog("WARN", "Sync", "Failed to wake devices during bulk photo import.", wakeEx.ToString());
            }
        }
    }

    return Results.Ok(new { success = true, mode = overwriteExisting ? "overwrite" : "skip", updated, skipped, skippedItems });
});

app.MapPost("/api/personnel/employees/import-photos-zip", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "Photo zip import requires multipart form data." });
    }

    var form = await request.ReadFormAsync();
    var mode = (form["mode"].ToString() ?? "overwrite").Trim().ToLowerInvariant();
    var overwriteExisting = mode is "overwrite" or "update";
    var importRows = new List<EmployeePhotoImportRowRequest>();
    var localSkipped = new List<object>();
    var line = 1;

    static bool IsPhotoExtension(string extension)
        => extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);

    static string MimeFromExtension(string extension)
        => extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/jpeg"
        };

    static async Task<string> ToDataUrlAsync(Stream stream, string extension)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return $"data:{MimeFromExtension(extension)};base64,{Convert.ToBase64String(ms.ToArray())}";
    }

    foreach (var file in form.Files)
    {
        var extension = Path.GetExtension(file.FileName);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await using var zipStream = file.OpenReadStream();
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                line++;
                var entryName = Path.GetFileName(entry.FullName);
                var entryExtension = Path.GetExtension(entryName);
                if (string.IsNullOrWhiteSpace(entryName) || !IsPhotoExtension(entryExtension))
                {
                    continue;
                }

                var code = Path.GetFileNameWithoutExtension(entryName).Trim();
                if (string.IsNullOrWhiteSpace(code))
                {
                    localSkipped.Add(new { line, userId = entryName, reason = "Photo filename must be the UserId." });
                    continue;
                }

                await using var entryStream = entry.Open();
                importRows.Add(new EmployeePhotoImportRowRequest(code, await ToDataUrlAsync(entryStream, entryExtension)));
            }
            continue;
        }

        line++;
        if (!IsPhotoExtension(extension))
        {
            localSkipped.Add(new { line, userId = file.FileName, reason = "Unsupported photo file type." });
            continue;
        }

        var fileCode = Path.GetFileNameWithoutExtension(file.FileName).Trim();
        if (string.IsNullOrWhiteSpace(fileCode))
        {
            localSkipped.Add(new { line, userId = file.FileName, reason = "Photo filename must be the UserId." });
            continue;
        }

        await using var stream = file.OpenReadStream();
        importRows.Add(new EmployeePhotoImportRowRequest(fileCode, await ToDataUrlAsync(stream, extension)));
    }

    if (importRows.Count == 0)
    {
        return Results.BadRequest(new { message = "No valid photo files found. File name without extension must be the UserId.", skippedItems = localSkipped });
    }

    var employees = repository.GetEmployees().ToList();
    var updated = 0;
    var skipped = localSkipped.Count;
    var skippedItems = new List<object>(localSkipped);
    var updatedEmployees = new List<Employee>();

    for (var i = 0; i < importRows.Count; i++)
    {
        var row = importRows[i];
        var rowLine = i + 1;
        var code = row.EmployeeCode?.Trim() ?? string.Empty;
        var pictureDataUrl = row.PictureDataUrl?.Trim() ?? string.Empty;
        var normalizedPhotoForCompare = NormalizePictureDataUrlForComparison(pictureDataUrl);
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(normalizedPhotoForCompare))
        {
            skipped++;
            skippedItems.Add(new { line = rowLine, userId = code, reason = "Missing UserId or photo data." });
            continue;
        }

        var employee = employees.FirstOrDefault(e => string.Equals(e.EmployeeCode, code, StringComparison.OrdinalIgnoreCase));
        if (employee is null)
        {
            skipped++;
            skippedItems.Add(new { line = rowLine, userId = code, reason = "Employee not found." });
            continue;
        }

        var profile = personnelStore.Profiles.FirstOrDefault(p => string.Equals(p.EmployeeCode, code, StringComparison.OrdinalIgnoreCase));
        var existingPicture = string.IsNullOrWhiteSpace(employee.ProfilePhoto) ? (profile?.PictureDataUrl ?? string.Empty) : employee.ProfilePhoto;
        var existingNormalized = NormalizePictureDataUrlForComparison(existingPicture);
        if (!overwriteExisting && !string.IsNullOrWhiteSpace(existingNormalized))
        {
            skipped++;
            skippedItems.Add(new { line = rowLine, userId = code, reason = "Photo already exists (skipped)." });
            continue;
        }

        var duplicatePhoto = employees.Any(e =>
            e.Id != employee.Id &&
            NormalizePictureDataUrlForComparison(e.ProfilePhoto) == normalizedPhotoForCompare);
        if (duplicatePhoto)
        {
            skipped++;
            skippedItems.Add(new { line = rowLine, userId = code, reason = "Same photo already used by another employee." });
            continue;
        }

        employee.ProfilePhoto = pictureDataUrl;
        if (IsLikelyPhotoPayload(employee.FaceTemplateVisible))
        {
            employee.FaceTemplateVisible = null;
        }
        if (IsLikelyPhotoPayload(employee.FaceTemplateInfrared))
        {
            employee.FaceTemplateInfrared = null;
        }
        employee.SyncedAt = null;
        if (!repository.UpdateEmployee(employee))
        {
            skipped++;
            skippedItems.Add(new { line = rowLine, userId = code, reason = "Failed to update employee photo." });
            continue;
        }

        var index = employees.FindIndex(e => e.Id == employee.Id);
        if (index >= 0)
        {
            employees[index] = employee;
        }
        UpsertProfilePhotoSnapshot(repository, employee);
        ProgramHelpers.UpsertEmployeeProfile(
            personnelStore,
            code,
            profile?.Privilege ?? ToProfilePrivilegeLabel(employee.Privilege),
            profile?.CardNumber ?? employee.CardNumber,
            pictureDataUrl,
            profile?.MobileNumber ?? employee.MobileNumber,
            profile?.Gender ?? (string.IsNullOrWhiteSpace(employee.Gender) ? "Male" : employee.Gender),
            profile?.HireDate ?? string.Empty,
            ResolveDevicePassword(profile?.Password, employee.Password));

        updated++;
        updatedEmployees.Add(employee);
    }

    if (updated > 0)
    {
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
        var queuedPhotoCommands = QueueEmployeePhotoSyncToAssignedDevices(
            service,
            protocolService,
            personnelStore,
            updatedEmployees,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName);
        if (queuedPhotoCommands > 0)
        {
            AddLog("INFO", "Sync", $"Queued {queuedPhotoCommands} photo sync command(s) after employee photo zip import.");
            try
            {
                var activeDevices = service.ListDevices();
                foreach (var device in activeDevices)
                {
                    var devSetup = ResolveDeviceSetupForSerial(personnelStore, device.SerialNumber, device.Location, DefaultAreaCode, DefaultAreaName, DefaultDepartmentCode, DefaultDepartmentName);
                    var anyAssigned = false;
                    foreach (var emp in updatedEmployees)
                    {
                        var profile = personnelStore.Profiles.FirstOrDefault(p => string.Equals(p.EmployeeCode, emp.EmployeeCode, StringComparison.OrdinalIgnoreCase));
                        var deptName = ResolveEmployeeDepartmentForDevice(personnelStore, emp, profile, devSetup.Department, DefaultAreaCode, DefaultAreaName, DefaultDepartmentCode, DefaultDepartmentName);
                        if (!string.IsNullOrWhiteSpace(deptName))
                        {
                            anyAssigned = true;
                            break;
                        }
                    }
                    if (anyAssigned)
                    {
                        _ = TryWakeDeviceAsync(device.IpAddress, device.SerialNumber, settings.Port);
                    }
                }
            }
            catch (Exception wakeEx)
            {
                AddLog("WARN", "Sync", "Failed to wake devices during bulk photo zip import.", wakeEx.ToString());
            }
        }
    }

    return Results.Ok(new { success = true, mode = overwriteExisting ? "overwrite" : "skip", updated, skipped, skippedItems });
});

app.MapGet("/api/personnel/employees/export", (string? search, string? status, string? scope, string? ids, string? department, string? area) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var query = service.ListEmployees().AsEnumerable();
    if (!string.IsNullOrWhiteSpace(search))
    {
        var token = search.Trim();
        query = query.Where(e =>
            e.EmployeeCode.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            e.FullName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            e.Department.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    if (!string.IsNullOrWhiteSpace(status))
    {
        var s = status.Trim().ToLowerInvariant();
        if (s == "active")
        {
            query = query.Where(e => e.IsActive);
        }
        else if (s == "inactive")
        {
            query = query.Where(e => !e.IsActive);
        }
    }

    var scopeToken = (scope ?? "all").Trim().ToLowerInvariant();
    if (scopeToken == "selected")
    {
        var set = (ids ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        query = query.Where(e => set.Contains(e.EmployeeCode) || set.Contains(e.Id.ToString()));
    }
    else if (scopeToken == "department")
    {
        var token = (department ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Results.BadRequest(new { message = "department is required when scope=department." });
        }

        query = query.Where(e =>
        {
            var profile = personnelStore.Profiles.FirstOrDefault(p => string.Equals(p.EmployeeCode, e.EmployeeCode, StringComparison.OrdinalIgnoreCase));
            var assignments = GetResolvedEmployeeAssignments(
                personnelStore,
                e,
                profile,
                DefaultAreaCode,
                DefaultAreaName,
                DefaultDepartmentCode,
                DefaultDepartmentName);
            return assignments.Any(x =>
                string.Equals(x.Department.Code, token, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Department.Name, token, StringComparison.OrdinalIgnoreCase));
        });
    }
    else if (scopeToken == "area")
    {
        var token = (area ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Results.BadRequest(new { message = "area is required when scope=area." });
        }

        query = query.Where(e =>
        {
            var profile = personnelStore.Profiles.FirstOrDefault(p => string.Equals(p.EmployeeCode, e.EmployeeCode, StringComparison.OrdinalIgnoreCase));
            var assignments = GetResolvedEmployeeAssignments(
                personnelStore,
                e,
                profile,
                DefaultAreaCode,
                DefaultAreaName,
                DefaultDepartmentCode,
                DefaultDepartmentName);
            return assignments.Any(x =>
                string.Equals(x.Area.Code, token, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Area.Name, token, StringComparison.OrdinalIgnoreCase));
        });
    }

    var rows = query.OrderBy(e => e.EmployeeCode, StringComparer.OrdinalIgnoreCase).ToList();
    var csv = new StringBuilder();
    csv.AppendLine("UserId,FullName,AreaName,DepartmentName,Position,CardNumber,Password,IsActive");
    foreach (var row in rows)
    {
        static string Esc(string? value)
        {
            var s = value ?? string.Empty;
            if (s.Contains('"'))
            {
                s = s.Replace("\"", "\"\"");
            }

            return (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
                ? $"\"{s}\""
                : s;
        }

        var profile = personnelStore.Profiles.FirstOrDefault(p => string.Equals(p.EmployeeCode, row.EmployeeCode, StringComparison.OrdinalIgnoreCase));
        var assignments = GetResolvedEmployeeAssignments(
            personnelStore,
            row,
            profile,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName);
        var primary = assignments.FirstOrDefault();
        var areaName = primary.Area?.Name ?? string.Empty;
        var departmentName = primary.Department?.Name ?? row.Department;
        var cardNumber = !string.IsNullOrWhiteSpace(row.CardNumber) ? row.CardNumber : profile?.CardNumber ?? string.Empty;
        var password = ResolveUiPassword(profile?.Password);

        csv.Append(Esc(row.EmployeeCode)).Append(',')
           .Append(Esc(row.FullName)).Append(',')
           .Append(Esc(areaName)).Append(',')
           .Append(Esc(departmentName)).Append(',')
           .Append(Esc(row.Position)).Append(',')
           .Append(Esc(cardNumber)).Append(',')
           .Append(Esc(password)).Append(',')
           .Append(row.IsActive ? "true" : "false")
           .AppendLine();
    }

    return Results.Text(csv.ToString(), "text/csv");
});

app.MapGet("/api/personnel/employees/export/photos", async (string? search, string? status, string? scope, string? ids, string? department, string? area) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var query = service.ListEmployees().AsEnumerable();
    if (!string.IsNullOrWhiteSpace(search))
    {
        var token = search.Trim();
        query = query.Where(e =>
            e.EmployeeCode.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            e.FullName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            e.Department.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    if (!string.IsNullOrWhiteSpace(status))
    {
        var s = status.Trim().ToLowerInvariant();
        if (s == "active")
        {
            query = query.Where(e => e.IsActive);
        }
        else if (s == "inactive")
        {
            query = query.Where(e => !e.IsActive);
        }
    }

    var scopeToken = (scope ?? "all").Trim().ToLowerInvariant();
    if (scopeToken == "selected")
    {
        var set = (ids ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        query = query.Where(e => set.Contains(e.EmployeeCode) || set.Contains(e.Id.ToString()));
    }
    else if (scopeToken == "department")
    {
        var token = (department ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Results.BadRequest(new { message = "department is required when scope=department." });
        }

        query = query.Where(e =>
        {
            var profile = personnelStore.Profiles.FirstOrDefault(p => string.Equals(p.EmployeeCode, e.EmployeeCode, StringComparison.OrdinalIgnoreCase));
            var assignments = GetResolvedEmployeeAssignments(
                personnelStore,
                e,
                profile,
                DefaultAreaCode,
                DefaultAreaName,
                DefaultDepartmentCode,
                DefaultDepartmentName);
            return assignments.Any(x =>
                string.Equals(x.Department.Code, token, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Department.Name, token, StringComparison.OrdinalIgnoreCase));
        });
    }
    else if (scopeToken == "area")
    {
        var token = (area ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Results.BadRequest(new { message = "area is required when scope=area." });
        }

        query = query.Where(e =>
        {
            var profile = personnelStore.Profiles.FirstOrDefault(p => string.Equals(p.EmployeeCode, e.EmployeeCode, StringComparison.OrdinalIgnoreCase));
            var assignments = GetResolvedEmployeeAssignments(
                personnelStore,
                e,
                profile,
                DefaultAreaCode,
                DefaultAreaName,
                DefaultDepartmentCode,
                DefaultDepartmentName);
            return assignments.Any(x =>
                string.Equals(x.Area.Code, token, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Area.Name, token, StringComparison.OrdinalIgnoreCase));
        });
    }

    var rows = query.OrderBy(e => e.EmployeeCode, StringComparer.OrdinalIgnoreCase).ToList();
    using var archiveStream = new MemoryStream();
    using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
    {
        var manifest = new StringBuilder();
        manifest.AppendLine("UserId,FullName,FileName,Status");
        foreach (var row in rows)
        {
            var profile = personnelStore.Profiles.FirstOrDefault(p => string.Equals(p.EmployeeCode, row.EmployeeCode, StringComparison.OrdinalIgnoreCase));
            var picture = string.IsNullOrWhiteSpace(row.ProfilePhoto) ? (profile?.PictureDataUrl ?? string.Empty) : row.ProfilePhoto;
            if (!TryDecodePictureDataUrl(picture, out var bytes, out var extension) || bytes.Length == 0)
            {
                manifest.Append(EscapeCsvCell(row.EmployeeCode)).Append(',')
                    .Append(EscapeCsvCell(row.FullName)).Append(',')
                    .Append(',')
                    .AppendLine("No photo");
                continue;
            }

            var fileName = $"{SanitizeArchiveFileName(row.EmployeeCode)}{extension}";
            var entry = archive.CreateEntry(fileName, CompressionLevel.Optimal);
            await using (var entryStream = entry.Open())
            {
                await entryStream.WriteAsync(bytes);
            }

            manifest.Append(EscapeCsvCell(row.EmployeeCode)).Append(',')
                .Append(EscapeCsvCell(row.FullName)).Append(',')
                .Append(EscapeCsvCell(fileName)).Append(',')
                .AppendLine("Exported");
        }

        var manifestEntry = archive.CreateEntry("manifest.csv", CompressionLevel.Optimal);
        await using var manifestStream = manifestEntry.Open();
        await using var writer = new StreamWriter(manifestStream, Encoding.UTF8);
        await writer.WriteAsync(manifest.ToString());
    }

    return Results.File(
        archiveStream.ToArray(),
        "application/zip",
        $"employee-photos-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
});

app.MapGet("/api/personnel/employees/export/template", () =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var csv = new StringBuilder();
    csv.AppendLine("UserId,FullName,AreaName,DepartmentName,Position,CardNumber,Password,IsActive");
    csv.AppendLine("1001,Pankaj Kushwaha,Default Area,Default Department,Staff,10010001,1001,true");
    csv.AppendLine("1002,Riya Sharma,Default Area,HR,Manager,10020001,1002,true");
    return Results.Text(csv.ToString(), "text/csv");
});

app.MapGet("/api/personnel/employees/export/template/photos", () =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var csv = new StringBuilder();
    csv.AppendLine("UserId,FullName,AreaName,DepartmentName,PictureDataUrl");
    csv.AppendLine("1001,Pankaj Kushwaha,Default Area,Default Department,data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAUA");
    return Results.Text(csv.ToString(), "text/csv");
});

app.MapGet("/api/personnel/employees/export/template/photos-filenames", () =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var csv = new StringBuilder();
    csv.AppendLine("UserId,FullName,AreaName,DepartmentName,PhotoFileName");
    csv.AppendLine("1001,Pankaj Kushwaha,Default Area,Default Department,1001.jpg");
    return Results.Text(csv.ToString(), "text/csv");
});

List<object> BuildAreaSetupRows()
{
    return personnelStore.Areas
        .OrderBy(a => a.Code, StringComparer.OrdinalIgnoreCase)
        .Select(a => (object)new
        {
            a.Id,
            a.Code,
            a.Name,
            a.Remark,
            isDefault = IsDefaultArea(a.Code, DefaultAreaCode),
            canDelete = !IsDefaultArea(a.Code, DefaultAreaCode)
        })
        .ToList();
}

List<object> BuildDepartmentSetupRows(IReadOnlyList<Employee> employees)
{
    var areaByCode = personnelStore.Areas.ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);
    return personnelStore.Departments
        .OrderBy(d => d.Code, StringComparer.OrdinalIgnoreCase)
        .Select(d =>
        {
            var empInDept = employees.Where(e =>
            {
                var profile = personnelStore.Profiles.FirstOrDefault(p =>
                    string.Equals(p.EmployeeCode, e.EmployeeCode, StringComparison.OrdinalIgnoreCase));
                return GetResolvedEmployeeAssignments(
                        personnelStore,
                        e,
                        profile,
                        DefaultAreaCode,
                        DefaultAreaName,
                        DefaultDepartmentCode,
                        DefaultDepartmentName)
                    .Any(x => string.Equals(x.Department.Code, d.Code, StringComparison.OrdinalIgnoreCase));
            }).ToList();
            var areaName = areaByCode.TryGetValue(d.AreaCode, out var area) ? area.Name : string.Empty;
            return (object)new
            {
                d.Id,
                d.Code,
                d.Name,
                d.AreaCode,
                areaName,
                d.Superior,
                employeeQty = empInDept.Count(e => e.IsActive),
                resignedQty = empInDept.Count(e => !e.IsActive),
                d.Manager,
                isDefault = IsDefaultDepartment(d, DefaultAreaCode, DefaultDepartmentCode)
            };
        })
        .ToList();
}

List<object> BuildPositionSetupRows(IReadOnlyList<Employee> employees)
{
    NormalizePositionsToSetup(
        personnelStore,
        DefaultAreaCode,
        DefaultAreaName,
        DefaultDepartmentCode,
        DefaultDepartmentName);

    return personnelStore.Positions
        .OrderBy(p => p.Code, StringComparer.OrdinalIgnoreCase)
        .Select(p => (object)new
        {
            p.Id,
            p.Code,
            p.Name,
            p.Department,
            employeeQty = employees.Count(e =>
            {
                if (!e.IsActive)
                {
                    return false;
                }

                var profile = personnelStore.Profiles.FirstOrDefault(x =>
                    string.Equals(x.EmployeeCode, e.EmployeeCode, StringComparison.OrdinalIgnoreCase));
                return GetResolvedEmployeeAssignments(
                        personnelStore,
                        e,
                        profile,
                        DefaultAreaCode,
                        DefaultAreaName,
                        DefaultDepartmentCode,
                        DefaultDepartmentName)
                    .Any(x => string.Equals(x.Department.Name, p.Department, StringComparison.OrdinalIgnoreCase));
            })
        })
        .ToList();
}

app.MapGet("/api/personnel/setup", () =>
{
    var employees = service.ListEmployees().ToList();
    return Results.Json(new
    {
        areas = BuildAreaSetupRows(),
        departments = BuildDepartmentSetupRows(employees),
        positions = BuildPositionSetupRows(employees)
    });
});

app.MapGet("/api/personnel/departments", (int page = 1, int pageSize = 200) =>
{
    var employees = service.ListEmployees().ToList();
    var allRows = BuildDepartmentSetupRows(employees);
    var totalCount = allRows.Count;
    var items = allRows.Skip((page - 1) * pageSize).Take(pageSize).ToList();
    return Results.Ok(new { totalCount, page, pageSize, items });
});

app.MapPost("/api/personnel/departments", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<DepartmentRequest>();
    if (payload is null || string.IsNullOrWhiteSpace(payload.Code) || string.IsNullOrWhiteSpace(payload.Name))
    {
        return Results.BadRequest(new { message = "Code and name are required." });
    }

    if (personnelStore.Departments.Any(d => string.Equals(d.Code, payload.Code, StringComparison.OrdinalIgnoreCase)))
    {
        return Results.BadRequest(new { message = "Department code already exists." });
    }

    var areaCode = string.IsNullOrWhiteSpace(payload.AreaCode) ? DefaultAreaCode : payload.AreaCode.Trim();
    var area = personnelStore.Areas.FirstOrDefault(a => string.Equals(a.Code, areaCode, StringComparison.OrdinalIgnoreCase));
    if (area is null)
    {
        return Results.BadRequest(new { message = "Area not found for department." });
    }

    var dept = new PersonnelDepartment(
        Guid.NewGuid(),
        payload.Code.Trim(),
        payload.Name.Trim(),
        payload.Superior?.Trim() ?? string.Empty,
        payload.Manager?.Trim() ?? string.Empty,
        area.Code);
    personnelStore.Departments.Add(dept);
    SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    _ = Task.Run(async () =>
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var scopedSync = scope.ServiceProvider.GetRequiredService<ConfigSyncService>();
            await EnsureDefaultShiftAssignmentsForAllDepartments(postgresConnectionString, personnelStore, scopedSync);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Department post-create defaults error: {ex.Message}");
        }
    });
    return Results.Json(dept);
});

app.MapPut("/api/personnel/departments/{id:guid}", async (Guid id, HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<DepartmentRequest>();
    if (payload is null || string.IsNullOrWhiteSpace(payload.Code) || string.IsNullOrWhiteSpace(payload.Name))
    {
        return Results.BadRequest(new { message = "Code and name are required." });
    }

    var idx = personnelStore.Departments.FindIndex(d => d.Id == id);
    if (idx < 0)
    {
        return Results.NotFound(new { message = "Department not found." });
    }

    if (personnelStore.Departments.Any(d => d.Id != id && string.Equals(d.Code, payload.Code, StringComparison.OrdinalIgnoreCase)))
    {
        return Results.BadRequest(new { message = "Department code already exists." });
    }

    var existing = personnelStore.Departments[idx];
    // ID/code is immutable after creation — preserve the existing code; only name/details change.
    var requestedCode = existing.Code;
    var requestedName = payload.Name.Trim();
    if (IsDefaultDepartment(existing, DefaultAreaCode, DefaultDepartmentCode) &&
        !string.Equals(requestedCode, existing.Code, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = "Default Department code cannot be changed." });
    }

    var areaCode = string.IsNullOrWhiteSpace(payload.AreaCode) ? existing.AreaCode : payload.AreaCode.Trim();
    if (string.IsNullOrWhiteSpace(areaCode))
    {
        areaCode = DefaultAreaCode;
    }

    var area = personnelStore.Areas.FirstOrDefault(a => string.Equals(a.Code, areaCode, StringComparison.OrdinalIgnoreCase));
    if (area is null)
    {
        return Results.BadRequest(new { message = "Area not found for department." });
    }

    personnelStore.Departments[idx] = personnelStore.Departments[idx] with
    {
        Code = requestedCode,
        Name = requestedName,
        Superior = payload.Superior?.Trim() ?? string.Empty,
        Manager = payload.Manager?.Trim() ?? string.Empty,
        AreaCode = area.Code
    };

    for (var i = 0; i < personnelStore.DeviceAssignments.Count; i++)
    {
        var assignment = personnelStore.DeviceAssignments[i];
        if (!string.Equals(assignment.DepartmentCode, existing.Code, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        personnelStore.DeviceAssignments[i] = assignment with
        {
            AreaCode = area.Code,
            DepartmentCode = requestedCode
        };
    }

    for (var i = 0; i < personnelStore.Profiles.Count; i++)
    {
        var profile = personnelStore.Profiles[i];
        var rawAssignments = new List<PersonnelEmployeeAssignment>(profile.Assignments ?? []);
        if (!string.IsNullOrWhiteSpace(profile.DepartmentCode))
        {
            rawAssignments.Add(new PersonnelEmployeeAssignment(profile.AreaCode, profile.DepartmentCode));
        }

        var changedProfile = false;
        for (var assignmentIndex = 0; assignmentIndex < rawAssignments.Count; assignmentIndex++)
        {
            var assignment = rawAssignments[assignmentIndex];
            if (!string.Equals(assignment.DepartmentCode, existing.Code, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rawAssignments[assignmentIndex] = new PersonnelEmployeeAssignment(area.Code, requestedCode);
            changedProfile = true;
        }

        if (!changedProfile)
        {
            continue;
        }

        var normalizedAssignments = NormalizeEmployeeAssignmentList(rawAssignments);
        if (normalizedAssignments.Count == 0)
        {
            normalizedAssignments = [new PersonnelEmployeeAssignment(area.Code, requestedCode)];
        }

        personnelStore.Profiles[i] = profile with
        {
            AreaCode = normalizedAssignments[0].AreaCode,
            DepartmentCode = normalizedAssignments[0].DepartmentCode,
            Assignments = normalizedAssignments
        };
    }

    if (!string.Equals(existing.Name, requestedName, StringComparison.OrdinalIgnoreCase))
    {
        foreach (var emp in service.ListEmployees())
        {
            if (!string.Equals(emp.Department, existing.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            service.UpdateEmployeeDepartment(emp.Id, requestedName);
        }

        for (var i = 0; i < personnelStore.Positions.Count; i++)
        {
            var position = personnelStore.Positions[i];
            if (string.Equals(position.Department, existing.Name, StringComparison.OrdinalIgnoreCase))
            {
                personnelStore.Positions[i] = position with { Department = requestedName };
            }
        }
    }

    SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    return Results.Ok(new { success = true });
});

app.MapDelete("/api/personnel/departments/{id:guid}", (Guid id) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var target = personnelStore.Departments.FirstOrDefault(d => d.Id == id);
    if (target is null)
    {
        return Results.NotFound(new { message = "Department not found." });
    }

    if (IsDefaultDepartment(target, DefaultAreaCode, DefaultDepartmentCode))
    {
        return Results.BadRequest(new { message = "Default Department cannot be deleted." });
    }

    var mappedDeviceCount = personnelStore.DeviceAssignments
        .Count(x => string.Equals(x.DepartmentCode, target.Code, StringComparison.OrdinalIgnoreCase));
    if (mappedDeviceCount > 0)
    {
        return Results.BadRequest(new { message = $"Department is assigned to {mappedDeviceCount} device(s)." });
    }

    var removed = personnelStore.Departments.RemoveAll(d => d.Id == id);
    SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    return Results.Ok(new { success = true });
});

app.MapPost("/api/personnel/departments/delete", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<DepartmentDeleteRequest>();
    if (payload is null || payload.Ids.Count == 0)
    {
        return Results.BadRequest(new { message = "Select departments first." });
    }

    var set = payload.Ids.ToHashSet();
    var protectedIds = personnelStore.Departments
        .Where(d => set.Contains(d.Id))
        .Where(d =>
            IsDefaultDepartment(d, DefaultAreaCode, DefaultDepartmentCode) ||
            personnelStore.DeviceAssignments.Any(a => string.Equals(a.DepartmentCode, d.Code, StringComparison.OrdinalIgnoreCase)))
        .Select(d => d.Id)
        .ToHashSet();

    var removed = personnelStore.Departments.RemoveAll(d => set.Contains(d.Id) && !protectedIds.Contains(d.Id));
    SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    return Results.Ok(new { success = true, removed, skipped = protectedIds.Count });
});

app.MapPost("/api/personnel/departments/import", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<DepartmentImportRequest>();
    if (payload is null || payload.Items.Count == 0)
    {
        return Results.BadRequest(new { message = "No items to import." });
    }

    var added = 0;
    foreach (var item in payload.Items)
    {
        if (string.IsNullOrWhiteSpace(item.Code) || string.IsNullOrWhiteSpace(item.Name))
        {
            continue;
        }

        if (personnelStore.Departments.Any(d => string.Equals(d.Code, item.Code, StringComparison.OrdinalIgnoreCase)))
        {
            continue;
        }

        var areaCode = string.IsNullOrWhiteSpace(item.AreaCode) ? DefaultAreaCode : item.AreaCode.Trim();
        var areaExists = personnelStore.Areas.Any(a => string.Equals(a.Code, areaCode, StringComparison.OrdinalIgnoreCase));
        if (!areaExists)
        {
            areaCode = DefaultAreaCode;
        }

        personnelStore.Departments.Add(new PersonnelDepartment(
            Guid.NewGuid(),
            item.Code.Trim(),
            item.Name.Trim(),
            item.Superior?.Trim() ?? string.Empty,
            item.Manager?.Trim() ?? string.Empty,
            areaCode));
        added++;
    }

    SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    return Results.Ok(new { success = true, added });
});

app.MapPost("/api/personnel/departments/transfer", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<DepartmentTransferRequest>();
    if (payload is null || string.IsNullOrWhiteSpace(payload.FromDepartment) || string.IsNullOrWhiteSpace(payload.ToDepartment))
    {
        return Results.BadRequest(new { message = "From and to departments are required." });
    }

    var fromName = payload.FromDepartment.Trim();
    var toName = payload.ToDepartment.Trim();
    if (string.Equals(fromName, toName, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = "From and To cannot be same." });
    }

    var fromDepartment = ResolveDepartmentFromSetup(
        personnelStore,
        fromName,
        DefaultAreaCode,
        DefaultAreaName,
        DefaultDepartmentCode,
        DefaultDepartmentName);
    var toDepartment = ResolveDepartmentFromSetup(
        personnelStore,
        toName,
        DefaultAreaCode,
        DefaultAreaName,
        DefaultDepartmentCode,
        DefaultDepartmentName);
    var moved = 0;
    foreach (var emp in repository.GetEmployees())
    {
        var profile = personnelStore.Profiles.FirstOrDefault(p =>
            string.Equals(p.EmployeeCode, emp.EmployeeCode, StringComparison.OrdinalIgnoreCase));
        var assignments = GetResolvedEmployeeAssignments(
            personnelStore,
            emp,
            profile,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName)
            .Select(x => x.Assignment)
            .ToList();
        if (!assignments.Any(x => string.Equals(x.DepartmentCode, fromDepartment.Code, StringComparison.OrdinalIgnoreCase)))
        {
            continue;
        }

        var replacedAssignments = NormalizeEmployeeAssignmentList(assignments
            .Select(x => string.Equals(x.DepartmentCode, fromDepartment.Code, StringComparison.OrdinalIgnoreCase)
                ? new PersonnelEmployeeAssignment(toDepartment.AreaCode, toDepartment.Code)
                : x));
        if (ApplyEmployeeAssignments(
                repository,
                personnelStore,
                emp,
                replacedAssignments,
                DefaultAreaCode,
                DefaultAreaName,
                DefaultDepartmentCode,
                DefaultDepartmentName))
        {
            moved++;
        }
    }

    if (moved > 0)
    {
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    }

    return Results.Ok(new { success = true, moved });
});

app.MapGet("/api/personnel/positions", () =>
{
    var employees = service.ListEmployees().ToList();
    return Results.Json(BuildPositionSetupRows(employees));
});

app.MapPost("/api/personnel/positions", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<PositionRequest>();
    if (payload is null || string.IsNullOrWhiteSpace(payload.Code) || string.IsNullOrWhiteSpace(payload.Name))
    {
        return Results.BadRequest(new { message = "Code and name are required." });
    }

    if (personnelStore.Positions.Any(p => string.Equals(p.Code, payload.Code, StringComparison.OrdinalIgnoreCase)))
    {
        return Results.BadRequest(new { message = "Position code already exists." });
    }

    var item = new PersonnelPosition(
        Guid.NewGuid(),
        payload.Code.Trim(),
        payload.Name.Trim(),
        ResolveDepartmentNameFromSetup(
            personnelStore,
            payload.Department,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName));
    personnelStore.Positions.Add(item);
    SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    return Results.Json(item);
});

app.MapPut("/api/personnel/positions/{id:guid}", async (Guid id, HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<PositionRequest>();
    if (payload is null || string.IsNullOrWhiteSpace(payload.Code) || string.IsNullOrWhiteSpace(payload.Name))
    {
        return Results.BadRequest(new { message = "Code and name are required." });
    }

    var idx = personnelStore.Positions.FindIndex(x => x.Id == id);
    if (idx < 0)
    {
        return Results.NotFound(new { message = "Position not found." });
    }

    if (personnelStore.Positions.Any(x => x.Id != id && string.Equals(x.Code, payload.Code, StringComparison.OrdinalIgnoreCase)))
    {
        return Results.BadRequest(new { message = "Position code already exists." });
    }

    personnelStore.Positions[idx] = personnelStore.Positions[idx] with
    {
        Code = payload.Code.Trim(),
        Name = payload.Name.Trim(),
        Department = ResolveDepartmentNameFromSetup(
            personnelStore,
            payload.Department,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName)
    };
    SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    return Results.Ok(new { success = true });
});

app.MapDelete("/api/personnel/positions/{id:guid}", (Guid id) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var removed = personnelStore.Positions.RemoveAll(x => x.Id == id);
    if (removed == 0)
    {
        return Results.NotFound(new { message = "Position not found." });
    }

    SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    return Results.Ok(new { success = true });
});

app.MapPost("/api/personnel/positions/delete", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<PositionDeleteRequest>();
    if (payload is null || payload.Ids.Count == 0)
    {
        return Results.BadRequest(new { message = "Select positions first." });
    }

    var set = payload.Ids.ToHashSet();
    var removed = personnelStore.Positions.RemoveAll(x => set.Contains(x.Id));
    SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    return Results.Ok(new { success = true, removed });
});

app.MapGet("/api/personnel/areas", () =>
{
    return Results.Json(BuildAreaSetupRows());
});

app.MapPost("/api/personnel/areas", async (HttpRequest request, ConfigSyncService syncService) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<AreaRequest>();
    if (payload is null || string.IsNullOrWhiteSpace(payload.Code) || string.IsNullOrWhiteSpace(payload.Name))
    {
        return Results.BadRequest(new { message = "Code and name are required." });
    }

    if (personnelStore.Areas.Any(a => string.Equals(a.Code, payload.Code, StringComparison.OrdinalIgnoreCase)))
    {
        return Results.BadRequest(new { message = "Area code already exists." });
    }

    var item = new PersonnelArea(Guid.NewGuid(), payload.Code.Trim(), payload.Name.Trim(), payload.Remark?.Trim() ?? string.Empty);
    personnelStore.Areas.Add(item);

    // Auto-create a matching department for the new area (department code == area code)
    if (!personnelStore.Departments.Any(d => string.Equals(d.Code, item.Code, StringComparison.OrdinalIgnoreCase)))
    {
        var dept = new PersonnelDepartment(
            Guid.NewGuid(),
            item.Code,
            $"{item.Name} Department",
            string.Empty,
            string.Empty,
            item.Code);
        personnelStore.Departments.Add(dept);
    }

    SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    // Post-create defaults can sync devices and should never make a completed area create look
    // failed to the UI. The read/save paths also repair these defaults if this background pass lags.
    _ = Task.Run(async () =>
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var scopedSync = scope.ServiceProvider.GetRequiredService<ConfigSyncService>();
            await EnsureAccessControlDefaultsForAllAreas(postgresConnectionString, service, personnelStore, scopedSync);
            await EnsureDefaultShiftAssignmentsForAllDepartments(postgresConnectionString, personnelStore, scopedSync);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Area post-create defaults error: {ex.Message}");
        }
    });
    return Results.Json(item);
});

app.MapPut("/api/personnel/areas/{id:guid}", async (Guid id, HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<AreaRequest>();
    if (payload is null || string.IsNullOrWhiteSpace(payload.Code) || string.IsNullOrWhiteSpace(payload.Name))
    {
        return Results.BadRequest(new { message = "Code and name are required." });
    }

    var idx = personnelStore.Areas.FindIndex(x => x.Id == id);
    if (idx < 0)
    {
        return Results.NotFound(new { message = "Area not found." });
    }

    if (personnelStore.Areas.Any(x => x.Id != id && string.Equals(x.Code, payload.Code, StringComparison.OrdinalIgnoreCase)))
    {
        return Results.BadRequest(new { message = "Area code already exists." });
    }

    var existing = personnelStore.Areas[idx];
    // ID/code is immutable after creation — preserve the existing code; only name/details change.
    var requestedCode = existing.Code;
    if (IsDefaultArea(existing.Code, DefaultAreaCode) &&
        !string.Equals(requestedCode, existing.Code, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = "Default Area code cannot be changed." });
    }

    personnelStore.Areas[idx] = personnelStore.Areas[idx] with
    {
        Code = requestedCode,
        Name = payload.Name.Trim(),
        Remark = payload.Remark?.Trim() ?? string.Empty
    };
    SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    return Results.Ok(new { success = true });
});

app.MapDelete("/api/personnel/areas/{id:guid}", (Guid id) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var target = personnelStore.Areas.FirstOrDefault(x => x.Id == id);
    if (target is null)
    {
        return Results.NotFound(new { message = "Area not found." });
    }

    if (IsDefaultArea(target.Code, DefaultAreaCode))
    {
        return Results.BadRequest(new { message = "Default Area cannot be deleted." });
    }

    var result = DeleteAreaCascade(
        personnelStore,
        repository,
        service,
        target,
        personnelPath,
        postgresConnectionString,
        DefaultAreaCode,
        DefaultAreaName,
        DefaultDepartmentCode,
        DefaultDepartmentName);
    AddLog(
        "WARN",
        "Personnel",
        $"Deleted area {target.Code} - {target.Name} with all mapped setup data.",
        $"departments={result.DepartmentsDeleted}; devices={result.DevicesDeleted}; employees={result.EmployeesDeleted}; positions={result.PositionsDeleted}; deviceDeleteCommands={result.QueuedDeviceDeletes}; artifactRowsDeleted={result.DeletedArtifactRows}");

    SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    return Results.Ok(new
    {
        success = true,
        removed = result.AreasDeleted,
        departmentsDeleted = result.DepartmentsDeleted,
        devicesDeleted = result.DevicesDeleted,
        employeesDeleted = result.EmployeesDeleted,
        positionsDeleted = result.PositionsDeleted,
        queuedDeviceDeletes = result.QueuedDeviceDeletes,
        deletedArtifactRows = result.DeletedArtifactRows
    });
});

app.MapPost("/api/personnel/areas/delete", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<AreaDeleteRequest>();
    if (payload is null || payload.Ids.Count == 0)
    {
        return Results.BadRequest(new { message = "Select areas first." });
    }

    var set = payload.Ids.ToHashSet();
    var targets = personnelStore.Areas.Where(a => set.Contains(a.Id)).ToList();
    var skippedDefault = 0;
    var total = new AreaCascadeDeleteResult();
    foreach (var area in targets)
    {
        if (IsDefaultArea(area.Code, DefaultAreaCode))
        {
            skippedDefault++;
            continue;
        }

        var result = DeleteAreaCascade(
            personnelStore,
            repository,
            service,
            area,
            personnelPath,
            postgresConnectionString,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName);
        total.Add(result);
    }

    if (total.AreasDeleted > 0)
    {
        AddLog(
            "WARN",
            "Personnel",
            $"Bulk area delete completed. Removed areas: {total.AreasDeleted}.",
            $"departments={total.DepartmentsDeleted}; devices={total.DevicesDeleted}; employees={total.EmployeesDeleted}; positions={total.PositionsDeleted}; skippedDefault={skippedDefault}; deviceDeleteCommands={total.QueuedDeviceDeletes}; artifactRowsDeleted={total.DeletedArtifactRows}");
    }

    SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
    return Results.Ok(new
    {
        success = true,
        removed = total.AreasDeleted,
        skipped = skippedDefault,
        departmentsDeleted = total.DepartmentsDeleted,
        devicesDeleted = total.DevicesDeleted,
        employeesDeleted = total.EmployeesDeleted,
        positionsDeleted = total.PositionsDeleted,
        queuedDeviceDeletes = total.QueuedDeviceDeletes,
        deletedArtifactRows = total.DeletedArtifactRows
    });
});

app.MapGet("/api/personnel/holidays", () =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var rows = LoadHolidayRows(postgresConnectionString);
    return Results.Json(rows);
});

app.MapPost("/api/personnel/holidays", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<HolidayRequest>();
    if (payload is null || string.IsNullOrWhiteSpace(payload.HolidayDate) || string.IsNullOrWhiteSpace(payload.HolidayName))
    {
        return Results.BadRequest(new { message = "Holiday date and name are required." });
    }

    if (!DateOnly.TryParseExact(payload.HolidayDate.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var holidayDate))
    {
        return Results.BadRequest(new { message = "Holiday date must be in YYYY-MM-DD format." });
    }

    var holidayName = payload.HolidayName.Trim();
    if (string.IsNullOrWhiteSpace(holidayName))
    {
        return Results.BadRequest(new { message = "Holiday name is required." });
    }

    if (HolidayExists(postgresConnectionString, holidayDate))
    {
        return Results.BadRequest(new { message = "A holiday already exists on this date." });
    }

    try
    {
        var created = CreateHoliday(
            postgresConnectionString,
            holidayDate,
            holidayName,
            payload.Description?.Trim() ?? string.Empty,
            payload.IsRecurring,
            payload.IsActive);
        return Results.Json(created);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPut("/api/personnel/holidays/{id:int}", async (int id, HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<HolidayRequest>();
    if (payload is null || string.IsNullOrWhiteSpace(payload.HolidayDate) || string.IsNullOrWhiteSpace(payload.HolidayName))
    {
        return Results.BadRequest(new { message = "Holiday date and name are required." });
    }

    if (!DateOnly.TryParseExact(payload.HolidayDate.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var holidayDate))
    {
        return Results.BadRequest(new { message = "Holiday date must be in YYYY-MM-DD format." });
    }

    var holidayName = payload.HolidayName.Trim();
    if (string.IsNullOrWhiteSpace(holidayName))
    {
        return Results.BadRequest(new { message = "Holiday name is required." });
    }

    if (HolidayExists(postgresConnectionString, holidayDate, id))
    {
        return Results.BadRequest(new { message = "A holiday already exists on this date." });
    }

    try
    {
        var updated = UpdateHoliday(
            postgresConnectionString,
            id,
            holidayDate,
            holidayName,
            payload.Description?.Trim() ?? string.Empty,
            payload.IsRecurring,
            payload.IsActive);
        return updated is null
            ? Results.NotFound(new { message = "Holiday not found." })
            : Results.Json(updated);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapDelete("/api/personnel/holidays/{id:int}", (int id) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var removed = DeleteHoliday(postgresConnectionString, id);
    return removed
        ? Results.Ok(new { success = true })
        : Results.NotFound(new { message = "Holiday not found." });
});

app.MapGet("/api/devices", (string? search, int page = 1, int pageSize = 200) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var allDevices = service.SearchDevices(search).ToList();
    var totalCount = allDevices.Count;
    var devices = allDevices.Skip((page - 1) * pageSize).Take(pageSize).ToList();
    var txBySerial = LoadDevicePunchCounts(postgresConnectionString, devices.Select(d => d.SerialNumber));

    var items = devices.Select(d =>
    {
        discoveredDevices.TryGetValue(d.SerialNumber, out var discovered);
        var isOnline = IsDeviceCurrentlyOnline(d.LastSeenUtc, d.IsOnline);
        txBySerial.TryGetValue(d.SerialNumber, out var deviceTx);
        var setup = ResolveDeviceSetupForSerial(
            personnelStore,
            d.SerialNumber,
            d.Location,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName);

        // Punch Logs in Device Manager reflects actual hardware count currently on the device
        var transactionQty = Math.Max(0, discovered?.TransactionQty ?? 0);

        var maxUserQty = 10000;
        var maxFpQty = 10000;
        var maxFaceQty = 6000;
        var maxPalmQty = 3000;
        var maxTransactionQty = 200000;

        try
        {
            if (!string.IsNullOrWhiteSpace(d.CapabilitiesJson))
            {
                using var doc = JsonDocument.Parse(d.CapabilitiesJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("max_user_qty", out var mu) && mu.TryGetInt32(out var muv)) maxUserQty = muv;
                else if (root.TryGetProperty("max_user_count", out var mu2) && mu2.TryGetInt32(out var mu2v)) maxUserQty = mu2v;

                if (root.TryGetProperty("max_att_log_qty", out var mt) && mt.TryGetInt32(out var mtv)) maxTransactionQty = mtv;
                else if (root.TryGetProperty("max_att_log_count", out var mt2) && mt2.TryGetInt32(out var mt2v)) maxTransactionQty = mt2v;

                if (root.TryGetProperty("max_templates", out var maxTemplates))
                {
                    if (maxTemplates.TryGetProperty("1", out var fpCap) && fpCap.TryGetInt32(out var fpCapVal)) maxFpQty = fpCapVal;
                    if (maxTemplates.TryGetProperty("9", out var faceCap) && faceCap.TryGetInt32(out var faceCapVal)) maxFaceQty = faceCapVal;
                    else if (maxTemplates.TryGetProperty("2", out var faceCapNir) && faceCapNir.TryGetInt32(out var faceCapNirVal)) maxFaceQty = faceCapNirVal;
                    if (maxTemplates.TryGetProperty("8", out var palmCap) && palmCap.TryGetInt32(out var palmCapVal)) maxPalmQty = palmCapVal;
                    else if (maxTemplates.TryGetProperty("10", out var palmCapVal2) && palmCapVal2.TryGetInt32(out var palmCapVal2Val)) maxPalmQty = palmCapVal2Val;
                }
            }
        }
        catch { }

        var rawMaxTemplates = ParseIndexedIntegerMap(d.MaxMultiBioDataCount);
        if (rawMaxTemplates.TryGetValue("1", out var fCap) && fCap > 0) maxFpQty = fCap;
        if (rawMaxTemplates.TryGetValue("9", out var faCap) && faCap > 0) maxFaceQty = faCap;
        else if (rawMaxTemplates.TryGetValue("2", out var faCapNir) && faCapNir > 0) maxFaceQty = faCapNir;
        if (rawMaxTemplates.TryGetValue("8", out var pCap) && pCap > 0) maxPalmQty = pCap;
        else if (rawMaxTemplates.TryGetValue("10", out var pCap2) && pCap2 > 0) maxPalmQty = pCap2;

        return new
        {
            d.Id,
            d.SerialNumber,
            d.Name,
            model = d.Model ?? string.Empty,
            d.IpAddress,
            area = setup.Area.Name,
            areaCode = setup.Area.Code,
            location = setup.Area.Name,
            department = setup.Department.Name,
            departmentCode = setup.Department.Code,
            d.TimeZone,
            d.SupportsVisibleLightFace,
            d.SupportsInfraredFace,
            d.SupportsFingerprint,
            d.SupportsPalm,
            d.SupportsFingerVein,
            d.FaceAlgorithmVersion,
            d.FingerprintAlgorithmVersion,
            d.FirmwareVersion,
            d.DeviceType,
            d.PushVer,
            d.Language,
            d.MultiBioDataSupport,
            d.MultiBioPhotoSupport,
            d.MultiBioVersion,
            d.MaxMultiBioDataCount,
            MaxMultiBioVersionNumber = d.MaxMultiBioDataCount, // Map to existing field for compatibility
            d.MaxMultiBioPhotoCount,
            d.FingerFunOn,
            d.FaceFunOn,
            d.VisiLightFunOn,
            d.BioDataFun,
            d.BioPhotoFun,
            d.CapabilitiesJson,
            d.CreatedAtUtc,
            d.UpdatedAtUtc,
            d.IsRegistrationDevice,
            state = isOnline ? "Online" : "Offline",
            IsOnline = isOnline,
            d.LastSeenUtc,
            userQty = discovered?.UserQty ?? 0,
            fpQty = discovered?.FpQty ?? 0,
            faceQty = discovered?.FaceQty ?? 0,
            palmQty = discovered?.PalmQty ?? 0,
            transactionQty = transactionQty,
            maxUserQty,
            maxFpQty,
            maxFaceQty,
            maxPalmQty,
            maxTransactionQty,
            cmdQty = service.GetPendingCommands(d.SerialNumber, 200).Count
        };
    });
    return Results.Ok(new { totalCount, page, pageSize, items });
});

app.MapGet("/api/devices/{serial}/transactions/last-punch", (string serial) =>
{
    if (string.IsNullOrWhiteSpace(serial))
    {
        return Results.BadRequest(new { message = "Device serial is required." });
    }

    var latest = LoadLatestPunchTimeForDevice(postgresConnectionString, serial.Trim());
    return Results.Ok(new { serial = serial.Trim(), lastPunchTime = latest });
});

app.MapGet("/api/device", () =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var items = service.ListDevices().Select(d => new
    {
        d.Id,
        serialNumber = d.SerialNumber,
        deviceName = d.Name,
        status = IsDeviceCurrentlyOnline(d.LastSeenUtc, d.IsOnline) ? "Online" : "Offline",
        supportsVisibleLightFace = d.SupportsVisibleLightFace,
        supportsInfraredFace = d.SupportsInfraredFace,
        supportsFingerprint = d.SupportsFingerprint,
        supportsPalm = d.SupportsPalm,
        supportsFingerVein = d.SupportsFingerVein,
        faceAlgorithmVersion = d.FaceAlgorithmVersion,
        fingerprintAlgorithmVersion = d.FingerprintAlgorithmVersion,
        firmwareVersion = d.FirmwareVersion,
        deviceType = d.DeviceType,
        pushVer = d.PushVer,
        language = d.Language,
        multiBioDataSupport = d.MultiBioDataSupport,
        multiBioPhotoSupport = d.MultiBioPhotoSupport,
        multiBioVersion = d.MultiBioVersion,
        maxMultiBioDataCount = d.MaxMultiBioDataCount,
        maxMultiBioPhotoCount = d.MaxMultiBioPhotoCount,
        fingerFunOn = d.FingerFunOn,
        faceFunOn = d.FaceFunOn,
        visiLightFunOn = d.VisiLightFunOn,
        bioDataFun = d.BioDataFun,
        bioPhotoFun = d.BioPhotoFun,
        timeZone = d.TimeZone,
        capabilitiesJson = d.CapabilitiesJson,
        createdAtUtc = d.CreatedAtUtc,
        updatedAtUtc = d.UpdatedAtUtc
    });

    return Results.Json(items);
});

app.MapGet("/api/devices/discovered", (string? search) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var addedSerials = service.ListDevices().Select(d => d.SerialNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var query = search?.Trim();
    // "Available to add" must mean actually reachable right now — devices heartbeat every
    // 5-60s while online, so 15 minutes let devices that went offline long ago still show up.
    // Match the same 2-minute freshness bar used elsewhere for "is this device alive".
    var cutoff = DateTime.UtcNow.AddMinutes(-2);
    var items = discoveredDevices.Values
        .Where(d => d.LastActivityUtc >= cutoff)
        .Where(d => !addedSerials.Contains(d.SerialNumber))
        .Where(d => string.IsNullOrWhiteSpace(query) ||
                    d.SerialNumber.Contains(query!, StringComparison.OrdinalIgnoreCase) ||
                    d.Name.Contains(query!, StringComparison.OrdinalIgnoreCase) ||
                    d.IpAddress.Contains(query!, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(d => d.LastActivityUtc)
        // IsOnline is always false for unregistered devices — "connected" status only applies to added devices
        .Select(d => d with { IsOnline = false })
        .ToList();

    return Results.Json(items);
});

app.MapPost("/api/devices/discovered/{serial}/add", (string serial) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        var currentState = licenseState;
        var currentDeviceCount = service.ListDevices().Count();
        var allowedDevices = ResolveEffectiveMaxDevices(currentState);
        if (allowedDevices >= 0 && currentDeviceCount >= allowedDevices)
        {
            return Results.BadRequest(new
            {
                code = "device_limit_reached",
                message = $"Device limit reached. Allowed: {allowedDevices}, current: {currentDeviceCount}.",
                allowedDevices,
                currentDeviceCount,
                activationMode = currentState.ActivationMode,
                licenseType = currentState.IsTrialActive ? "Trial" : currentState.Validation.LicenseType
            });
        }

        if (!discoveredDevices.TryGetValue(serial, out var d))
        {
            return Results.NotFound(new { message = "Discovered device not found." });
        }

        var setup = ResolveDeviceSetup(
            personnelStore,
            areaCodeInput: null,
            areaNameInput: d.Area,
            departmentCodeInput: null,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName);
        var created = service.RegisterDevice(d.SerialNumber, d.Name, d.IpAddress, setup.Area.Name, d.Model);
        var sqlIds = SyncSqlAreaAndDepartment(repository, setup.Area.Name, setup.Department.Name);
        created.AreaId = sqlIds.AreaId;
        created.DepartmentId = sqlIds.DepartmentId;
        // RegisterDevice always defaults a brand-new device row to offline/never-seen, even though a
        // "discovered" device (this path) was by definition already polling the server moments ago.
        // Carry that known state over so the dashboard doesn't show a false "offline" flicker until
        // the device's next heartbeat arrives a few seconds later.
        created.IsOnline = d.IsOnline;
        created.Status = d.IsOnline ? DeviceStatus.Online : DeviceStatus.Offline;
        created.LastSeenUtc = d.LastActivityUtc;
        repository.UpdateDevice(created);
        UpsertDeviceAssignment(personnelStore, created.SerialNumber, setup.Area.Code, setup.Department.Code);
        PersistDeviceAssignmentToDb(postgresConnectionString, created.SerialNumber, setup.Area.Code, setup.Department.Code);
        AddLog("INFO", "Device", $"Discovered device added: {created.SerialNumber} ({created.Name}).");
        return Results.Json(created);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[API] Error adding discovered device {serial}: {ex}");
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

app.MapPost("/api/devices", async (HttpRequest request, ConfigSyncService syncService) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var currentState = licenseState;
    var currentDeviceCount = service.ListDevices().Count();
    var allowedDevices = ResolveEffectiveMaxDevices(currentState);
    if (allowedDevices >= 0 && currentDeviceCount >= allowedDevices)
    {
        return Results.BadRequest(new
        {
            code = "device_limit_reached",
            message = $"Device limit reached. Allowed: {allowedDevices}, current: {currentDeviceCount}.",
            allowedDevices,
            currentDeviceCount,
            activationMode = currentState.ActivationMode,
            licenseType = currentState.IsTrialActive ? "Trial" : currentState.Validation.LicenseType
        });
    }

    var payload = await request.ReadFromJsonAsync<DeviceCreateRequest>();
    if (payload is null)
    {
        return Results.BadRequest(new { message = "Invalid payload." });
    }

    try
    {
        var setup = ResolveDeviceSetup(
            personnelStore,
            payload.AreaCode,
            payload.Location,
            payload.DepartmentCode,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName);
        var device = service.RegisterDevice(
            payload.SerialNumber ?? string.Empty,
            payload.Name ?? string.Empty,
            payload.IpAddress ?? string.Empty,
            setup.Area.Name,
            payload.Model);
        var sqlIds = SyncSqlAreaAndDepartment(repository, setup.Area.Name, setup.Department.Name);
        device.AreaId = sqlIds.AreaId;
        device.DepartmentId = sqlIds.DepartmentId;
        if (payload.RegistrationDevice.HasValue)
        {
            device.IsRegistrationDevice = payload.RegistrationDevice.Value;
        }
        repository.UpdateDevice(device);
        UpsertDeviceAssignment(personnelStore, device.SerialNumber, setup.Area.Code, setup.Department.Code);
        PersistDeviceAssignmentToDb(postgresConnectionString, device.SerialNumber, setup.Area.Code, setup.Department.Code);
        if (payload.ClearAllDataOnAdd == true)
        {
            // User chose "clear all data": do NOT fetch anything (even for a registration
            // device). Mark the device so that on its next connect we send CLEAR DATA and
            // suppress the user/biometric real-time upload (otherwise a registration device
            // would still push all its data on connect).
            MarkClearAllDataPending(clearAllDataPendingBySerial, device.SerialNumber);
            AddLog(
                "INFO",
                "Command",
                $"Clear-all-data selected on add — will clear device {device.SerialNumber} on connect and skip fetch.");
        }
        else if (device.IsRegistrationDevice)
        {
            var fetchResult = await QueueFetchAllCommandsAsync(
                service,
                device,
                includeAttendanceLogs: true,
                manualUserSyncUntilUtcBySerial,
                fullDataResetUtcBySerial,
                settings.Port);
            AddLog(
                "INFO",
                "Command",
                $"Auto fetch-all queued for registration device {device.SerialNumber}.",
                $"includeAttendanceLogs=true; wakeAttempted={fetchResult.WakeAttempted}; pendingCount={fetchResult.PendingCount}");
        }
        // Ensure the device's area has its default access-control entities and that they include
        // this device's serial, then sync them to the device.
        await EnsureAccessControlDefaultsForAllAreas(postgresConnectionString, service, personnelStore, syncService);
        AddLog("INFO", "Device", $"Device saved: {device.SerialNumber} ({device.Name}).");
        return Results.Json(device);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/devices/{id:guid}/connect", (Guid id) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var ok = service.ConnectDevice(id);
    if (ok) AddLog("INFO", "Device", $"Device connected: {id}");
    return ok ? Results.Ok(new { success = true }) : Results.NotFound(new { success = false });
});

app.MapPost("/api/devices/{id:guid}/disconnect", (Guid id) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var ok = service.DisconnectDevice(id);
    if (ok) AddLog("WARN", "Device", $"Device disconnected: {id}");
    return ok ? Results.Ok(new { success = true }) : Results.NotFound(new { success = false });
});

app.MapDelete("/api/devices/{id:guid}", (Guid id) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var ok = service.DeleteDevice(id);
    if (ok) AddLog("WARN", "Device", $"Device deleted: {id}");
    return ok ? Results.Ok(new { success = true }) : Results.NotFound(new { success = false });
});

app.MapPost("/api/devices/{serial}/commands", async (string serial, HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<DeviceCommandRequest>();
    if (payload is null)
    {
        return Results.BadRequest(new { message = "Invalid payload." });
    }

    try
    {
        var normalizedSerial = serial.Trim();
        var normalized = NormalizeCommandForQueue(payload.Command ?? string.Empty);
        var cmd = service.QueueDeviceCommand(normalizedSerial, normalized);
        var device = service.ListDevices().FirstOrDefault(d =>
            string.Equals(d.SerialNumber, normalizedSerial, StringComparison.OrdinalIgnoreCase));
        var wakeAttempted = false;
        if (!string.IsNullOrWhiteSpace(device?.IpAddress))
        {
            wakeAttempted = true;
            await TryWakeDeviceAsync(device.IpAddress, normalizedSerial, settings.Port);
        }
        AddLog("INFO", "Command", $"Command queued for {serial}: {cmd.CommandText}");
        return Results.Json(new
        {
            cmd.Id,
            cmd.DeviceSerialNumber,
            cmd.CommandText,
            cmd.CreatedUtc,
            cmd.SentUtc,
            cmd.ReplyText,
            wakeAttempted
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

// ─── Firmware Upgrade ───────────────────────────────────────────────────────
// POST /api/devices/firmware-upgrade
// Accepts multipart/form-data:  file=<emfw.cfg>  serials=SN1,SN2,...
// Stores the file, computes MD5, queues UPGRADE command to each target serial.
app.MapPost("/api/devices/firmware-upgrade", async (HttpRequest request) =>
{
    if (!serverRunning)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    if (!request.HasFormContentType)
        return Results.BadRequest(new { message = "Multipart form required." });

    IFormFile? fwFile = null;
    var fwSerials = new List<string>();

    try
    {
        var form = await request.ReadFormAsync();
        fwFile = form.Files.GetFile("file");
        var serialsRaw = form["serials"].ToString();
        if (!string.IsNullOrWhiteSpace(serialsRaw))
            fwSerials.AddRange(serialsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"Failed to read form: {ex.Message}" });
    }

    if (fwFile == null || fwFile.Length == 0)
        return Results.BadRequest(new { message = "Firmware file is required." });

    var allDevices = service.ListDevices();
    var fwTargets = fwSerials.Count > 0
        ? allDevices.Where(d => fwSerials.Contains(d.SerialNumber, StringComparer.OrdinalIgnoreCase)).ToList()
        : allDevices.ToList();

    if (fwTargets.Count == 0)
        return Results.BadRequest(new { message = "No matching devices found." });

    var fwTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var fwBaseDir = Path.Combine(app.Environment.WebRootPath, "file", "fw", fwTimestamp.ToString());
    Directory.CreateDirectory(fwBaseDir);
    var fwFileName = Path.GetFileName(fwFile.FileName).Replace(" ", "_");
    var fwFilePath = Path.Combine(fwBaseDir, fwFileName);
    await using (var fwFs = System.IO.File.Create(fwFilePath))
        await fwFile.CopyToAsync(fwFs);

    string fwMd5;
    await using (var fwFs = System.IO.File.OpenRead(fwFilePath))
    {
        var hashBytes = System.Security.Cryptography.MD5.HashData(fwFs);
        fwMd5 = Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    var fwFileSize = new FileInfo(fwFilePath).Length;
    var fwRelUrl = $"file/fw/{fwTimestamp}/{fwFileName}";
    var fwUpgradeCmd = $"UPGRADE checksum={fwMd5},url={fwRelUrl},s={fwFileSize}";

    var fwQueued = new List<string>();
    foreach (var dev in fwTargets)
    {
        service.QueueDeviceCommand(dev.SerialNumber, fwUpgradeCmd);
        fwQueued.Add(dev.SerialNumber);
        AddLog("INFO", "Firmware", $"Queued UPGRADE for {dev.SerialNumber}: {fwUpgradeCmd}");
    }

    return Results.Ok(new
    {
        message = $"Firmware upgrade queued for {fwQueued.Count} device(s).",
        command = fwUpgradeCmd,
        queued = fwQueued
    });
});

app.MapPost("/api/devices/{serial}/commands/clear", (string serial) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var cleared = service.ClearPendingCommands(serial);
    AddLog("INFO", "Command", $"Cleared pending commands for {serial}: {cleared}");
    return Results.Ok(new { success = true, cleared });
});

app.MapDelete("/api/devices/{serial}/commands/{id:guid}", (string serial, Guid id) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var deleted = service.DeleteDeviceCommand(id);
    if (deleted)
    {
        AddLog("INFO", "Command", $"Deleted command {id} for {serial}");
        return Results.Ok(new { success = true, id });
    }

    return Results.NotFound(new { success = false, message = "Command not found." });
});

app.MapPost("/api/devices/{serial}/commands/force-poll", async (string serial) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (string.IsNullOrWhiteSpace(serial))
    {
        return Results.BadRequest(new { success = false, message = "Device serial is required." });
    }

    var normalizedSerial = serial.Trim();
    var device = service.ListDevices().FirstOrDefault(d =>
        string.Equals(d.SerialNumber, normalizedSerial, StringComparison.OrdinalIgnoreCase));
    if (device is null)
    {
        return Results.NotFound(new { success = false, message = "Device not found." });
    }

    // Queue a lightweight nudge command so the next poll has immediate payload.
    service.QueueDeviceCommand(normalizedSerial, "CHECK");

    var wakeAttempted = false;
    if (!string.IsNullOrWhiteSpace(device.IpAddress))
    {
        wakeAttempted = true;
        await TryWakeDeviceAsync(device.IpAddress, normalizedSerial, settings.Port);
    }

    var pendingCount = service.GetPendingCommands(normalizedSerial, 200).Count;
    AddLog("INFO", "Command", $"Force poll requested for {normalizedSerial}. Pending commands: {pendingCount}.");
    return Results.Ok(new
    {
        success = true,
        wakeAttempted,
        pendingCount,
        message = "Poll nudge sent. Device should fetch pending commands on next request."
    });
});

app.MapPost("/api/devices/{serial}/fetch-all", async (string serial, HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (string.IsNullOrWhiteSpace(serial))
    {
        return Results.BadRequest(new { success = false, message = "Device serial is required." });
    }

    var normalizedSerial = serial.Trim();
    var device = service.ListDevices().FirstOrDefault(d =>
        string.Equals(d.SerialNumber, normalizedSerial, StringComparison.OrdinalIgnoreCase));
    if (device is null)
    {
        return Results.NotFound(new { success = false, message = "Device not found." });
    }
    FetchAllDataRequest? payload = null;
    if (request.ContentLength.GetValueOrDefault() > 0)
    {
        payload = await request.ReadFromJsonAsync<FetchAllDataRequest>();
    }

    var includeAttendanceLogs = payload?.IncludeAttendanceLogs ?? false;
    var fetchResult = await QueueFetchAllCommandsAsync(
        service,
        device,
        includeAttendanceLogs,
        manualUserSyncUntilUtcBySerial,
        fullDataResetUtcBySerial,
        settings.Port);

    AddLog(
        "INFO",
        "Command",
        $"Fetch-all requested for {normalizedSerial}: full data query queued.",
        $"registrationDevice={device.IsRegistrationDevice}; includeAttendanceLogs={includeAttendanceLogs}; wakeAttempted={fetchResult.WakeAttempted}; pendingCount={fetchResult.PendingCount}");

    return Results.Ok(new
    {
        success = true,
        serial = normalizedSerial,
        includeAttendanceLogs,
        wakeAttempted = fetchResult.WakeAttempted,
        pendingCount = fetchResult.PendingCount,
        clearedPending = fetchResult.ClearedPending,
        registrationDevice = device.IsRegistrationDevice,
        commandId = fetchResult.CheckCommandId,
        queuedSteps = fetchResult.QueuedSteps,
        message = "Fetch-all has been queued. User/biometric data will import for this manual sync window, and options=all will force full re-upload."
    });
});

app.MapPost("/api/faceenrollment/enroll", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<UniversalFaceEnrollmentRequest>();
    if (payload is null || string.IsNullOrWhiteSpace(payload.DeviceSerial) || string.IsNullOrWhiteSpace(payload.UserId))
    {
        return Results.BadRequest(new { success = false, message = "deviceSerial and userId are required." });
    }

    var serial = payload.DeviceSerial.Trim();
    var userId = payload.UserId.Trim();
    var device = service.ListDevices().FirstOrDefault(d =>
        string.Equals(d.SerialNumber, serial, StringComparison.OrdinalIgnoreCase));
    if (device is null)
    {
        return Results.NotFound(new { success = false, message = "Device not found." });
    }

    if (!device.IsOnline)
    {
        return Results.BadRequest(new { success = false, message = "Device is offline." });
    }

    // Prevent stale queued FP commands from taking over the device prompt.
    var clearedBeforeEnroll = service.ClearPendingCommands(serial);
    if (clearedBeforeEnroll > 0)
    {
        AddLog("INFO", "Enrollment", $"Cleared {clearedBeforeEnroll} pending command(s) before face enroll for {serial}.");
    }

    var requestedName = payload.UserName?.Trim() ?? string.Empty;
    var requestedCard = payload.CardNo?.Trim() ?? string.Empty;

    var employee = service.ListEmployees()
        .FirstOrDefault(e => string.Equals(e.EmployeeCode, userId, StringComparison.OrdinalIgnoreCase));
    var autoCreatedUser = false;
    if (employee is null)
    {
        var initialName = string.IsNullOrWhiteSpace(requestedName) ? userId : requestedName;
        var setup = ResolveDeviceSetupForSerial(
            personnelStore,
            device.SerialNumber,
            device.Location,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName);
        employee = service.CreateEmployee(userId, initialName, setup.Department.Name);
        employee.Password = NormalizePasswordHash(userId);
        employee.VerifyMode = ProgramHelpers.NormalizeEmployeeVerifyMode(payload.VerifyMode);
        repository.UpdateEmployee(employee);
        autoCreatedUser = true;
        AddLog("INFO", "Enrollment", $"Auto-created employee {userId} in {setup.Department.Name} before face enrollment.");
    }

    var employeeUpdated = false;
    var requestedVerifyMode = ProgramHelpers.NormalizeEmployeeVerifyMode(payload.VerifyMode, employee.VerifyMode);
    if (string.IsNullOrWhiteSpace(employee.FullName) && !string.IsNullOrWhiteSpace(requestedName))
    {
        employee.FullName = requestedName;
        employeeUpdated = true;
    }
    if (string.IsNullOrWhiteSpace(employee.CardNumber) && !string.IsNullOrWhiteSpace(requestedCard))
    {
        employee.CardNumber = requestedCard;
        employeeUpdated = true;
    }
    if (employee.VerifyMode != requestedVerifyMode)
    {
        employee.VerifyMode = requestedVerifyMode;
        employeeUpdated = true;
    }
    if (employeeUpdated)
    {
        repository.UpdateEmployee(employee);
    }

    var profile = personnelStore.Profiles
        .FirstOrDefault(p => string.Equals(p.EmployeeCode, userId, StringComparison.OrdinalIgnoreCase));
    var shouldUpsertProfile = profile is null && (autoCreatedUser || !string.IsNullOrWhiteSpace(requestedCard));
    if (profile is not null &&
        string.IsNullOrWhiteSpace(profile.CardNumber) &&
        !string.IsNullOrWhiteSpace(requestedCard))
    {
        shouldUpsertProfile = true;
    }
    if (shouldUpsertProfile)
    {
        var privilegeToken = ToProfilePrivilegeLabel(employee.Privilege);
        ProgramHelpers.UpsertEmployeeProfile(
            personnelStore,
            employee.EmployeeCode,
            profile?.Privilege ?? privilegeToken,
            string.IsNullOrWhiteSpace(profile?.CardNumber) ? requestedCard : profile.CardNumber,
            profile?.PictureDataUrl ?? employee.ProfilePhoto ?? string.Empty,
            profile?.MobileNumber ?? employee.MobileNumber ?? string.Empty,
            profile?.Gender ?? (string.IsNullOrWhiteSpace(employee.Gender) ? "Male" : employee.Gender),
            profile?.HireDate ?? string.Empty,
            ResolveDevicePassword(profile?.Password, employee.Password),
            verifyMode: requestedVerifyMode);
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
        profile = personnelStore.Profiles
            .FirstOrDefault(p => string.Equals(p.EmployeeCode, userId, StringComparison.OrdinalIgnoreCase));
    }

    var commandName = !string.IsNullOrWhiteSpace(employee.FullName)
        ? employee.FullName
        : (!string.IsNullOrWhiteSpace(requestedName) ? requestedName : userId);
    var safeName = SanitizeCommandValue(commandName);
    if (string.IsNullOrWhiteSpace(safeName))
    {
        safeName = SanitizeCommandValue(userId);
    }

    var cardValue = !string.IsNullOrWhiteSpace(profile?.CardNumber)
        ? profile.CardNumber
        : (!string.IsNullOrWhiteSpace(employee.CardNumber) ? employee.CardNumber : requestedCard);
    var safeCardNo = SanitizeCommandValue(cardValue ?? string.Empty);
    var safePassword = SanitizeCommandValue(ResolveDevicePassword(profile?.Password, employee.Password));
    var privilege = (int)ResolveDeviceSyncPrivilege(profile?.Privilege, employee.Privilege);
    var verifyMode = ProgramHelpers.NormalizeEmployeeVerifyMode(payload.VerifyMode, ResolveDeviceSyncVerifyMode(profile?.VerifyMode, employee.VerifyMode));
    var deviceGroupId = profile?.GroupId ?? (employee.DepartmentId > 0 ? employee.DepartmentId : 1);
    var timezoneProfile = string.IsNullOrWhiteSpace(profile?.TimezoneProfile)
        ? "0000000000000000"
        : profile.TimezoneProfile;
    var userInfoCommand =
        $"DATA UPDATE USERINFO PIN={userId}\tName={safeName}\tPri={privilege}\tPasswd={safePassword}\tCard={safeCardNo}\tGrp={deviceGroupId}\tTZ={timezoneProfile}\tVerify={verifyMode}";
    // Strict mode: caller must explicitly select face type (2=IR, 9=Visible Light).
    var selectedFaceType = payload.FaceType?.Trim() switch
    {
        "2" => 2,
        "9" => 9,
        _ => 0
    };
    if (selectedFaceType == 0)
    {
        return Results.BadRequest(new { success = false, message = "faceType is required and must be '2' (infrared) or '9' (visible)." });
    }
    // Do not hard-block by cached capability flags. Some firmware reports stale/partial flags
    // while still accepting the selected ENROLL_BIO type. Let the device return the final result.

    var retry = payload.Retry is >= 1 and <= 10 ? payload.Retry.Value : 3;
    var overwrite = payload.Overwrite ?? true ? 1 : 0;
    var existingUserOnly = autoCreatedUser ? false : payload.ExistingUserOnly ?? false;
    var directFaceCommand = protocolService.CreateRemoteEnrollCommand(userId, selectedFaceType, retry, overwrite, null);

    // Queue-only flow for reliability with push devices.
    if (!string.IsNullOrWhiteSpace(device.IpAddress))
    {
        await TryWakeDeviceAsync(device.IpAddress, serial, settings.Port);
    }
    service.ClearPendingCommands(serial);
    var queued = new List<DeviceCommand>
    {
        service.QueueDeviceCommand(serial, "CHECK")
    };
    var queuedSteps = new List<string> { "CHECK" };
    if (!existingUserOnly)
    {
        queued.Add(service.QueueDeviceCommand(serial, userInfoCommand));
        queuedSteps.Add("DATA UPDATE USERINFO");
    }

    queued.Add(service.QueueDeviceCommand(serial, directFaceCommand));
    queuedSteps.Add($"ENROLL_BIO(TYPE={selectedFaceType})");
    var firstEnroll = queued[^1];
    var syncLogIds = new[] { firstEnroll.Id.ToString() }
        .Concat(queued.Where(c => c.Id != firstEnroll.Id).Select(c => c.Id.ToString()))
        .ToArray();

    AddLog("INFO", "Enrollment", $"Queued fallback face enroll commands for {serial} user {userId}.", $"{directFaceCommand}; existingUserOnly={existingUserOnly}; autoCreatedUser={autoCreatedUser}");
    return Results.Ok(new
    {
        success = true,
        code = 202,
        mode = "queued",
        commandId = firstEnroll.Id.ToString(),
        message = autoCreatedUser
            ? "User auto-saved and face enrollment queued. Device will prompt for face."
            : existingUserOnly
                ? "Face enrollment queued for existing user. Device will prompt shortly."
                : "Enrollment queued. Device will respond in a few seconds.",
        command = directFaceCommand,
        preProvisionedUser = !existingUserOnly,
        autoCreatedUser,
        queuedCount = queued.Count,
        queuedSteps = queuedSteps.ToArray(),
        queued = true,
        syncLogIds
    });
});

app.MapPost("/api/fpenrollment/enroll", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<FingerprintEnrollmentRequest>();
    if (payload is null || string.IsNullOrWhiteSpace(payload.DeviceSerial) || string.IsNullOrWhiteSpace(payload.UserId))
    {
        return Results.BadRequest(new { success = false, message = "deviceSerial and userId are required." });
    }

    var serial = payload.DeviceSerial.Trim();
    var userId = payload.UserId.Trim();
    var device = service.ListDevices().FirstOrDefault(d =>
        string.Equals(d.SerialNumber, serial, StringComparison.OrdinalIgnoreCase));
    if (device is null)
    {
        return Results.NotFound(new { success = false, message = "Device not found." });
    }

    if (!device.IsOnline)
    {
        return Results.BadRequest(new { success = false, message = "Device is offline." });
    }

    var employee = service.ListEmployees()
        .FirstOrDefault(e => string.Equals(e.EmployeeCode, userId, StringComparison.OrdinalIgnoreCase));
    if (employee is null)
    {
        return Results.BadRequest(new { success = false, message = "User not found in database." });
    }

    var profile = personnelStore.Profiles
        .FirstOrDefault(p => string.Equals(p.EmployeeCode, userId, StringComparison.OrdinalIgnoreCase));
    var safeName = SanitizeCommandValue(employee.FullName ?? payload.UserName ?? userId);
    if (string.IsNullOrWhiteSpace(safeName))
    {
        safeName = SanitizeCommandValue(userId);
    }

    var safeCardNo = SanitizeCommandValue(profile?.CardNumber ?? employee.CardNumber ?? payload.CardNo ?? string.Empty);
    var safePassword = SanitizeCommandValue(ResolveDevicePassword(profile?.Password, employee.Password));
    var privilege = (int)ResolveDeviceSyncPrivilege(profile?.Privilege, employee.Privilege);
    var verifyMode = ProgramHelpers.NormalizeEmployeeVerifyMode(payload.VerifyMode, ResolveDeviceSyncVerifyMode(profile?.VerifyMode, employee.VerifyMode));
    var deviceGroupId = profile?.GroupId ?? (employee.DepartmentId > 0 ? employee.DepartmentId : 1);
    var timezoneProfile = string.IsNullOrWhiteSpace(profile?.TimezoneProfile)
        ? "0000000000000000"
        : profile.TimezoneProfile;
    var fingerIndex = payload.FingerIndex is >= 0 and <= 9 ? payload.FingerIndex.Value : 0;
    var retry = payload.Retry is >= 1 and <= 10 ? payload.Retry.Value : 3;
    var overwrite = payload.Overwrite ?? true ? 1 : 0;

    var userInfoCommand =
        $"DATA UPDATE USERINFO PIN={userId}\tName={safeName}\tPri={privilege}\tPasswd={safePassword}\tCard={safeCardNo}\tGrp={deviceGroupId}\tTZ={timezoneProfile}\tVerify={verifyMode}";
    var enrollCommand = $"ENROLL_FP\tPIN={userId}\tFID={fingerIndex}\tRETRY={retry}\tOVERWRITE={overwrite}";

    service.ClearPendingCommands(serial);
    service.QueueDeviceCommand(serial, "CHECK");
    service.QueueDeviceCommand(serial, userInfoCommand);
    var enroll = service.QueueDeviceCommand(serial, enrollCommand);
    AddLog("INFO", "Enrollment", $"Queued FP enroll for {serial} user {userId} finger {fingerIndex}.");

    return Results.Ok(new
    {
        success = true,
        mode = "queued",
        commandId = enroll.Id.ToString(),
        message = "Fingerprint enrollment queued. Place finger on the device sensor.",
        command = enrollCommand,
        preProvisionedUser = true,
        queuedCount = 3,
        queuedSteps = new[] { "CHECK", "DATA UPDATE USERINFO", $"ENROLL_FP(FID={fingerIndex})" }
    });
});

app.MapMethods("/api/fpenrollment/delete", ["DELETE", "POST"], async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    string? userId = request.Query["userId"].ToString()?.Trim();
    string? fidStr = request.Query["fingerIndex"].ToString()?.Trim();
    string? serial = request.Query["deviceSerial"].ToString()?.Trim();

    if (string.IsNullOrWhiteSpace(userId) && request.HasJsonContentType())
    {
        try
        {
            var body = await request.ReadFromJsonAsync<JsonElement>();
            if (body.TryGetProperty("userId", out var u)) userId = u.GetString()?.Trim();
            if (body.TryGetProperty("fingerIndex", out var f)) fidStr = f.ToString();
            if (body.TryGetProperty("deviceSerial", out var s)) serial = s.GetString()?.Trim();
        }
        catch { }
    }

    if (string.IsNullOrWhiteSpace(userId) || !int.TryParse(fidStr, out var fingerIndex) || fingerIndex < 0 || fingerIndex > 9)
    {
        return Results.BadRequest(new { success = false, message = "userId and valid fingerIndex (0-9) are required." });
    }

    if (!string.IsNullOrWhiteSpace(postgresConnectionString))
    {
        try
        {
            using var conn = new Npgsql.NpgsqlConnection(postgresConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"DELETE FROM ""E_Attendace"".""biometric_templates"" WHERE ""user_pin"" = @pin AND ""type"" = 1 AND ""no"" = @fid;";
            cmd.Parameters.AddWithValue("pin", userId);
            cmd.Parameters.AddWithValue("fid", fingerIndex);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            AddLog("WARN", "Enrollment", $"Failed to delete biometric_template for user {userId} FID {fingerIndex}: {ex.Message}");
        }
    }

    var employee = service.ListEmployees().FirstOrDefault(e => string.Equals(e.EmployeeCode, userId, StringComparison.OrdinalIgnoreCase));
    if (employee is not null)
    {
        employee.FingerprintTemplates = RemoveFingerprintFromTemplateJson(employee.FingerprintTemplates, fingerIndex);
        employee.SyncedAt = DateTime.UtcNow;
        repository.UpdateEmployee(employee);
    }

    var deleteCommand = $"DATA DELETE FINGERTMP PIN={userId}\tFID={fingerIndex}";
    if (!string.IsNullOrWhiteSpace(serial))
    {
        service.QueueDeviceCommand(serial, deleteCommand);
    }
    else
    {
        foreach (var d in service.ListDevices())
        {
            service.QueueDeviceCommand(d.SerialNumber, deleteCommand);
        }
    }

    AddLog("INFO", "Enrollment", $"Deleted finger {fingerIndex} for user {userId} on device {serial ?? "all"}.");
    return Results.Ok(new { success = true, message = $"Fingerprint {fingerIndex} deleted." });
});

app.MapPost("/api/cardenrollment/enroll", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<CardEnrollmentRequest>();
    if (payload is null || string.IsNullOrWhiteSpace(payload.DeviceSerial) || string.IsNullOrWhiteSpace(payload.UserId))
    {
        return Results.BadRequest(new { success = false, message = "deviceSerial and userId are required." });
    }

    var serial = payload.DeviceSerial.Trim();
    var userId = payload.UserId.Trim();
    var device = service.ListDevices().FirstOrDefault(d =>
        string.Equals(d.SerialNumber, serial, StringComparison.OrdinalIgnoreCase));
    if (device is null)
    {
        return Results.NotFound(new { success = false, message = "Device not found." });
    }

    if (!device.IsOnline)
    {
        return Results.BadRequest(new { success = false, message = "Device is offline." });
    }

    var employee = service.ListEmployees()
        .FirstOrDefault(e => string.Equals(e.EmployeeCode, userId, StringComparison.OrdinalIgnoreCase));
    if (employee is null)
    {
        return Results.BadRequest(new { success = false, message = "User not found in database." });
    }

    var profile = personnelStore.Profiles
        .FirstOrDefault(p => string.Equals(p.EmployeeCode, userId, StringComparison.OrdinalIgnoreCase));
    var safeName = SanitizeCommandValue(employee.FullName ?? payload.UserName ?? userId);
    if (string.IsNullOrWhiteSpace(safeName))
    {
        safeName = SanitizeCommandValue(userId);
    }

    var safeCardNo = SanitizeCommandValue(profile?.CardNumber ?? employee.CardNumber ?? payload.CardNo ?? string.Empty);
    var safePassword = SanitizeCommandValue(ResolveDevicePassword(profile?.Password, employee.Password));
    var privilege = (int)ResolveDeviceSyncPrivilege(profile?.Privilege, employee.Privilege);
    var verifyMode = ProgramHelpers.NormalizeEmployeeVerifyMode(payload.VerifyMode, ResolveDeviceSyncVerifyMode(profile?.VerifyMode, employee.VerifyMode));
    var deviceGroupId = profile?.GroupId ?? (employee.DepartmentId > 0 ? employee.DepartmentId : 1);
    var timezoneProfile = string.IsNullOrWhiteSpace(profile?.TimezoneProfile)
        ? "0000000000000000"
        : profile.TimezoneProfile;
    var retry = payload.Retry is >= 1 and <= 10 ? payload.Retry.Value : 3;

    var userInfoCommand =
        $"DATA UPDATE USERINFO PIN={userId}\tName={safeName}\tPri={privilege}\tPasswd={safePassword}\tCard={safeCardNo}\tGrp={deviceGroupId}\tTZ={timezoneProfile}\tVerify={verifyMode}";
    var enrollCommand = $"ENROLL_MF PIN={userId}\tRETRY={retry}";

    service.ClearPendingCommands(serial);
    service.QueueDeviceCommand(serial, "CHECK");
    service.QueueDeviceCommand(serial, userInfoCommand);
    var enroll = service.QueueDeviceCommand(serial, enrollCommand);
    AddLog("INFO", "Enrollment", $"Queued Card enroll for {serial} user {userId}.");

    return Results.Ok(new
    {
        success = true,
        mode = "queued",
        commandId = enroll.Id.ToString(),
        message = "Card enrollment queued. Swipe card on the device sensor.",
        command = enrollCommand,
        preProvisionedUser = true,
        queuedCount = 3,
        queuedSteps = new[] { "CHECK", "DATA UPDATE USERINFO", "ENROLL_MF" }
    });
});

app.MapGet("/api/cardenrollment/poll", (string serial, string? sinceUtc) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (string.IsNullOrWhiteSpace(serial))
    {
        return Results.BadRequest(new { message = "Serial is required." });
    }

    var normalizedSerial = serial.Trim();
    var thresholdUtc = DateTime.UtcNow.AddSeconds(-15);
    if (!string.IsNullOrWhiteSpace(sinceUtc) && DateTime.TryParse(sinceUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsedSince))
    {
        thresholdUtc = parsedSince;
    }

    if (latestDeviceCardSwipes.TryGetValue(normalizedSerial, out var swipe) && swipe.TimestampUtc >= thresholdUtc)
    {
        return Results.Json(new
        {
            success = true,
            cardScanned = true,
            cardNumber = swipe.CardNumber,
            timestampUtc = swipe.TimestampUtc
        });
    }

    // Fallback: Check recent TransactionDetails in PostgreSQL
    try
    {
        using var conn = new NpgsqlConnection(postgresConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "EmployeeCode", "RawLine", "ReceivedUtc"
            FROM "E_Attendace"."TransactionDetails"
            WHERE "DeviceSerialNumber" = @serial
              AND "ReceivedUtc" >= @since
            ORDER BY "ReceivedUtc" DESC
            LIMIT 1;
        """;
        cmd.Parameters.AddWithValue("serial", normalizedSerial);
        cmd.Parameters.AddWithValue("since", thresholdUtc);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            var empCode = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var rawLine = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var cardNum = !string.IsNullOrWhiteSpace(empCode) ? empCode : rawLine.Split('\t')[0].Trim();
            if (!string.IsNullOrWhiteSpace(cardNum) && !cardNum.StartsWith("OPLOG", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(new
                {
                    success = true,
                    cardScanned = true,
                    cardNumber = cardNum,
                    timestampUtc = reader.GetDateTime(2)
                });
            }
        }
    }
    catch
    {
        // Ignore fallback db query errors
    }

    return Results.Json(new
    {
        success = true,
        cardScanned = false
    });
});

app.MapGet("/api/faceenrollment/status/{commandId}", (string commandId) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (string.IsNullOrWhiteSpace(commandId))
    {
        return Results.BadRequest(new { message = "commandId is required." });
    }

    if (!Guid.TryParse(commandId, out var id) && !Guid.TryParseExact(commandId, "N", out id))
    {
        return Results.Ok(new
        {
            commandId,
            status = "Sent",
            completed = false,
            failed = false,
            errorMessage = (string?)null
        });
    }

    DeviceCommand? cmd = null;
    foreach (var d in service.ListDevices())
    {
        cmd = service.GetDeviceCommands(d.SerialNumber, 2000).FirstOrDefault(c => c.Id == id);
        if (cmd is not null)
        {
            break;
        }
    }
    if (cmd is null)
    {
        return Results.Ok(new
        {
            commandId,
            status = "NotFound",
            completed = false,
            failed = false,
            errorMessage = (string?)null
        });
    }

    var status = cmd.ReplyText is not null ? "Completed" : cmd.SentUtc is not null ? "Sent" : "Pending";
    var returnCode = ExtractReturnCode(cmd.ReplyText);
    var completed = string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase);
    var failed = completed && !string.Equals(returnCode, "0", StringComparison.OrdinalIgnoreCase);

    return Results.Ok(new
    {
        commandId,
        status,
        completed,
        failed,
        code = string.IsNullOrWhiteSpace(returnCode) ? (int?)null : int.Parse(returnCode),
        errorMessage = failed ? DescribeEnrollmentCode(returnCode) : (string?)null,
        response = cmd.ReplyText
    });
});

// Backward compatibility for existing callers using query parameters.
app.MapGet("/api/faceenrollment/status", (string commandId) =>
{
    if (!Guid.TryParse(commandId, out var id) && !Guid.TryParseExact(commandId, "N", out id))
    {
        return Results.Ok(new { commandId, status = "Sent", completed = false, failed = false, errorMessage = (string?)null });
    }

    DeviceCommand? cmd = null;
    foreach (var d in service.ListDevices())
    {
        cmd = service.GetDeviceCommands(d.SerialNumber, 2000).FirstOrDefault(c => c.Id == id);
        if (cmd is not null)
        {
            break;
        }
    }

    if (cmd is null)
    {
        return Results.Ok(new { commandId, status = "NotFound", completed = false, failed = false, errorMessage = (string?)null });
    }

    var status = cmd.ReplyText is not null ? "Completed" : cmd.SentUtc is not null ? "Sent" : "Pending";
    var returnCode = ExtractReturnCode(cmd.ReplyText);
    var completed = string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase);
    var failed = completed && !string.Equals(returnCode, "0", StringComparison.OrdinalIgnoreCase);
    return Results.Ok(new
    {
        commandId,
        status,
        completed,
        failed,
        code = string.IsNullOrWhiteSpace(returnCode) ? (int?)null : int.Parse(returnCode),
        errorMessage = failed ? DescribeEnrollmentCode(returnCode) : (string?)null,
        response = cmd.ReplyText
    });
});

app.MapPost("/api/faceenrollment/enroll-with-photo", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { success = false, message = "multipart/form-data is required." });
    }

    var form = await request.ReadFormAsync();
    var deviceSerial = form["deviceSerial"].ToString().Trim();
    var userId = form["userId"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(deviceSerial) || string.IsNullOrWhiteSpace(userId))
    {
        return Results.BadRequest(new { success = false, message = "deviceSerial and userId are required." });
    }

    var file = form.Files["photo"];
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { success = false, message = "No photo provided." });
    }

    if (file.Length > 500 * 1024)
    {
        return Results.BadRequest(new { success = false, message = "Photo too large. Max 500KB." });
    }

    var contentType = file.ContentType?.Trim().ToLowerInvariant() ?? string.Empty;
    if (contentType != "image/jpeg" && contentType != "image/jpg")
    {
        return Results.BadRequest(new { success = false, message = "Only JPG images are supported." });
    }

    var device = service.ListDevices().FirstOrDefault(d =>
        string.Equals(d.SerialNumber, deviceSerial, StringComparison.OrdinalIgnoreCase));
    if (device is null)
    {
        return Results.NotFound(new { success = false, message = "Device not found." });
    }
    if (!device.IsOnline)
    {
        return Results.BadRequest(new { success = false, message = "Device is offline." });
    }

    await using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    var base64 = Convert.ToBase64String(ms.ToArray());

    service.ClearPendingCommands(deviceSerial);
    service.QueueDeviceCommand(deviceSerial, "CHECK");
    // BIOPHOTO (comparison/face photo) is only supported by visible-light face devices per protocol
    // (Attendance_PUSH_Communication_Protocol_20250429 §12.1.1.8: "only supported by the visible
    // light devices"). Low-end devices without that hardware (e.g. IR-only face, or fingerprint-only)
    // silently can't use it, so the photo never shows there. They still accept the plain USERPIC
    // command (§12.1.1.7) for a display photo, so fall back to that when the device can't do BIOPHOTO.
    var photoCommand = device.SupportsVisibleLightFace || device.BioPhotoFun || device.VisiLightFunOn
        ? $"DATA UPDATE BIOPHOTO PIN={userId} Type=9 No=0 Index=0 Size={base64.Length} Content={base64} Format=0 PostBackTmpFlag=1"
        : $"DATA UPDATE USERPIC PIN={userId}\tSize={base64.Length}\tContent={base64}";
    var cmd = service.QueueDeviceCommand(deviceSerial, photoCommand);

    if (!string.IsNullOrWhiteSpace(device.IpAddress))
    {
        _ = TryWakeDeviceAsync(device.IpAddress, deviceSerial, settings.Port);
    }

    AddLog("INFO", "Enrollment", $"Queued face photo enrollment for {deviceSerial} user {userId}.");
    return Results.Ok(new
    {
        success = true,
        mode = "queued",
        commandId = cmd.Id.ToString(),
        message = "Face photo sent to device. Processing will complete shortly."
    });
});

app.MapPut("/api/devices/{id:guid}/info", async (Guid id, HttpRequest request, ConfigSyncService syncService) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<DeviceUpdateRequest>();
    if (payload is null)
    {
        return Results.BadRequest(new { message = "Invalid payload." });
    }

    var existing = service.ListDevices().FirstOrDefault(d => d.Id == id);
    if (existing is null)
    {
        return Results.NotFound(new { success = false, message = "Device not found." });
    }
    var wasRegistrationDevice = existing.IsRegistrationDevice;

    var setup = ResolveDeviceSetup(
        personnelStore,
        payload.AreaCode,
        payload.Location,
        payload.DepartmentCode,
        DefaultAreaCode,
        DefaultAreaName,
        DefaultDepartmentCode,
        DefaultDepartmentName);

    var ok = service.UpdateDeviceInfo(
        id,
        payload.Name,
        payload.Model,
        payload.IpAddress,
        setup.Area.Name,
        payload.RegistrationDevice);
    if (ok)
    {
        var devToUpdate = service.ListDevices().FirstOrDefault(d => d.Id == id);
        if (devToUpdate is not null)
        {
            var sqlIds = SyncSqlAreaAndDepartment(repository, setup.Area.Name, setup.Department.Name);
            devToUpdate.AreaId = sqlIds.AreaId;
            devToUpdate.DepartmentId = sqlIds.DepartmentId;
            repository.UpdateDevice(devToUpdate);
        }
        UpsertDeviceAssignment(personnelStore, existing.SerialNumber, setup.Area.Code, setup.Department.Code);
        PersistDeviceAssignmentToDb(postgresConnectionString, existing.SerialNumber, setup.Area.Code, setup.Department.Code);
        if (payload.ClearAllDataOnAdd == true)
        {
            // Clear all device data on next connect and suppress the fetch/upload.
            MarkClearAllDataPending(clearAllDataPendingBySerial, existing.SerialNumber);
            AddLog("INFO", "Command", $"Clear-all-data selected — will clear device {existing.SerialNumber} on connect and skip fetch.");
        }
        if (!wasRegistrationDevice && payload.RegistrationDevice == true && payload.ClearAllDataOnAdd != true)
        {
            var updated = service.ListDevices().FirstOrDefault(d => d.Id == id);
            if (updated is not null)
            {
                var fetchResult = await QueueFetchAllCommandsAsync(
                    service,
                    updated,
                    includeAttendanceLogs: true,
                    manualUserSyncUntilUtcBySerial,
                    fullDataResetUtcBySerial,
                    settings.Port);
                AddLog(
                    "INFO",
                    "Command",
                    $"Auto fetch-all queued after enabling registration device {updated.SerialNumber}.",
                    $"includeAttendanceLogs=true; wakeAttempted={fetchResult.WakeAttempted}; pendingCount={fetchResult.PendingCount}");
            }
        }
        // Ensure the (possibly newly assigned) area has its default access-control entities and
        // that they include this device, then sync them to the device.
        await EnsureAccessControlDefaultsForAllAreas(postgresConnectionString, service, personnelStore, syncService);
        AddLog("INFO", "Device", $"Device updated: {id}");
        return Results.Ok(new { success = true });
    }

    return Results.NotFound(new { success = false, message = "Device not found." });
});

app.MapGet("/api/devices/{serial}/commands", (string serial, int? take) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (string.IsNullOrWhiteSpace(serial))
    {
        return Results.BadRequest(new { message = "Device serial is required." });
    }

    var limit = take.GetValueOrDefault(200);
    if (limit is < 1 or > 2000)
    {
        limit = 200;
    }

    var rows = service.GetDeviceCommands(serial.Trim(), limit)
        .Select(c => new
        {
            c.Id,
            c.DeviceSerialNumber,
            c.CommandText,
            c.CreatedUtc,
            c.SentUtc,
            c.ReplyText,
            status = c.ReplyText is not null ? "replied" : c.SentUtc is not null ? "sent" : "pending"
        });

    return Results.Json(rows);
});

app.MapPost("/api/devices/{serial}/commands/preset", async (string serial, HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (string.IsNullOrWhiteSpace(serial))
    {
        return Results.BadRequest(new { message = "Device serial is required." });
    }

    var payload = await request.ReadFromJsonAsync<DevicePresetCommandRequest>();
    if (payload is null || string.IsNullOrWhiteSpace(payload.Action))
    {
        return Results.BadRequest(new { message = "Action is required." });
    }

    string commandText;
    try
    {
        commandText = BuildDevicePresetCommand(payload);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }

    try
    {
        var normalizedSerial = serial.Trim();
        var cmd = service.QueueDeviceCommand(normalizedSerial, commandText);
        var removedAttendance = 0;
        var action = payload.Action.Trim().ToLowerInvariant();

        if (action is "clear-data")
        {
            // "Clear All Data": also mark the device so its next connect handshake SUPPRESSES the
            // user/biometric upload and re-issues CLEAR DATA. Without this, a registration device
            // re-pushes all its data on the next handshake (which happens before it processes the
            // CLEAR DATA command), so the data appears to come right back ("fetched all data").
            MarkClearAllDataPending(clearAllDataPendingBySerial, normalizedSerial);
            AddLog("INFO", "Command", $"Clear All Data requested for {normalizedSerial}: device upload suppressed until cleared.");
        }

        if (action is "clear-log" or "clear-data")
        {
            // Database records are kept completely untouched for payroll/history.
            // Only device hardware memory is cleared via the queued command.

            // The device list's transactionQty falls back to this cached self-reported count
            // whenever our own AttendanceRecords count is 0 (see LoadDevicePunchCounts usage) —
            // without resetting it here, a just-cleared device kept showing its stale pre-clear
            // count until the device happened to report a fresh INFO/options payload.
            if (discoveredDevices.TryGetValue(normalizedSerial, out var discoveredForClear))
            {
                if (action is "clear-data")
                {
                    discoveredDevices[normalizedSerial] = discoveredForClear with
                    {
                        TransactionQty = 0,
                        UserQty = 0,
                        FpQty = 0,
                        FaceQty = 0
                    };
                }
                else if (action is "clear-log")
                {
                    discoveredDevices[normalizedSerial] = discoveredForClear with
                    {
                        TransactionQty = 0
                    };
                }
            }
        }

        var deviceForWake = service.ListDevices().FirstOrDefault(d =>
            string.Equals(d.SerialNumber, normalizedSerial, StringComparison.OrdinalIgnoreCase));
        var wakeAttempted = false;
        if (!string.IsNullOrWhiteSpace(deviceForWake?.IpAddress))
        {
            wakeAttempted = true;
            await TryWakeDeviceAsync(deviceForWake.IpAddress, normalizedSerial, settings.Port);
        }

        AddLog("INFO", "Command", $"Preset command queued for {serial}: {commandText}");
        return Results.Json(new
        {
            cmd.Id,
            cmd.DeviceSerialNumber,
            cmd.CommandText,
            cmd.CreatedUtc,
            cmd.SentUtc,
            cmd.ReplyText,
            removedAttendance,
            wakeAttempted
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/device/sync/{deviceSerial}", (string deviceSerial) =>
{
    try
    {
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (string.IsNullOrWhiteSpace(deviceSerial))
    {
        return Results.BadRequest(new { message = "Device serial is required." });
    }

    var device = service.ListDevices().FirstOrDefault(d =>
        string.Equals(d.SerialNumber, deviceSerial.Trim(), StringComparison.OrdinalIgnoreCase));
    if (device is null)
    {
        return Results.NotFound(new { message = "Device not found." });
    }

    NormalizeEmployeeDepartmentsToSetup(
        repository,
        personnelStore,
        DefaultAreaCode,
        DefaultAreaName,
        DefaultDepartmentCode,
        DefaultDepartmentName);

    var byCode = personnelStore.Profiles.ToDictionary(p => p.EmployeeCode, StringComparer.OrdinalIgnoreCase);
    var setup = ResolveDeviceSetupForSerial(
        personnelStore,
        device.SerialNumber,
        device.Location,
        DefaultAreaCode,
        DefaultAreaName,
        DefaultDepartmentCode,
        DefaultDepartmentName);
    var employees = service.ListEmployees()
        .Where(e => e.IsActive)
        .Select(e =>
        {
            var profile = byCode.TryGetValue(e.EmployeeCode, out var p) ? p : null;
            var departmentName = ResolveEmployeeDepartmentForDevice(
                personnelStore,
                e,
                profile,
                setup.Department,
                DefaultAreaCode,
                DefaultAreaName,
                DefaultDepartmentCode,
                DefaultDepartmentName);
            return new
            {
                Employee = e,
                Profile = profile,
                DepartmentName = departmentName
            };
        })
        .Where(x => !string.IsNullOrWhiteSpace(x.DepartmentName))
        .Select(x =>
        {
            var e = x.Employee;
            return CreateEmployeeForDeviceSync(e, x.Profile, x.DepartmentName!);
        })
        .ToList();

    var pins = employees
        .Select(static e => e.EmployeeCode?.Trim())
        .Where(static pin => !string.IsNullOrWhiteSpace(pin))
        .Cast<string>()
        .ToList();
    var biometricTemplatesByPin = repository.GetBiometricTemplatesByPins(pins);
    var biometricPhotosByPin = repository.GetBiometricPhotosByPins(pins);
    foreach (var employee in employees)
    {
        HydrateEmployeeBiometricsForDeviceSync(employee, device, biometricTemplatesByPin, biometricPhotosByPin);
    }

    var timetables = service.ListTimetables();
    var departmentTimetables = service.ListDepartmentTimetables();
    var holidays = repository.GetAllHolidays();
    var combinations = repository.GetAllAccCombinations();
    var commandRows = protocolService.CreateBulkSyncCommands(
        employees, 
        device, 
        timetables, 
        departmentTimetables,
        holidays,
        combinations);
    var commandCount = 0;
    foreach (var (type, commandText) in commandRows)
    {
        var normalized = NormalizeCommandForQueue(commandText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            continue;
        }

        service.QueueDeviceCommand(device.SerialNumber, normalized);
        service.AddSyncLog(device.SerialNumber, type, commandText, SyncStatus.Pending);
        commandCount++;
    }

    AddLog("INFO", "Sync", $"Device sync queued for {device.SerialNumber} (Area: {setup.Area.Name}, Department: {setup.Department.Name}). Commands: {commandCount}");
    return Results.Ok(new { message = "Sync commands queued", commandCount, area = setup.Area.Name, department = setup.Department.Name });
    }
    catch (Exception ex)
    {
        AddLog("ERROR", "Sync", $"Device sync failed for {deviceSerial}: {ex.Message}", ex.ToString());
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/api/device/sync/{deviceSerial}/selected", async (string deviceSerial, HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (string.IsNullOrWhiteSpace(deviceSerial))
    {
        return Results.BadRequest(new { message = "Device serial is required." });
    }

    var payload = await request.ReadFromJsonAsync<DeviceSyncSelectedRequest>();
    if (payload is null || payload.EmployeeIds.Count == 0)
    {
        return Results.BadRequest(new { message = "Select employee(s) first." });
    }

    var serial = deviceSerial.Trim();
    var device = service.ListDevices().FirstOrDefault(d =>
        string.Equals(d.SerialNumber, serial, StringComparison.OrdinalIgnoreCase));
    if (device is null)
    {
        return Results.NotFound(new { message = "Device not found." });
    }

    var selectedIds = payload.EmployeeIds
        .Where(id => id != Guid.Empty)
        .ToHashSet();
    if (selectedIds.Count == 0)
    {
        return Results.BadRequest(new { message = "No valid employee IDs provided." });
    }

    NormalizeEmployeeDepartmentsToSetup(
        repository,
        personnelStore,
        DefaultAreaCode,
        DefaultAreaName,
        DefaultDepartmentCode,
        DefaultDepartmentName);

    var byCode = personnelStore.Profiles.ToDictionary(p => p.EmployeeCode, StringComparer.OrdinalIgnoreCase);
    var setup = ResolveDeviceSetupForSerial(
        personnelStore,
        device.SerialNumber,
        device.Location,
        DefaultAreaCode,
        DefaultAreaName,
        DefaultDepartmentCode,
        DefaultDepartmentName);
    var employees = service.ListEmployees()
        .Where(e => selectedIds.Contains(e.Id))
        .Select(e =>
        {
            var profile = byCode.TryGetValue(e.EmployeeCode, out var p) ? p : null;
            var departmentName = ResolveEmployeeDepartmentForDevice(
                personnelStore,
                e,
                profile,
                setup.Department,
                DefaultAreaCode,
                DefaultAreaName,
                DefaultDepartmentCode,
                DefaultDepartmentName);
            return string.IsNullOrWhiteSpace(departmentName)
                ? null
                : CreateEmployeeForDeviceSync(e, profile, departmentName);
        })
        .Where(static employee => employee is not null)
        .Select(static employee => employee!)
        .ToList();

    if (employees.Count == 0)
    {
        return Results.BadRequest(new { message = "None of the selected employees belong to this device's area." });
    }

    var pins = employees
        .Select(static e => e.EmployeeCode?.Trim())
        .Where(static pin => !string.IsNullOrWhiteSpace(pin))
        .Cast<string>()
        .ToList();
    var biometricTemplatesByPin = repository.GetBiometricTemplatesByPins(pins);
    var biometricPhotosByPin = repository.GetBiometricPhotosByPins(pins);
    foreach (var employee in employees)
    {
        HydrateEmployeeBiometricsForDeviceSync(employee, device, biometricTemplatesByPin, biometricPhotosByPin);
    }

    var timetables = service.ListTimetables();
    var departmentTimetables = service.ListDepartmentTimetables();
    var holidays = repository.GetAllHolidays();
    var combinations = repository.GetAllAccCombinations();
    var commandRows = protocolService.CreateBulkSyncCommands(
        employees, 
        device, 
        timetables, 
        departmentTimetables,
        holidays,
        combinations);
    var commandCount = 0;
    foreach (var (type, commandText) in commandRows)
    {
        var normalized = NormalizeCommandForQueue(commandText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            continue;
        }

        service.QueueDeviceCommand(device.SerialNumber, normalized);
        service.AddSyncLog(device.SerialNumber, type, commandText, SyncStatus.Pending);
        commandCount++;
    }

    AddLog("INFO", "Sync", $"Selected user sync queued for {device.SerialNumber}. Employees: {employees.Count}, Commands: {commandCount}");
    return Results.Ok(new { message = "Selected user sync queued", commandCount, employeeCount = employees.Count });
});

app.MapGet("/api/device/sync/{deviceSerial}/logs", (string deviceSerial, int? take) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var limit = take.GetValueOrDefault(200);
    if (limit is < 1 or > 2000)
    {
        limit = 200;
    }

    var rows = service.GetSyncLogs(deviceSerial, limit);
    return Results.Json(rows);
});

app.MapGet("/api/device/sync/{deviceSerial}/pending-diagnostics", (string deviceSerial, int? take) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (string.IsNullOrWhiteSpace(deviceSerial))
    {
        return Results.BadRequest(new { message = "Device serial is required." });
    }

    var serial = deviceSerial.Trim();
    var limit = take.GetValueOrDefault(500);
    if (limit is < 1 or > 5000)
    {
        limit = 500;
    }

    var syncLogs = service.GetSyncLogs(serial, limit)
        .OrderByDescending(x => x.CreatedUtc)
        .ToList();
    var commands = service.GetDeviceCommands(serial, 5000).ToList();

    var pending = syncLogs
        .Where(x => x.Status == SyncStatus.Pending)
        .Select(log =>
        {
            var normalized = NormalizeCommandForSyncMatch(log.CommandText);
            var bestCommand = commands
                .Where(c => string.Equals(NormalizeCommandForSyncMatch(c.CommandText), normalized, StringComparison.Ordinal))
                .OrderByDescending(c => c.CreatedUtc)
                .FirstOrDefault();

            return new
            {
                syncLogId = log.Id,
                log.CreatedUtc,
                log.Type,
                log.Status,
                log.CommandText,
                normalizedCommand = normalized,
                matchedCommandId = bestCommand?.Id,
                matchedCommandCreatedUtc = bestCommand?.CreatedUtc,
                matchedCommandSentUtc = bestCommand?.SentUtc,
                matchedCommandReplyText = bestCommand?.ReplyText,
                matchedCommandState = bestCommand is null
                    ? "no-command-match"
                    : bestCommand.ReplyText is not null
                        ? "replied"
                        : bestCommand.SentUtc is not null
                            ? "sent"
                            : "pending"
            };
        })
        .ToList();

    return Results.Json(new
    {
        serial,
        syncLogCount = syncLogs.Count,
        pendingCount = pending.Count,
        pending
    });
});

app.MapGet("/api/device/sync/{deviceSerial}/import-summary", (string deviceSerial, int? take) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    if (string.IsNullOrWhiteSpace(deviceSerial))
    {
        return Results.BadRequest(new { message = "Device serial is required." });
    }

    var serial = deviceSerial.Trim();
    var limit = take.GetValueOrDefault(500);
    if (limit is < 1 or > 5000)
    {
        limit = 500;
    }

    var logs = LoadAppLogs(postgresConnectionString, null, null, "DeviceEvent", 5000)
        .Where(l => l.Message.Contains(serial, StringComparison.OrdinalIgnoreCase))
        .ToList();

    int ExtractInt(string? detail, string key)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return 0;
        }

        var match = Regex.Match(
            detail,
            $@"(?:^|[;\s]){Regex.Escape(key)}\s*=\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    var recent = logs
        .Where(l =>
            l.Message.StartsWith("CDATA ", StringComparison.OrdinalIgnoreCase) ||
            l.Message.StartsWith("Imported ", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(l => l.TimestampUtc)
        .Take(limit)
        .Select(l => new
        {
            l.TimestampUtc,
            l.Level,
            l.Source,
            l.Message,
            l.Detail,
            attendance = ExtractInt(l.Detail, "attlog") + ExtractInt(l.Detail, "records"),
            userinfo = ExtractInt(l.Detail, "userinfo") + (l.Message.Contains("CDATA USERINFO", StringComparison.OrdinalIgnoreCase) ? ExtractInt(l.Detail, "rows") : 0),
            biodata = ExtractInt(l.Detail, "biodata") + (l.Message.Contains("CDATA BIODATA", StringComparison.OrdinalIgnoreCase) ? ExtractInt(l.Detail, "rows") : 0),
            biophoto = ExtractInt(l.Detail, "biophoto") + (l.Message.Contains("CDATA BIOPHOTO", StringComparison.OrdinalIgnoreCase) ? ExtractInt(l.Detail, "rows") : 0),
            fingertmp = ExtractInt(l.Detail, "fingertmp") + (l.Message.Contains("CDATA FINGERTMP", StringComparison.OrdinalIgnoreCase) ? ExtractInt(l.Detail, "rows") : 0),
            face = ExtractInt(l.Detail, "face") + (l.Message.Contains("CDATA FACE", StringComparison.OrdinalIgnoreCase) ? ExtractInt(l.Detail, "rows") : 0),
            userpic = ExtractInt(l.Detail, "userpic") + (l.Message.Contains("CDATA USERPIC", StringComparison.OrdinalIgnoreCase) ? ExtractInt(l.Detail, "rows") : 0)
        })
        .ToList();

    var totals = new
    {
        attendance = recent.Sum(r => r.attendance),
        userinfo = recent.Sum(r => r.userinfo),
        biodata = recent.Sum(r => r.biodata),
        biophoto = recent.Sum(r => r.biophoto),
        fingertmp = recent.Sum(r => r.fingertmp),
        face = recent.Sum(r => r.face),
        userpic = recent.Sum(r => r.userpic)
    };

    return Results.Json(new
    {
        serial,
        eventCount = recent.Count,
        totals,
        recent
    });
});

app.MapGet("/api/attendance", (string? from, string? to, Guid? employeeId, Guid? deviceId, string? status, string? search, int? take) =>
{
    NormalizeEmployeeDepartmentsToSetup(
        repository,
        personnelStore,
        DefaultAreaCode,
        DefaultAreaName,
        DefaultDepartmentCode,
        DefaultDepartmentName);
    DateOnly? fromDate = ParseDate(from);
    DateOnly? toDate = ParseDate(to);
    var limit = take.GetValueOrDefault(1000);
    if (limit is < 1 or > 5000)
    {
        limit = 1000;
    }

    var devices = service.ListDevices().ToDictionary(d => d.Id, d => d);
    var employees = service.ListEmployees().ToDictionary(e => e.Id, e => e);
    IEnumerable<AttendanceRecord> records = service.ListAttendance(fromDate, toDate);

    if (employeeId.HasValue && employeeId.Value != Guid.Empty)
    {
        records = records.Where(r => r.EmployeeId == employeeId.Value);
    }

    if (deviceId.HasValue && deviceId.Value != Guid.Empty)
    {
        records = records.Where(r => r.DeviceId.HasValue && r.DeviceId.Value == deviceId.Value);
    }

    if (TryParseAttendanceStatus(status, out var parsedStatus))
    {
        records = records.Where(r => r.Status == parsedStatus);
    }

    if (!string.IsNullOrWhiteSpace(search))
    {
        var token = search.Trim();
        records = records.Where(r =>
            r.EmployeeCode.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            (employees.TryGetValue(r.EmployeeId, out var emp) && emp.FullName.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(r.DeviceSerialNumber) && r.DeviceSerialNumber.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(r.Workcode) && r.Workcode.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    var rows = records
        .OrderByDescending(r => r.Timestamp)
        .Take(limit)
        .Select(r =>
        {
            employees.TryGetValue(r.EmployeeId, out var employee);
            var deviceName = "-";
            if (r.DeviceId.HasValue && devices.TryGetValue(r.DeviceId.Value, out var device))
            {
                deviceName = string.IsNullOrWhiteSpace(device.Name) ? device.SerialNumber : device.Name;
            }
            else if (!string.IsNullOrWhiteSpace(r.DeviceSerialNumber))
            {
                deviceName = r.DeviceSerialNumber;
            }

            return new
            {
                r.Id,
                r.EmployeeId,
                employeeCode = employee?.EmployeeCode ?? r.EmployeeCode,
                employeeName = employee?.FullName ?? r.EmployeeCode,
                employeeDepartment = ResolveDepartmentNameFromSetup(
                    personnelStore,
                    employee?.Department,
                    DefaultAreaCode,
                    DefaultAreaName,
                    DefaultDepartmentCode,
                    DefaultDepartmentName),
                r.Timestamp,
                status = r.Status.ToString(),
                r.StatusCode,
                r.VerifyMode,
                r.Workcode,
                r.DeviceId,
                r.DeviceSerialNumber,
                deviceName
            };
        });

    return Results.Json(rows);
});

app.MapGet("/api/transactions", (string? from, string? to, string? search, string? deviceSerial, string? area, string? department, string? employeeCode, int? take, bool? usePunchTime, bool? lite, int? page, int? pageSize, bool? approvedOnly) =>
{
    NormalizeEmployeeDepartmentsToSetup(
        repository,
        personnelStore,
        DefaultAreaCode,
        DefaultAreaName,
        DefaultDepartmentCode,
        DefaultDepartmentName);

    string[]? filterCodes = null;
    if (!string.IsNullOrWhiteSpace(area) || !string.IsNullOrWhiteSpace(department))
    {
        var query = service.ListEmployees().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(area))
        {
            query = query.Where(e => string.Equals(e.Area, area, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(department))
        {
            var targetDept = department.Trim();
            var mappedDeptNames = personnelStore.Departments
                .Where(d => string.Equals(d.Name, targetDept, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(d.Code, targetDept, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(d.Id.ToString(), targetDept, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            query = query.Where(e =>
                string.Equals(e.Department, targetDept, StringComparison.OrdinalIgnoreCase) ||
                mappedDeptNames.Contains(e.Department ?? string.Empty));
        }
        var codes = query.Select(e => e.EmployeeCode).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (codes.Length > 0)
        {
            filterCodes = codes;
        }
    }

    if (!string.IsNullOrWhiteSpace(employeeCode))
    {
        var code = employeeCode.Trim();
        filterCodes = filterCodes is null
            ? new[] { code }
            : filterCodes.Where(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    // Server-side pagination mode when page/pageSize are provided
    if (page.HasValue || pageSize.HasValue)
    {
        var pgSize = Math.Min(Math.Max(pageSize.GetValueOrDefault(100), 1), 500);
        var pgNum = Math.Max(page.GetValueOrDefault(1), 1);
        var skip = (pgNum - 1) * pgSize;

        DateTime? fromUtcPg = null;
        DateTime? toUtcPg = null;
        if (DateOnly.TryParse(from, out var fromDatePg))
            fromUtcPg = fromDatePg.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        if (DateOnly.TryParse(to, out var toDatePg))
            toUtcPg = toDatePg.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Local).ToUniversalTime();

        var empByCodePg = service.ListEmployees()
            .GroupBy(e => e.EmployeeCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var fallbackDeptPg = ResolveDepartmentNameFromSetup(personnelStore, null, DefaultAreaCode, DefaultAreaName, DefaultDepartmentCode, DefaultDepartmentName);

        var (dataRows, totalCount) = LoadTransactionDetailsPaged(
            postgresConnectionString, fromUtcPg, toUtcPg, search, deviceSerial, filterCodes, pgSize, skip, empByCodePg, fallbackDeptPg, usePunchTime == true, lite == true);

        return Results.Json(new { data = dataRows, total = totalCount, page = pgNum, pageSize = pgSize });
    }

    var limit = take.GetValueOrDefault(5000);
    if (limit is < 1 or > 20000)
    {
        limit = 5000;
    }

    DateTime? fromUtc = null;
    DateTime? toUtc = null;
    if (DateOnly.TryParse(from, out var fromDate))
    {
        fromUtc = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
    }
    if (DateOnly.TryParse(to, out var toDate))
    {
        toUtc = toDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Local).ToUniversalTime();
    }

    var employeeByCode = service.ListEmployees()
        .GroupBy(e => e.EmployeeCode, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    var rows = LoadTransactionDetails(
        postgresConnectionString,
        fromUtc,
        toUtc,
        search,
        deviceSerial,
        filterCodes,
        limit,
        employeeByCode,
        ResolveDepartmentNameFromSetup(
            personnelStore,
            null,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName),
        usePunchTime == true,
        lite == true,
        approvedOnly == true);

    return Results.Json(rows);
});

app.MapGet("/api/attendance/summary", (string? from, string? to, Guid? employeeId, Guid? deviceId, string? status, string? search) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    DateOnly? fromDate = ParseDate(from);
    DateOnly? toDate = ParseDate(to);
    var employees = service.ListEmployees().ToDictionary(e => e.Id, e => e);
    var records = service.ListAttendance(fromDate, toDate);

    if (employeeId.HasValue && employeeId.Value != Guid.Empty)
    {
        records = records.Where(r => r.EmployeeId == employeeId.Value);
    }

    if (deviceId.HasValue && deviceId.Value != Guid.Empty)
    {
        records = records.Where(r => r.DeviceId.HasValue && r.DeviceId.Value == deviceId.Value);
    }

    if (TryParseAttendanceStatus(status, out var parsedStatus))
    {
        records = records.Where(r => r.Status == parsedStatus);
    }

    if (!string.IsNullOrWhiteSpace(search))
    {
        var token = search.Trim();
        records = records.Where(r =>
            r.EmployeeCode.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            (employees.TryGetValue(r.EmployeeId, out var emp) && emp.FullName.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    var list = records.ToList();
    var uniqueEmployees = list.Select(r => r.EmployeeId).Distinct().Count();
    var total = list.Count;
    var inCount = list.Count(r => r.Status == AttendanceStatus.In);
    var outCount = list.Count(r => r.Status == AttendanceStatus.Out);
    var breakInCount = list.Count(r => r.Status == AttendanceStatus.BreakIn);
    var breakOutCount = list.Count(r => r.Status == AttendanceStatus.BreakOut);
    var unknownCount = total - inCount - outCount - breakInCount - breakOutCount;

    return Results.Ok(new
    {
        total,
        uniqueEmployees,
        inCount,
        outCount,
        breakInCount,
        breakOutCount,
        unknownCount
    });
});

app.MapGet("/api/reports/daily-punch-summary", (string? from, string? to, string? department, string? user, int? take) =>
{
    var limit = take.GetValueOrDefault(5000);
    if (limit is < 1 or > 20000)
    {
        limit = 5000;
    }

    DateOnly? fromDate = ParseDate(from);
    DateOnly? toDate = ParseDate(to);
    var users = service.ListEmployees().ToList();
    var userById = users.ToDictionary(u => u.Id, u => u);

    IEnumerable<AttendanceRecord> records = service.ListAttendance(fromDate, toDate);
    records = records.Where(r => userById.ContainsKey(r.EmployeeId));

    if (!string.IsNullOrWhiteSpace(department) && !string.Equals(department.Trim(), "all", StringComparison.OrdinalIgnoreCase))
    {
        var dept = department.Trim();
        var allowedIds = users
            .Where(u => string.Equals(u.Department, dept, StringComparison.OrdinalIgnoreCase))
            .Select(u => u.Id)
            .ToHashSet();
        records = records.Where(r => allowedIds.Contains(r.EmployeeId));
    }

    if (!string.IsNullOrWhiteSpace(user) && !string.Equals(user.Trim(), "all", StringComparison.OrdinalIgnoreCase))
    {
        var token = user.Trim();
        records = records.Where(r =>
            r.EmployeeCode.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            (userById.TryGetValue(r.EmployeeId, out var e) && e.FullName.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    var grouped = records
        .GroupBy(r => new
        {
            r.EmployeeId,
            Day = DateOnly.FromDateTime(r.Timestamp),
        })
        .Select(g =>
        {
            var ordered = g.OrderBy(x => x.Timestamp).ToList();
            var first = ordered.First();
            var last = ordered.Last();
            userById.TryGetValue(g.Key.EmployeeId, out var emp);
            return new
            {
                employeeId = g.Key.EmployeeId,
                employeeCode = emp?.EmployeeCode ?? first.EmployeeCode,
                employeeName = emp?.FullName ?? first.EmployeeCode,
                department = emp?.Department ?? string.Empty,
                date = g.Key.Day.ToString("yyyy-MM-dd"),
                checkInTime = first.Timestamp,
                checkOutTime = last.Timestamp,
                punchCount = ordered.Count
            };
        })
        .OrderByDescending(x => x.date)
        .ThenBy(x => x.employeeCode, StringComparer.OrdinalIgnoreCase)
        .Take(limit)
        .ToList();

    return Results.Json(grouped);
});

app.MapPost("/api/attendance/clock", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<ClockRequest>();
    if (payload is null || payload.EmployeeId == Guid.Empty)
    {
        return Results.BadRequest(new { message = "Invalid payload." });
    }

    try
    {
        var action = (payload.Action ?? string.Empty).Trim().ToLowerInvariant();
        var record = action switch
        {
            "in" => service.ClockIn(payload.EmployeeId, payload.DeviceId),
            "out" => service.ClockOut(payload.EmployeeId, payload.DeviceId),
            _ => throw new InvalidOperationException("Action must be 'in' or 'out'.")
        };

        AddLog("INFO", "Attendance", $"Manual clock {action} for employee {payload.EmployeeId}.");
        return Results.Json(record);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/attendance/manual", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<AttendanceManualRequest>();
    if (payload is null || payload.EmployeeId == Guid.Empty || !TryParseAttendanceStatus(payload.Status, out var parsedStatus))
    {
        return Results.BadRequest(new { message = "Employee and valid status are required." });
    }

    var employee = repository.GetEmployees().FirstOrDefault(e => e.Id == payload.EmployeeId);
    if (employee is null)
    {
        return Results.NotFound(new { message = "Employee not found." });
    }

    if (!employee.IsActive)
    {
        return Results.BadRequest(new { message = "Employee is inactive." });
    }

    DateTime timestamp = payload.Timestamp ?? DateTime.Now;
    var record = new AttendanceRecord
    {
        Id = Guid.NewGuid(),
        EmployeeId = employee.Id,
        EmployeeCode = employee.EmployeeCode,
        Timestamp = timestamp,
        Status = parsedStatus,
        StatusCode = payload.StatusCode,
        VerifyMode = payload.VerifyMode,
        Workcode = string.IsNullOrWhiteSpace(payload.Workcode) ? null : payload.Workcode.Trim(),
        DeviceId = payload.DeviceId == Guid.Empty ? null : payload.DeviceId,
        DeviceSerialNumber = string.IsNullOrWhiteSpace(payload.DeviceSerialNumber) ? null : payload.DeviceSerialNumber.Trim()
    };

    var saved = repository.AddAttendance(record);
    // Also write a TransactionDetails row so the manual punch appears in Transactions and Reports.
    InsertManualTransactionDetail(
        postgresConnectionString,
        employee.EmployeeCode,
        timestamp,
        payload.StatusCode ?? StatusCodeForAttendanceStatus(parsedStatus),
        payload.VerifyMode,
        string.IsNullOrWhiteSpace(payload.Workcode) ? null : payload.Workcode.Trim(),
        string.IsNullOrWhiteSpace(payload.Remarks) ? null : payload.Remarks.Trim());
    AddLog("INFO", "Attendance", $"Manual attendance added for {employee.EmployeeCode} ({parsedStatus}).");
    return Results.Json(saved);
});

app.MapGet("/api/personnel/verify/{code}", (string code) =>
{
    var employee = repository.GetEmployees().FirstOrDefault(e => string.Equals(e.EmployeeCode, code, StringComparison.OrdinalIgnoreCase));
    if (employee is null) return Results.NotFound(new { message = "Employee not found." });
    if (!employee.IsActive) return Results.BadRequest(new { message = "Employee is inactive." });
    return Results.Ok(new { id = employee.Id, employeeCode = employee.EmployeeCode, fullName = employee.FullName, department = employee.Department });
});

app.MapPost("/api/attendance/request", async (HttpRequest request) =>
{
    var payload = await request.ReadFromJsonAsync<AttendanceManualRequest>();
    if (payload is null || string.IsNullOrWhiteSpace(payload.EmployeeCode))
    {
        return Results.BadRequest(new { message = "Employee code is required." });
    }

    var employee = repository.GetEmployees().FirstOrDefault(e => string.Equals(e.EmployeeCode, payload.EmployeeCode, StringComparison.OrdinalIgnoreCase));
    if (employee is null) return Results.NotFound(new { message = "Employee not found." });
    if (!employee.IsActive) return Results.BadRequest(new { message = "Employee is inactive." });

    DateTime timestamp = payload.Timestamp ?? DateTime.Now;
    
    // For "Request", we only write to TransactionDetails with ParsedOk = 2 (Pending)
    InsertManualTransactionDetail(
        postgresConnectionString,
        employee.EmployeeCode,
        timestamp,
        payload.StatusCode ?? 0,
        payload.VerifyMode ?? 200, 
        string.IsNullOrWhiteSpace(payload.Workcode) ? null : payload.Workcode.Trim(),
        !string.IsNullOrWhiteSpace(payload.Remarks) ? "QR Request: " + payload.Remarks.Trim() : "QR Manual Request",
        2); 

    AddLog("INFO", "Attendance", $"Manual punch requested via QR for {employee.EmployeeCode}.");
    return Results.Ok(new { success = true, message = "Punch request submitted for approval." });
});

app.MapPost("/api/transactions/{id}/approve", async (string id) =>
{
    using var conn = new NpgsqlConnection(postgresConnectionString);
    conn.Open();
    using var selectCmd = new NpgsqlCommand("SELECT * FROM \"E_Attendace\".\"TransactionDetails\" WHERE \"Id\" = @id", conn);
    selectCmd.Parameters.AddWithValue("id", id);
    using var reader = await selectCmd.ExecuteReaderAsync();
    if (!reader.Read()) return Results.NotFound(new { message = "Transaction not found." });

    var employeeCode = reader["EmployeeCode"].ToString();
    var punchTime = (DateTime)reader["PunchTime"];
    var statusCode = reader["StatusCode"] == DBNull.Value ? 0 : (int)reader["StatusCode"];
    var verifyMode = reader["VerifyMode"] == DBNull.Value ? 0 : (int)reader["VerifyMode"];
    var workcode = reader["Workcode"]?.ToString();
    var deviceSerial = reader["DeviceSerialNumber"]?.ToString();
    reader.Close();

    // 1. Mark as Approved (ParsedOk = 1)
    using var updateCmd = new NpgsqlCommand("UPDATE \"E_Attendace\".\"TransactionDetails\" SET \"ParsedOk\" = 1 WHERE \"Id\" = @id", conn);
    updateCmd.Parameters.AddWithValue("id", id);
    await updateCmd.ExecuteNonQueryAsync();

    // 2. Add to actual AttendanceRecords
    var employee = repository.GetEmployees().FirstOrDefault(e => string.Equals(e.EmployeeCode, employeeCode, StringComparison.OrdinalIgnoreCase));
    if (employee != null)
    {
        TryParseAttendanceStatus(statusCode == 1 ? "Out" : "In", out var status);
        repository.AddAttendance(new AttendanceRecord
        {
            Id = Guid.NewGuid(),
            EmployeeId = employee.Id,
            EmployeeCode = employee.EmployeeCode,
            Timestamp = punchTime,
            Status = status,
            StatusCode = statusCode,
            VerifyMode = verifyMode,
            Workcode = workcode,
            DeviceSerialNumber = deviceSerial
        });
    }

    AddLog("INFO", "Attendance", $"Transaction {id} approved manually.");
    return Results.Ok(new { success = true });
});

app.MapPost("/api/transactions/{id}/reject", async (string id) =>
{
    using var conn = new NpgsqlConnection(postgresConnectionString);
    conn.Open();
    // Mark as Rejected (ParsedOk = 3 or just delete? Let's use 3 for Rejected)
    using var updateCmd = new NpgsqlCommand("UPDATE \"E_Attendace\".\"TransactionDetails\" SET \"ParsedOk\" = 3 WHERE \"Id\" = @id", conn);
    updateCmd.Parameters.AddWithValue("id", id);
    var affected = await updateCmd.ExecuteNonQueryAsync();
    if (affected == 0) return Results.NotFound(new { message = "Transaction not found." });

    AddLog("INFO", "Attendance", $"Transaction {id} rejected manually.");
    return Results.Ok(new { success = true });
});

app.MapPut("/api/attendance/{id:guid}", async (Guid id, HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<AttendanceManualRequest>();
    if (payload is null || payload.EmployeeId == Guid.Empty || !TryParseAttendanceStatus(payload.Status, out var parsedStatus))
    {
        return Results.BadRequest(new { message = "Employee and valid status are required." });
    }

    var existing = repository.GetAttendance(null, null).FirstOrDefault(r => r.Id == id);
    if (existing is null)
    {
        return Results.NotFound(new { message = "Attendance not found." });
    }

    var employee = repository.GetEmployees().FirstOrDefault(e => e.Id == payload.EmployeeId);
    if (employee is null)
    {
        return Results.NotFound(new { message = "Employee not found." });
    }

    DateTime timestamp = payload.Timestamp ?? existing.Timestamp;
    var updated = new AttendanceRecord
    {
        Id = existing.Id,
        EmployeeId = employee.Id,
        EmployeeCode = employee.EmployeeCode,
        Timestamp = timestamp,
        Status = parsedStatus,
        StatusCode = payload.StatusCode,
        VerifyMode = payload.VerifyMode,
        Workcode = string.IsNullOrWhiteSpace(payload.Workcode) ? null : payload.Workcode.Trim(),
        DeviceId = payload.DeviceId == Guid.Empty ? null : payload.DeviceId,
        DeviceSerialNumber = string.IsNullOrWhiteSpace(payload.DeviceSerialNumber) ? null : payload.DeviceSerialNumber.Trim()
    };

    var ok = repository.UpdateAttendance(updated);
    if (ok)
    {
        AddLog("INFO", "Attendance", $"Attendance updated: {id}");
    }

    return ok ? Results.Ok(new { success = true }) : Results.NotFound(new { message = "Attendance not found." });
});

app.MapDelete("/api/attendance/{id:guid}", (Guid id) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var ok = repository.DeleteAttendance(id);
    if (ok)
    {
        AddLog("WARN", "Attendance", $"Attendance deleted: {id}");
    }
    return ok ? Results.Ok(new { success = true }) : Results.NotFound(new { message = "Attendance not found." });
});

app.MapPost("/api/attendance/delete", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<AttendanceDeleteRequest>();
    if (payload is null || payload.Ids.Count == 0)
    {
        return Results.BadRequest(new { message = "Select attendance row(s) first." });
    }

    var removed = 0;
    foreach (var id in payload.Ids)
    {
        if (repository.DeleteAttendance(id))
        {
            removed++;
        }
    }

    AddLog("WARN", "Attendance", $"Attendance bulk delete: {removed} row(s).");
    return Results.Ok(new { success = true, removed });
});

app.MapPost("/api/attendance/import", async (HttpRequest request) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var payload = await request.ReadFromJsonAsync<AttendanceImportRequest>();
    if (payload is null || payload.Items.Count == 0)
    {
        return Results.BadRequest(new { message = "No rows to import." });
    }

    var employeesByCode = repository.GetEmployees().ToDictionary(e => e.EmployeeCode, StringComparer.OrdinalIgnoreCase);
    var devicesBySerial = repository.GetDevices().ToDictionary(d => d.SerialNumber, StringComparer.OrdinalIgnoreCase);
    var added = 0;
    var skipped = 0;
    foreach (var row in payload.Items)
    {
        var employeeCode = row.EmployeeCode?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(employeeCode) || !TryParseAttendanceStatus(row.Status, out var parsedStatus))
        {
            skipped++;
            continue;
        }

        if (!employeesByCode.TryGetValue(employeeCode, out var employee))
        {
            skipped++;
            continue;
        }

        var timestamp = row.Timestamp ?? DateTime.Now;
        Guid? deviceId = null;
        string? deviceSerial = null;
        if (!string.IsNullOrWhiteSpace(row.DeviceSerialNumber))
        {
            deviceSerial = row.DeviceSerialNumber.Trim();
            if (devicesBySerial.TryGetValue(deviceSerial, out var device))
            {
                deviceId = device.Id;
            }
        }

        repository.AddAttendance(new AttendanceRecord
        {
            Id = Guid.NewGuid(),
            EmployeeId = employee.Id,
            EmployeeCode = employee.EmployeeCode,
            Timestamp = timestamp,
            Status = parsedStatus,
            StatusCode = row.StatusCode,
            VerifyMode = row.VerifyMode,
            Workcode = string.IsNullOrWhiteSpace(row.Workcode) ? null : row.Workcode.Trim(),
            DeviceId = deviceId,
            DeviceSerialNumber = deviceSerial
        });
        added++;
    }

    AddLog("INFO", "Attendance", $"Attendance import completed. Added={added}, Skipped={skipped}");
    return Results.Ok(new { success = true, added, skipped });
});

app.MapGet("/api/attendance/export", (string? from, string? to, Guid? employeeId, Guid? deviceId, string? status, string? search) =>
{
    if (!serverRunning)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    DateOnly? fromDate = ParseDate(from);
    DateOnly? toDate = ParseDate(to);
    var devices = service.ListDevices().ToDictionary(d => d.Id, d => d);
    var employees = service.ListEmployees().ToDictionary(e => e.Id, e => e);
    IEnumerable<AttendanceRecord> records = service.ListAttendance(fromDate, toDate);

    if (employeeId.HasValue && employeeId.Value != Guid.Empty)
    {
        records = records.Where(r => r.EmployeeId == employeeId.Value);
    }
    if (deviceId.HasValue && deviceId.Value != Guid.Empty)
    {
        records = records.Where(r => r.DeviceId.HasValue && r.DeviceId.Value == deviceId.Value);
    }
    if (TryParseAttendanceStatus(status, out var parsedStatus))
    {
        records = records.Where(r => r.Status == parsedStatus);
    }
    if (!string.IsNullOrWhiteSpace(search))
    {
        var token = search.Trim();
        records = records.Where(r =>
            r.EmployeeCode.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            (employees.TryGetValue(r.EmployeeId, out var emp) && emp.FullName.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    static string Esc(string? value)
    {
        var s = value ?? string.Empty;
        if (s.Contains('"'))
        {
            s = s.Replace("\"", "\"\"");
        }

        return (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            ? $"\"{s}\""
            : s;
    }

    var csv = new StringBuilder();
    csv.AppendLine("Id,Timestamp,EmployeeCode,EmployeeName,Department,Status,StatusCode,VerifyMode,Workcode,DeviceSerial,DeviceName");
    foreach (var r in records.OrderByDescending(r => r.Timestamp).Take(10000))
    {
        employees.TryGetValue(r.EmployeeId, out var employee);
        var deviceName = string.Empty;
        if (r.DeviceId.HasValue && devices.TryGetValue(r.DeviceId.Value, out var device))
        {
            deviceName = string.IsNullOrWhiteSpace(device.Name) ? device.SerialNumber : device.Name;
        }

        csv.Append(Esc(r.Id.ToString())).Append(',')
           .Append(Esc(r.Timestamp.ToString("O"))).Append(',')
           .Append(Esc(employee?.EmployeeCode ?? r.EmployeeCode)).Append(',')
           .Append(Esc(employee?.FullName ?? r.EmployeeCode)).Append(',')
           .Append(Esc(employee?.Department ?? string.Empty)).Append(',')
           .Append(Esc(r.Status.ToString())).Append(',')
           .Append(Esc(r.StatusCode?.ToString())).Append(',')
           .Append(Esc(r.VerifyMode?.ToString())).Append(',')
           .Append(Esc(r.Workcode)).Append(',')
           .Append(Esc(r.DeviceSerialNumber)).Append(',')
           .Append(Esc(deviceName))
           .AppendLine();
    }

    return Results.Text(csv.ToString(), "text/csv");
});

app.MapMethods("/iclock/ping", ["GET"], (HttpRequest request) =>
{
    var serialNumber = TryGetSerialNumber(request);
    var ipAddress = request.HttpContext.Connection.RemoteIpAddress?.ToString();
    var knownDevice = IsKnownDevice(service, serialNumber);
    if (knownDevice && !serverRunning)
    {
        serverRunning = true;
        AddLog("INFO", "Server", "Auto-started by incoming device ping.");
    }

    if (knownDevice && !string.IsNullOrWhiteSpace(serialNumber))
    {
        service.TouchDevice(serialNumber, ipAddress);
        QueueMetadataRefreshCommands(service, serialNumber, metadataProbeUtcBySerial);
    }
    else if (!string.IsNullOrWhiteSpace(serialNumber))
    {
        TouchDiscoveredDevice(discoveredDevices, serialNumber, ipAddress);
    }

    // DIAGNOSTIC: log ping heartbeats so we can see whether the device is heartbeating via /iclock/ping
    // (busy/upload mode — delivers NO commands) instead of /iclock/getrequest (idle mode that pulls
    // commands). pending>0 here means commands are queued but the device is pinging, not pulling them.
    AddLogTransient("INFO", "DeviceEvent", $"PING heartbeat from {serialNumber ?? "unknown"}.",
        $"ip={ipAddress ?? string.Empty}; pending={(knownDevice && !string.IsNullOrWhiteSpace(serialNumber) ? service.GetPendingCommands(serialNumber, 5).Count : 0)}");
    return Results.Text("OK", "text/plain");
});

app.MapMethods("/iclock/getrequest", ["GET", "POST"], async (HttpRequest request) =>
{
    request.HttpContext.Response.Headers.Date = DateTime.UtcNow.ToString("R");

    var serialNumber = TryGetSerialNumber(request);
    var postBody = string.Empty;
    if (string.IsNullOrWhiteSpace(serialNumber) && HttpMethods.IsPost(request.Method))
    {
        postBody = await ReadBodyAsync(request);
        serialNumber = TryExtractSerialNumberFromPayload(postBody);
    }
    var ipAddress = request.HttpContext.Connection.RemoteIpAddress?.ToString();
    serialNumber = ResolveEffectiveDeviceSerialForRequest(service, serialNumber, ipAddress);
    var knownDevice = IsKnownDevice(service, serialNumber);
    if (knownDevice && !serverRunning)
    {
        serverRunning = true;
        AddLog("INFO", "Server", "Auto-started by incoming device getrequest.");
    }
    if (knownDevice && !string.IsNullOrWhiteSpace(serialNumber))
    {
        EnsureDeviceRegistered(service, serialNumber, ipAddress);
        QueueMetadataRefreshCommands(service, serialNumber, metadataProbeUtcBySerial);
    }

    var infoPayloadText = request.Query.TryGetValue("INFO", out var infoPayload)
        ? infoPayload.ToString()
        : string.Empty;
    if (string.IsNullOrWhiteSpace(infoPayloadText) && HttpMethods.IsPost(request.Method))
    {
        if (string.IsNullOrWhiteSpace(postBody))
        {
            postBody = await ReadBodyAsync(request);
        }

        infoPayloadText = TryExtractPayloadValue(postBody, "INFO")
            ?? TryExtractPayloadValue(postBody, "Info")
            ?? string.Empty;
    }

    if (!string.IsNullOrWhiteSpace(infoPayloadText))
    {
        UpdateDiscoveredFromInfo(discoveredDevices, serialNumber, infoPayloadText, ipAddress);
        if (knownDevice)
        {
            ApplyInfoUpdate(service, serialNumber, infoPayloadText, autoUserFetchUtcBySerial, clearAllDataPendingBySerial);
        }
    }

    if (string.IsNullOrWhiteSpace(serialNumber))
    {
        AddLog("WARN", "DeviceEvent", "GETREQUEST without SN; command queue not delivered.", $"ip={ipAddress ?? string.Empty}; method={request.Method}");
        return Results.Text("OK", "text/plain");
    }
    if (!knownDevice)
    {
        TouchDiscoveredDevice(discoveredDevices, serialNumber, ipAddress);
        return Results.Text("OK", "text/plain");
    }

    var commandPayload = BuildPendingCommandPayload(
        service,
        serialNumber,
        ResolveCommandBatchSize(manualUserSyncUntilUtcBySerial, serialNumber),
        commandSerialById);
    if (!string.Equals(commandPayload, "OK", StringComparison.Ordinal))
    {
        AddLog(
            "INFO",
            "DeviceEvent",
            $"Delivered pending commands to {serialNumber}.",
            $"ip={ipAddress ?? string.Empty}; payload={commandPayload.Replace('\r', ' ').Replace('\n', '|')}");
    }
    else
    {
        // DIAGNOSTIC: log idle getrequest polls so we can confirm the device is using getrequest for its
        // heartbeat (which delivers commands). pending>0 here would mean a delivery bug; pending=0 is normal.
        AddLogTransient("INFO", "DeviceEvent", $"GETREQUEST poll from {serialNumber} — no commands (OK).",
            $"ip={ipAddress ?? string.Empty}; pending={service.GetPendingCommands(serialNumber, 5).Count}");
    }
    return Results.Text(commandPayload, "text/plain");
});

app.MapMethods("/iclock/devicecmd", ["POST"], async (HttpRequest request) =>
{
    var body = await ReadBodyAsync(request);
    var sn = TryGetSerialNumber(request);
    if (string.IsNullOrWhiteSpace(sn))
    {
        sn = TryExtractSerialNumberFromPayload(body);
    }
    var knownDevice = IsKnownDevice(service, sn);
    if (knownDevice && !serverRunning)
    {
        serverRunning = true;
        AddLog("INFO", "Server", "Auto-started by incoming devicecmd.");
    }
    if (!knownDevice)
    {
        TouchDiscoveredDevice(discoveredDevices, sn, request.HttpContext.Connection.RemoteIpAddress?.ToString());
        return Results.Text("OK", "text/plain");
    }
    AddLog("INFO", "DeviceEvent", $"DEVICECMD reply from {sn ?? "unknown"}", $"bytes={body.Length}");
    HandleCommandReplies(
        service,
        body,
        commandSerialById,
        discoveredDevices,
        (level, source, message, detail) => AddLog(level, source, message, detail),
        (commandId, serial, commandText, replyText) =>
        {
            if (string.IsNullOrWhiteSpace(serial))
            {
                return;
            }

            if ((commandText ?? string.Empty).Trim().StartsWith("INFO", StringComparison.OrdinalIgnoreCase))
            {
                // Full raw dump for manual inspection (e.g. hunting for an undocumented option key
                // like a device-side duplicate-punch-interval setting) — everything below this only
                // extracts a handful of known keys and discards the rest, so without this the full
                // reply is otherwise never visible anywhere.
                AddLog("INFO", "DeviceEvent", $"CMD=INFO raw reply from {serial}", replyText ?? string.Empty);

                var infoMap = ParseOptionPairs(replyText ?? string.Empty);
                var infoName = FirstValue(infoMap, "~DeviceName", "~DevName", "DevName", "DeviceName", "Alias", "TerminalName");
                var infoModel = NormalizeDeviceModelToken(FirstValue(infoMap, "~DeviceModel", "DeviceModel", "~Model", "Model", "DeviceType", "ProductType", "DevType", "TerminalType"));
                if (!string.IsNullOrWhiteSpace(infoName) || !string.IsNullOrWhiteSpace(infoModel))
                {
                    EnsureDeviceRegistered(service, serial, null, infoName, null, infoModel);
                }

                var parsedUserQty = ParseOptionInt(infoMap, "UserCount", "~UserCount", "UserQty");
                var parsedFpQty = ParseOptionInt(infoMap, "FPCount", "~FPCount", "FpQty", "FingerprintCount");
                var parsedFaceQty = ParseOptionInt(infoMap, "FaceCount", "~FaceCount", "FaceQty");
                var parsedPalmQty = ParseOptionInt(infoMap, "PvCount", "~PvCount", "PalmCount", "PalmQty");
                var parsedAttLogQty = ParseOptionInt(infoMap, "AttLogCount", "~AttLogCount", "TransactionCount", "TransactionQty");

                if (parsedUserQty >= 0 || parsedFpQty >= 0 || parsedFaceQty >= 0 || parsedPalmQty >= 0 || parsedAttLogQty >= 0)
                {
                    discoveredDevices.AddOrUpdate(
                        serial.Trim(),
                        _ => new DiscoveredDevice(serial.Trim(), infoName ?? serial.Trim(), infoModel ?? string.Empty, string.Empty, string.Empty, DateTime.UtcNow, true, Math.Max(0, parsedUserQty), Math.Max(0, parsedFpQty), Math.Max(0, parsedFaceQty), Math.Max(0, parsedPalmQty), parsedAttLogQty),
                        (_, old) => old with
                        {
                            LastActivityUtc = DateTime.UtcNow,
                            IsOnline = true,
                            UserQty = parsedUserQty >= 0 ? parsedUserQty : old.UserQty,
                            FpQty = parsedFpQty >= 0 ? parsedFpQty : old.FpQty,
                            FaceQty = parsedFaceQty >= 0 ? parsedFaceQty : old.FaceQty,
                            PalmQty = parsedPalmQty >= 0 ? parsedPalmQty : old.PalmQty,
                            TransactionQty = parsedAttLogQty >= 0 ? parsedAttLogQty : old.TransactionQty,
                            Name = !string.IsNullOrWhiteSpace(infoName) ? infoName : old.Name,
                            Model = !string.IsNullOrWhiteSpace(infoModel) ? infoModel : old.Model
                        });
                }

                // The CMD=INFO devicecmd reply carries the same FaceFunOn/BioPhotoFun/etc. capability
                // fields as the table=options payload (ApplyOptionsUpdate already parses those
                // correctly) but this handler previously discarded everything except name/model. That
                // left capability flags dependent on table=options ever firing again; in between,
                // ApplyInfoUpdate's much weaker "faceQty > 0" heuristic (driven by the getrequest
                // ?INFO= query string, not this reply) could downgrade SupportsInfraredFace back to
                // false for a device with FaceFunOn=1 but zero faces enrolled yet — which is exactly
                // why a capable device stopped being offered its BIOPHOTO comparison photo.
                var infoFingerFunOn = ParseBoolOption(FirstValue(infoMap, "FingerFunOn"));
                var infoFaceFunOn = ParseBoolOption(FirstValue(infoMap, "FaceFunOn"));
                var infoVisiLightFunOn = ParseBoolOption(FirstValue(infoMap, "VisilightFun", "VisiLightFun", "VisilightFunOn", "VisiLightFunOn"));
                var infoBioDataFun = ParseBoolOption(FirstValue(infoMap, "BioDataFun"));
                var infoBioPhotoFun = ParseBoolOption(FirstValue(infoMap, "BioPhotoFun"));
                if (infoFingerFunOn.HasValue || infoFaceFunOn.HasValue || infoVisiLightFunOn.HasValue ||
                    infoBioDataFun.HasValue || infoBioPhotoFun.HasValue)
                {
                    service.UpdateDeviceProtocolInfo(
                        serial,
                        fingerFunOn: infoFingerFunOn,
                        faceFunOn: infoFaceFunOn,
                        visiLightFunOn: infoVisiLightFunOn,
                        bioDataFun: infoBioDataFun,
                        bioPhotoFun: infoBioPhotoFun);
                }
                if (infoFaceFunOn == true)
                {
                    var infoFaceVersion = FirstValue(infoMap, "FaceVersion", "FaceAlgVer", "FaceAlgoVersion", "FaceAlgorithmVersion") ?? string.Empty;
                    var isVisibleLightFace = infoVisiLightFunOn == true || infoBioDataFun == true;
                    if (double.TryParse(infoFaceVersion, NumberStyles.Float, CultureInfo.InvariantCulture, out var infoFv) && infoFv >= 30.0)
                    {
                        isVisibleLightFace = true;
                    }
                    var deviceForCaps = service.ListDevices().FirstOrDefault(d => string.Equals(d.SerialNumber, serial, StringComparison.OrdinalIgnoreCase));
                    service.UpdateDeviceCapabilities(
                        serial,
                        visibleLight: isVisibleLightFace || (deviceForCaps?.SupportsVisibleLightFace ?? false),
                        infrared: !isVisibleLightFace || (deviceForCaps?.SupportsInfraredFace ?? false),
                        fingerprint: infoFingerFunOn == true || (deviceForCaps?.SupportsFingerprint ?? false));
                }
            }

            var table = InferDataReplyTableFromCommand(commandText);
            if (string.IsNullOrWhiteSpace(table))
            {
                return;
            }
            if (IsUserDataTable(table) && !ShouldImportUserData(service, serial, commandText, manualUserSyncUntilUtcBySerial, clearAllDataPendingBySerial))
            {
                AddLog(
                    "INFO",
                    "DeviceAction",
                    $"Skipped user-data import from non-registration/manual window inactive device: serial={serial}, table={table}, ID={commandId:N}",
                    commandText ?? string.Empty);
                return;
            }

            var dataPayload = ExtractDataPayloadFromDeviceCommandReply(replyText);
            if (string.IsNullOrWhiteSpace(dataPayload))
            {
                return;
            }

            var sourceDevice = service.ListDevices().FirstOrDefault(d =>
                string.Equals(d.SerialNumber, serial, StringComparison.OrdinalIgnoreCase));
            var mappedSetup = ResolveDeviceSetupForSerial(
                personnelStore,
                serial,
                sourceDevice?.Location,
                DefaultAreaCode,
                DefaultAreaName,
                DefaultDepartmentCode,
                DefaultDepartmentName);
            var mappedPosition = ResolvePositionNameFromSetup(personnelStore, null, mappedSetup.Department.Name);

            try
            {
                int count = table.ToUpperInvariant() switch
                {
                    "USERINFO" => HandleUserInfoPayload(
                        repository,
                        personnelStore,
                        dataPayload,
                        mappedSetup.Department.Name,
                        mappedPosition,
                        mappedSetup.Area.Code,
                        mappedSetup.Department.Code),
                    "BIODATA" => HandleBioDataPayload(
                        repository,
                        personnelStore,
                        dataPayload,
                        mappedSetup.Department.Name,
                        mappedPosition,
                        mappedSetup.Area.Code,
                        mappedSetup.Department.Code),
                    "BIOPHOTO" => HandleBioPhotoPayload(
                        repository,
                        personnelStore,
                        dataPayload,
                        mappedSetup.Department.Name,
                        mappedPosition,
                        mappedSetup.Area.Code,
                        mappedSetup.Department.Code),
                    "FINGERTMP" => HandleFingerTemplatePayload(
                        repository,
                        personnelStore,
                        dataPayload,
                        mappedSetup.Department.Name,
                        mappedPosition,
                        mappedSetup.Area.Code,
                        mappedSetup.Department.Code),
                    "FACE" => HandleFaceTemplatePayload(
                        repository,
                        personnelStore,
                        dataPayload,
                        mappedSetup.Department.Name,
                        mappedPosition,
                        mappedSetup.Area.Code,
                        mappedSetup.Department.Code,
                        service,
                        serial),
                    "USERPIC" => HandleUserPicPayload(
                        repository,
                        personnelStore,
                        dataPayload,
                        mappedSetup.Department.Name,
                        mappedPosition,
                        mappedSetup.Area.Code,
                        mappedSetup.Department.Code),
                    "ATTLOG" => repository.RunInTransaction(() =>
                        HandleAttendancePayload(service, serial, dataPayload, allowUnknownEmployeeAutoCreate: false)),
                    "OPERLOG" => repository.RunInTransaction(() =>
                        HandleAttendancePayload(service, serial, dataPayload, allowUnknownEmployeeAutoCreate: false)),
                    _ => 0
                };

                if (table is "USERINFO" or "BIODATA" or "BIOPHOTO" or "FINGERTMP" or "FACE" or "USERPIC")
                {
                    SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
                }

                AddLog(
                    "INFO",
                    "DeviceAction",
                    $"Imported {table} rows from devicecmd reply: serial={serial}, ID={commandId:N}, rows={count}",
                    commandText ?? string.Empty);
            }
            catch (Exception ex)
            {
                AddLog(
                    "WARN",
                    "DeviceAction",
                    $"Failed to import {table} from devicecmd reply: serial={serial}, ID={commandId:N}",
                    ex.Message);
            }
        });

    return Results.Text("OK", "text/plain");
});

app.MapMethods("/iclock/cdata", ["GET", "POST"], async (HttpRequest request) =>
{
    // Per protocol (Attendance_PUSH_Communication_Protocol p.21): the device reads the HTTP
    // Date response header to keep its own clock synchronized in real time. Previously only the
    // GET options=all branch set this explicitly; most firmware actually heartbeats via POST, so
    // set it unconditionally here (covers every response this endpoint returns, GET or POST).
    request.HttpContext.Response.Headers.Date = DateTime.UtcNow.ToString("R");

    var serialNumber = TryGetSerialNumber(request);
    var ipAddress = request.HttpContext.Connection.RemoteIpAddress?.ToString();
    var knownDevice = IsKnownDevice(service, serialNumber);
    if (knownDevice && !serverRunning)
    {
        serverRunning = true;
        AddLog("INFO", "Server", "Auto-started by incoming cdata.");
    }
    if (knownDevice && !string.IsNullOrWhiteSpace(serialNumber))
    {
        EnsureDeviceRegistered(service, serialNumber, ipAddress);
        QueueMetadataRefreshCommands(service, serialNumber, metadataProbeUtcBySerial);
    }

    if (HttpMethods.IsGet(request.Method))
    {
        if (request.Query.TryGetValue("options", out var options) &&
            string.Equals(options.ToString(), "all", StringComparison.OrdinalIgnoreCase))
        {
            request.HttpContext.Response.Headers.Date = DateTime.UtcNow.ToString("R");
            AddLogTransient("INFO", "DeviceEvent", $"CDATA options=all from {serialNumber ?? "unknown"}", $"ip={ipAddress ?? string.Empty}");
            ApplyInitOptionsAllUpdate(service, discoveredDevices, serialNumber, request.Query, ipAddress);

            if (!knownDevice)
            {
                // Device is not registered in the software — reject initialization so it does not show as connected
                return Results.Text("ERROR: Device not registered. Please add this device in the software first.", "text/plain");
            }

            if (string.IsNullOrWhiteSpace(serialNumber))
            {
                AddLog("WARN", "DeviceEvent", "CDATA options=all from known device without serial number.", $"ip={ipAddress ?? string.Empty}");
                return Results.Text("OK", "text/plain");
            }

            var forceFullDataReset = knownDevice && ConsumeFullDataResetPending(fullDataResetUtcBySerial, serialNumber);
            // If "clear all data" is pending for this device, suppress the user/biometric upload
            // (no fetch — even for a registration device) and make sure a CLEAR DATA command is
            // queued. We PEEK the flag (don't consume here): it is cleared in ApplyInfoUpdate once
            // the device reports it is actually empty, so the suppression holds across the whole
            // clear cycle and the data can't be re-fetched in the meantime.
            var clearAllDataPending = knownDevice && IsClearAllDataPending(clearAllDataPendingBySerial, serialNumber);
            if (clearAllDataPending)
            {
                var hasPendingClear = service.GetDeviceCommands(serialNumber, 100).Any(c =>
                    string.IsNullOrWhiteSpace(c.ReplyText) &&
                    (c.CommandText ?? string.Empty).TrimStart().StartsWith("CLEAR DATA", StringComparison.OrdinalIgnoreCase));
                if (!hasPendingClear)
                {
                    service.QueueDeviceCommand(serialNumber, "CLEAR DATA");
                    AddLog("INFO", "Command", $"Clearing all data on device {serialNumber}; fetch suppressed until cleared.");
                }
            }
            var includeUserRealtimeSync = knownDevice && IsRegistrationDevice(service, serialNumber) && !clearAllDataPending;

            // Auto clock-sync on every reconnect/reboot handshake, not just device add/manual sync —
            // see timeSyncUtcBySerial declaration for why. Throttled to once per 5 minutes per device.
            if (knownDevice && !string.IsNullOrWhiteSpace(serialNumber))
            {
                var tzSn = serialNumber.Trim();
                if (!timeSyncUtcBySerial.TryGetValue(tzSn, out var lastSync) ||
                    DateTime.UtcNow - lastSync >= TimeSpan.FromMinutes(5))
                {
                    timeSyncUtcBySerial[tzSn] = DateTime.UtcNow;
                    var syncDevice = service.ListDevices().FirstOrDefault(d =>
                        string.Equals(d.SerialNumber, tzSn, StringComparison.OrdinalIgnoreCase));
                    if (syncDevice is not null)
                    {
                        service.QueueDeviceCommand(tzSn, protocolService.CreateMachineTzCommand(syncDevice));
                        service.QueueDeviceCommand(tzSn, protocolService.CreateTimezoneCommand(syncDevice));
                    }
                    service.QueueDeviceCommand(tzSn, protocolService.CreateTimeSyncCommand(DateTime.Now));
                    service.QueueDeviceCommand(tzSn, $"SHELL date -s \"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\"");
                    service.QueueDeviceCommand(tzSn, "SHELL hwclock -w");
                    AddLog("INFO", "Command", $"Auto clock-sync queued for {tzSn} on options=all handshake.");
                }
            }

            // Registration device (Yes/No = No): auto-fetch ATTENDANCE LOGS ONLY on connect — no user/
            // biometric pull. Registration devices (Yes) already get a full fetch-all on add/enable and
            // sync user data in realtime; a non-registration device should still auto-pull its attendance
            // so the transactions dashboard and reports stay current. Throttled to once per 60s.
            if (knownDevice && !clearAllDataPending && !string.IsNullOrWhiteSpace(serialNumber) &&
                !IsRegistrationDevice(service, serialNumber))
            {
                var attSn = serialNumber.Trim();
                if (!attLogFetchUtcBySerial.TryGetValue(attSn, out var lastAtt) ||
                    DateTime.UtcNow - lastAtt >= TimeSpan.FromSeconds(60))
                {
                    attLogFetchUtcBySerial[attSn] = DateTime.UtcNow;
                    var attStart = attLogRangeStartBySerial.TryGetValue(attSn, out var prevEnd) ? prevEnd : "2000-01-01 00:00:00";
                    var attEnd = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    service.QueueDeviceCommand(attSn, $"DATA QUERY ATTLOG StartTime={attStart}\tEndTime={attEnd}");
                    attLogRangeStartBySerial[attSn] = attEnd;
                    AddLog("INFO", "Command", $"Auto attendance-log fetch queued for non-registration device {attSn}: {attStart} to {attEnd}.");
                }
            }

            var optionsBody = BuildOptionsResponse(
                service,
                serialNumber,
                devicePollDelaySeconds,
                deviceDefaultTimeZoneMinutes,
                deviceEnableAttPhoto,
                forceFullDataReset,
                includeUserRealtimeSync);
            return Results.Text(optionsBody, "text/plain");
        }

        if (knownDevice && !string.IsNullOrWhiteSpace(serialNumber))
        {
            // Compatibility fallback: some firmware polls /iclock/cdata (GET) instead of /iclock/getrequest.
            return Results.Text(
                BuildPendingCommandPayload(
                    service,
                    serialNumber,
                    ResolveCommandBatchSize(manualUserSyncUntilUtcBySerial, serialNumber),
                    commandSerialById),
                "text/plain");
        }

        return Results.Text("OK", "text/plain");
    }

    var table = request.Query.TryGetValue("table", out var tableValue) ? tableValue.ToString() : string.Empty;

    if (!knownDevice)
    {
        if (string.Equals(table, "options", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(serialNumber))
        {
            var optionsPayload = await ReadBodyAsync(request);
            UpdateDiscoveredFromOptions(discoveredDevices, serialNumber, optionsPayload, ipAddress);
        }
        else
        {
            await DrainBodyAsync(request);
            TouchDiscoveredDevice(discoveredDevices, serialNumber, ipAddress);
        }

        return Results.Text("OK", "text/plain");
    }

    (PersonnelArea Area, PersonnelDepartment Department) ResolveMappedSetupForCurrentDevice()
    {
        if (string.IsNullOrWhiteSpace(serialNumber))
        {
            return ResolveDeviceSetup(
                personnelStore,
                DefaultAreaCode,
                DefaultAreaName,
                DefaultDepartmentCode,
                DefaultAreaCode,
                DefaultAreaName,
                DefaultDepartmentCode,
                DefaultDepartmentName);
        }

        var sourceDevice = service.ListDevices().FirstOrDefault(d =>
            string.Equals(d.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase));
        var setup = ResolveDeviceSetupForSerial(
            personnelStore,
            serialNumber,
            sourceDevice?.Location,
            DefaultAreaCode,
            DefaultAreaName,
            DefaultDepartmentCode,
            DefaultDepartmentName);
        return setup;
    }

    if (string.Equals(table, "ATTPHOTO", StringComparison.OrdinalIgnoreCase))
    {
        var saved = 0;
        var payloadBytes = request.ContentLength ?? 0;
        if (deviceEnableAttPhoto)
        {
            (saved, payloadBytes) = await SaveRawAttPhotoPayloadAsync(
                appDataRoot,
                serialNumber,
                request,
                request.HttpContext.RequestAborted);
        }
        else
        {
            await DrainBodyAsync(request);
        }

        TryUpdateDeviceTableStampFromRequest(service, serialNumber, "ATTPHOTO", request.Query);
        AddLog(
            "INFO",
            "DeviceEvent",
            $"CDATA ATTPHOTO from {serialNumber ?? "unknown"}",
            $"saved={saved}; enabled={(deviceEnableAttPhoto ? "1" : "0")}; bytes={payloadBytes}; ip={ipAddress ?? string.Empty}");

        return Results.Text(deviceEnableAttPhoto ? $"OK: {saved}" : "OK", "text/plain");
    }

    var payload = await ReadBodyAsync(request);

    // A "Clear All Data" is in progress for this device: the CLEAR DATA command has been queued
    // but the device hasn't yet confirmed it's actually empty (see IsClearAllDataPending/
    // ConsumeClearAllDataPending in ApplyInfoUpdate). ShouldImportUserData already blocks
    // USERINFO/BIODATA/etc. during this window, but attendance records pushed via ATTLOG/OPERLOG
    // were never gated the same way — the device's pre-clear backlog could still land in
    // TransactionDetails before the clear actually takes effect. Drop (don't persist) attendance
    // data for both tables while a clear is pending; the device's own buffer is wiped once CLEAR
    // DATA executes, so there's nothing legitimate lost by skipping this stale window.
    if ((string.Equals(table, "ATTLOG", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(table, "OPERLOG", StringComparison.OrdinalIgnoreCase)) &&
        IsClearAllDataPending(clearAllDataPendingBySerial, serialNumber))
    {
        AddLog(
            "INFO",
            "DeviceEvent",
            $"CDATA {table} from {serialNumber ?? "unknown"} suppressed — clear-all-data pending.",
            $"bytes={payload.Length}; ip={ipAddress ?? string.Empty}");
        return Results.Text("OK", "text/plain");
    }

    if (string.Equals(table, "ATTLOG", StringComparison.OrdinalIgnoreCase))
    {
        var count = repository.RunInTransaction(() =>
        {
            var added = HandleAttendancePayload(
                service,
                serialNumber,
                payload,
                allowUnknownEmployeeAutoCreate: false);
            TryUpdateDeviceTableStampFromRequest(service, serialNumber, "ATTLOG", request.Query);
            return added;
        });
        QueueTransactionDetails(serialNumber, "ATTLOG", payload);
        if (count > 0 && !string.IsNullOrWhiteSpace(serialNumber))
        {
            discoveredDevices.AddOrUpdate(
                serialNumber.Trim(),
                _ => new DiscoveredDevice(serialNumber.Trim(), serialNumber.Trim(), string.Empty, string.Empty, string.Empty, DateTime.UtcNow, true, 0, 0, 0, 0, count),
                (_, old) => old with { TransactionQty = Math.Max(0, old.TransactionQty) + count, LastActivityUtc = DateTime.UtcNow, IsOnline = true });
        }
        AddLog("INFO", "DeviceEvent", $"CDATA ATTLOG from {serialNumber ?? "unknown"}", $"records={count}; ip={ipAddress ?? string.Empty}");
        AddLog("INFO", "Attendance", $"ATTLOG received from {serialNumber ?? "unknown"}: {count} record(s).");

        // Same idle-heartbeat delivery as OPERLOG below: never deliver commands while a real
        // backlog is actively draining (count > 0) — low-end devices with a large ATTLOG backlog
        // (e.g. hundreds of records) POST table=ATTLOG chunks back-to-back for a while and never
        // call /iclock/getrequest during that burst, so once the chunk is empty (backlog drained)
        // this is the only channel left to hand it the queued commands.
        if (count == 0 && !string.IsNullOrWhiteSpace(serialNumber))
        {
            var idleCommandPayload = BuildPendingCommandPayload(
                service,
                serialNumber,
                ResolveCommandBatchSize(manualUserSyncUntilUtcBySerial, serialNumber),
                commandSerialById);
            if (!string.Equals(idleCommandPayload, "OK", StringComparison.Ordinal))
            {
                AddLog(
                    "INFO",
                    "DeviceEvent",
                    $"Delivered pending commands to {serialNumber} via idle ATTLOG heartbeat.",
                    $"ip={ipAddress ?? string.Empty}; payload={idleCommandPayload.Replace('\r', ' ').Replace('\n', '|')}");
                return Results.Text(idleCommandPayload, "text/plain");
            }
        }

        return Results.Text($"OK: {count}", "text/plain");
    }

    if (string.Equals(table, "OPERLOG", StringComparison.OrdinalIgnoreCase))
    {
        (int Attendance, int UserInfo, int BioData, int BioPhoto, int FingerTmp, int Face, int UserPic) summary = (0, 0, 0, 0, 0, 0, 0);
        try
        {
            var mappedSetup = ResolveMappedSetupForCurrentDevice();
            var mappedPosition = ResolvePositionNameFromSetup(personnelStore, null, mappedSetup.Department.Name);
            var includeUserData = ShouldImportUserData(service, serialNumber, null, manualUserSyncUntilUtcBySerial, clearAllDataPendingBySerial);
            summary = repository.RunInTransaction(() =>
            {
                var imported = HandleOperLogPayload(
                    service,
                    repository,
                    personnelStore,
                    serialNumber,
                    payload,
                    mappedSetup.Department.Name,
                    mappedPosition,
                    mappedSetup.Area.Code,
                    mappedSetup.Department.Code,
                    includeUserData,
                    allowUnknownEmployeeAutoCreate: IsRegistrationDevice(service, serialNumber) || IsManualUserSyncWindowActive(manualUserSyncUntilUtcBySerial, serialNumber));
                TryUpdateDeviceTableStampFromRequest(service, serialNumber, "OPERLOG", request.Query);
                // Some devices (e.g. SpeedFace-V5L) send USERINFO/BIODATA/etc. embedded inside
                // table=OPERLOG rather than as separate tables. Update every stamp that the device
                // included in the query (the calls are no-ops when the key is absent).
                TryUpdateDeviceTableStampFromRequest(service, serialNumber, "USERINFO", request.Query);
                TryUpdateDeviceTableStampFromRequest(service, serialNumber, "BIODATA", request.Query);
                TryUpdateDeviceTableStampFromRequest(service, serialNumber, "BIOPHOTO", request.Query);
                TryUpdateDeviceTableStampFromRequest(service, serialNumber, "FINGERTMP", request.Query);
                TryUpdateDeviceTableStampFromRequest(service, serialNumber, "FACE", request.Query);
                TryUpdateDeviceTableStampFromRequest(service, serialNumber, "USERPIC", request.Query);
                return imported;
            });
            if (summary.UserInfo > 0 || summary.BioData > 0 || summary.BioPhoto > 0 || summary.FingerTmp > 0 || summary.Face > 0 || summary.UserPic > 0)
            {
                SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
                // If the device sends these types embedded in OPERLOG but doesn't include
                // separate stamp keys in the query, advance the stamps to the current time so
                // the device stops re-uploading the same records every cycle.
                var nowStamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!string.IsNullOrWhiteSpace(serialNumber))
                {
                    if (summary.UserInfo > 0) service.UpsertDeviceTableStamp(serialNumber, "USERINFO", nowStamp);
                    if (summary.BioData > 0) service.UpsertDeviceTableStamp(serialNumber, "BIODATA", nowStamp);
                    if (summary.BioPhoto > 0) service.UpsertDeviceTableStamp(serialNumber, "BIOPHOTO", nowStamp);
                    if (summary.FingerTmp > 0) service.UpsertDeviceTableStamp(serialNumber, "FINGERTMP", nowStamp);
                    if (summary.Face > 0) service.UpsertDeviceTableStamp(serialNumber, "FACE", nowStamp);
                    if (summary.UserPic > 0) service.UpsertDeviceTableStamp(serialNumber, "USERPIC", nowStamp);
                }
            }
        }
        catch (Exception ex)
        {
            // Never let a malformed/unexpected OPERLOG payload bubble into the global exception
            // handler: a non-"OK" HTTP response here makes some firmware (e.g. SpeedFace-V5L)
            // assume the upload failed and retry the same payload forever, silently — with zero
            // visibility, since this whole branch's AddLog calls never execute if it throws first.
            var failPreview = payload.Length <= 200 ? payload : payload[..200];
            failPreview = failPreview.Replace('\r', ' ').Replace('\n', ' ').Trim();
            AddLog(
                "WARN",
                "DeviceEvent",
                $"CDATA OPERLOG from {serialNumber ?? "unknown"} failed to process — returning OK anyway.",
                $"error={ex.GetType().Name}: {ex.Message}; bytes={payload.Length}; ip={ipAddress ?? string.Empty}; preview={failPreview}");
        }

        QueueTransactionDetails(serialNumber, "OPERLOG", payload);
        var operlogPreview = payload.Length <= 240 ? payload : payload[..240];
        operlogPreview = operlogPreview.Replace('\r', ' ').Replace('\n', ' ').Trim();
        AddLog(
            "INFO",
            "DeviceEvent",
            $"CDATA OPERLOG from {serialNumber ?? "unknown"}",
            $"attlog={summary.Attendance}; userinfo={summary.UserInfo}; biodata={summary.BioData}; biophoto={summary.BioPhoto}; fingertmp={summary.FingerTmp}; face={summary.Face}; userpic={summary.UserPic}; bytes={payload.Length}; ip={ipAddress ?? string.Empty}; preview={operlogPreview}");
        AddLog("INFO", "Attendance", $"OPERLOG received from {serialNumber ?? "unknown"}: {summary.Attendance} attendance record(s).");

        var processedTotal = summary.Attendance + summary.UserInfo + summary.BioData + summary.BioPhoto + summary.FingerTmp + summary.Face + summary.UserPic;

        // Do not deliver pending commands while an actual upload is in flight (processedTotal > 0) —
        // ZK devices ignore commands riding a non-trivial OPERLOG reply and assume the upload failed,
        // causing hangs and retries. But some firmware (e.g. SpeedFace-V5L) heartbeats exclusively via
        // empty OPERLOG POSTs and never calls /iclock/getrequest, so an idle (0-record) OPERLOG poll is
        // the only channel it ever offers us — deliver queued commands on that idle heartbeat.
        if (processedTotal == 0 && !string.IsNullOrWhiteSpace(serialNumber))
        {
            var idleCommandPayload = BuildPendingCommandPayload(
                service,
                serialNumber,
                ResolveCommandBatchSize(manualUserSyncUntilUtcBySerial, serialNumber),
                commandSerialById);
            if (!string.Equals(idleCommandPayload, "OK", StringComparison.Ordinal))
            {
                AddLog(
                    "INFO",
                    "DeviceEvent",
                    $"Delivered pending commands to {serialNumber} via idle OPERLOG heartbeat.",
                    $"ip={ipAddress ?? string.Empty}; payload={idleCommandPayload.Replace('\r', ' ').Replace('\n', '|')}");
                return Results.Text(idleCommandPayload, "text/plain");
            }
        }

        return Results.Text($"OK: {processedTotal}", "text/plain");
    }

    if (string.Equals(table, "options", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(serialNumber))
    {
        UpdateDiscoveredFromOptions(discoveredDevices, serialNumber, payload, ipAddress);
        ApplyOptionsUpdate(service, serialNumber, ipAddress, payload);
        AddLog("INFO", "DeviceEvent", $"CDATA options from {serialNumber}", $"ip={ipAddress ?? string.Empty}; payloadBytes={payload.Length}");
        // Commands are NOT delivered here — the device re-initializes immediately after with
        // GET options=all, which clears any commands queued from this POST response body.
        // Commands are delivered in the GET options=all response instead.
        return Results.Text("OK", "text/plain");
    }

    if (IsUserDataTable(table) && !ShouldImportUserData(service, serialNumber, null, manualUserSyncUntilUtcBySerial, clearAllDataPendingBySerial))
    {
        AddLog(
            "INFO",
            "DeviceEvent",
            $"Skipped realtime user-data sync because registration/manual window not enabled: {serialNumber ?? "unknown"}",
            $"table={table}; bytes={payload.Length}; ip={ipAddress ?? string.Empty}");
        var commandPayload = TryBuildCdataCommandPayload(
            service,
            serialNumber,
            ipAddress,
            ResolveCommandBatchSize(manualUserSyncUntilUtcBySerial, serialNumber),
            commandSerialById,
            AddLog,
            $"CDATA {table}");
        if (!string.IsNullOrWhiteSpace(commandPayload))
        {
            return Results.Text(commandPayload, "text/plain");
        }
        return Results.Text("OK", "text/plain");
    }

    if (string.Equals(table, "USERINFO", StringComparison.OrdinalIgnoreCase))
    {
        var mappedSetup = ResolveMappedSetupForCurrentDevice();
        var mappedPosition = ResolvePositionNameFromSetup(personnelStore, null, mappedSetup.Department.Name);
        var count = HandleUserInfoPayload(
            repository,
            personnelStore,
            payload,
            mappedSetup.Department.Name,
            mappedPosition,
            mappedSetup.Area.Code,
            mappedSetup.Department.Code);
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
        TryUpdateDeviceTableStampFromRequest(service, serialNumber, "USERINFO", request.Query);
        var payloadPreview = payload.Length <= 240 ? payload : payload[..240];
        payloadPreview = payloadPreview
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        AddLog(
            "INFO",
            "DeviceEvent",
            $"CDATA USERINFO from {serialNumber ?? "unknown"}",
            $"rows={count}; ip={ipAddress ?? string.Empty}; bytes={payload.Length}; preview={payloadPreview}");
        var commandPayload = TryBuildCdataCommandPayload(
            service,
            serialNumber,
            ipAddress,
            ResolveCommandBatchSize(manualUserSyncUntilUtcBySerial, serialNumber),
            commandSerialById,
            AddLog,
            "CDATA USERINFO");
        if (!string.IsNullOrWhiteSpace(commandPayload))
        {
            return Results.Text(commandPayload, "text/plain");
        }
        return Results.Text($"OK: {count}", "text/plain");
    }

    if (string.Equals(table, "BIODATA", StringComparison.OrdinalIgnoreCase))
    {
        var mappedSetup = ResolveMappedSetupForCurrentDevice();
        var mappedPosition = ResolvePositionNameFromSetup(personnelStore, null, mappedSetup.Department.Name);
        var count = HandleBioDataPayload(
            repository,
            personnelStore,
            payload,
            mappedSetup.Department.Name,
            mappedPosition,
            mappedSetup.Area.Code,
            mappedSetup.Department.Code);
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
        TryUpdateDeviceTableStampFromRequest(service, serialNumber, "BIODATA", request.Query);
        var biodataPreview = payload.Length <= 240 ? payload : payload[..240];
        biodataPreview = biodataPreview.Replace('\r', ' ').Replace('\n', ' ').Trim();
        AddLog("INFO", "DeviceEvent", $"CDATA BIODATA from {serialNumber ?? "unknown"}", $"rows={count}; bytes={payload.Length}; ip={ipAddress ?? string.Empty}; preview={biodataPreview}");
        var commandPayload = TryBuildCdataCommandPayload(
            service,
            serialNumber,
            ipAddress,
            ResolveCommandBatchSize(manualUserSyncUntilUtcBySerial, serialNumber),
            commandSerialById,
            AddLog,
            "CDATA BIODATA");
        if (!string.IsNullOrWhiteSpace(commandPayload))
        {
            return Results.Text(commandPayload, "text/plain");
        }
        return Results.Text($"OK: {count}", "text/plain");
    }

    if (string.Equals(table, "BIOPHOTO", StringComparison.OrdinalIgnoreCase))
    {
        var mappedSetup = ResolveMappedSetupForCurrentDevice();
        var mappedPosition = ResolvePositionNameFromSetup(personnelStore, null, mappedSetup.Department.Name);
        var count = HandleBioPhotoPayload(
            repository,
            personnelStore,
            payload,
            mappedSetup.Department.Name,
            mappedPosition,
            mappedSetup.Area.Code,
            mappedSetup.Department.Code);
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
        TryUpdateDeviceTableStampFromRequest(service, serialNumber, "BIOPHOTO", request.Query);
        AddLog("INFO", "DeviceEvent", $"CDATA BIOPHOTO from {serialNumber ?? "unknown"}", $"rows={count}; ip={ipAddress ?? string.Empty}");
        var commandPayload = TryBuildCdataCommandPayload(
            service,
            serialNumber,
            ipAddress,
            ResolveCommandBatchSize(manualUserSyncUntilUtcBySerial, serialNumber),
            commandSerialById,
            AddLog,
            "CDATA BIOPHOTO");
        if (!string.IsNullOrWhiteSpace(commandPayload))
        {
            return Results.Text(commandPayload, "text/plain");
        }
        return Results.Text($"OK: {count}", "text/plain");
    }

    if (string.Equals(table, "FINGERTMP", StringComparison.OrdinalIgnoreCase))
    {
        var mappedSetup = ResolveMappedSetupForCurrentDevice();
        var mappedPosition = ResolvePositionNameFromSetup(personnelStore, null, mappedSetup.Department.Name);
        var count = HandleFingerTemplatePayload(
            repository,
            personnelStore,
            payload,
            mappedSetup.Department.Name,
            mappedPosition,
            mappedSetup.Area.Code,
            mappedSetup.Department.Code);
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
        TryUpdateDeviceTableStampFromRequest(service, serialNumber, "FINGERTMP", request.Query);
        AddLog("INFO", "DeviceEvent", $"CDATA FINGERTMP from {serialNumber ?? "unknown"}", $"rows={count}; ip={ipAddress ?? string.Empty}");
        var commandPayload = TryBuildCdataCommandPayload(
            service,
            serialNumber,
            ipAddress,
            ResolveCommandBatchSize(manualUserSyncUntilUtcBySerial, serialNumber),
            commandSerialById,
            AddLog,
            "CDATA FINGERTMP");
        if (!string.IsNullOrWhiteSpace(commandPayload))
        {
            return Results.Text(commandPayload, "text/plain");
        }
        return Results.Text($"OK: {count}", "text/plain");
    }

    if (string.Equals(table, "FACE", StringComparison.OrdinalIgnoreCase))
    {
        var mappedSetup = ResolveMappedSetupForCurrentDevice();
        var mappedPosition = ResolvePositionNameFromSetup(personnelStore, null, mappedSetup.Department.Name);
        var count = HandleFaceTemplatePayload(
            repository,
            personnelStore,
            payload,
            mappedSetup.Department.Name,
            mappedPosition,
            mappedSetup.Area.Code,
            mappedSetup.Department.Code,
            service,
            serialNumber);
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
        TryUpdateDeviceTableStampFromRequest(service, serialNumber, "FACE", request.Query);
        AddLog("INFO", "DeviceEvent", $"CDATA FACE from {serialNumber ?? "unknown"}", $"rows={count}; ip={ipAddress ?? string.Empty}");
        var commandPayload = TryBuildCdataCommandPayload(
            service,
            serialNumber,
            ipAddress,
            ResolveCommandBatchSize(manualUserSyncUntilUtcBySerial, serialNumber),
            commandSerialById,
            AddLog,
            "CDATA FACE");
        if (!string.IsNullOrWhiteSpace(commandPayload))
        {
            return Results.Text(commandPayload, "text/plain");
        }
        return Results.Text($"OK: {count}", "text/plain");
    }

    if (string.Equals(table, "USERPIC", StringComparison.OrdinalIgnoreCase))
    {
        var mappedSetup = ResolveMappedSetupForCurrentDevice();
        var mappedPosition = ResolvePositionNameFromSetup(personnelStore, null, mappedSetup.Department.Name);
        var count = HandleUserPicPayload(
            repository,
            personnelStore,
            payload,
            mappedSetup.Department.Name,
            mappedPosition,
            mappedSetup.Area.Code,
            mappedSetup.Department.Code);
        SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, postgresConnectionString);
        TryUpdateDeviceTableStampFromRequest(service, serialNumber, "USERPIC", request.Query);
        AddLog("INFO", "DeviceEvent", $"CDATA USERPIC from {serialNumber ?? "unknown"}", $"rows={count}; ip={ipAddress ?? string.Empty}");
        var commandPayload = TryBuildCdataCommandPayload(
            service,
            serialNumber,
            ipAddress,
            ResolveCommandBatchSize(manualUserSyncUntilUtcBySerial, serialNumber),
            commandSerialById,
            AddLog,
            "CDATA USERPIC");
        if (!string.IsNullOrWhiteSpace(commandPayload))
        {
            return Results.Text(commandPayload, "text/plain");
        }
        return Results.Text($"OK: {count}", "text/plain");
    }

    TryUpdateDeviceTableStampFromRequest(service, serialNumber, table, request.Query);
    if (!string.IsNullOrWhiteSpace(table))
    {
        var preview = payload.Length <= 200 ? payload : payload[..200];
        preview = preview.Replace('\r', ' ').Replace('\n', ' ').Trim();
        AddLog("INFO", "DeviceEvent", $"CDATA {table} from {serialNumber ?? "unknown"}", $"bytes={payload.Length}; ip={ipAddress ?? string.Empty}; preview={preview}");
    }
    else
    {
        // Some firmware (e.g. SpeedFace-V5L) heartbeats via POST /iclock/cdata with no `table`
        // param at all instead of table=OPERLOG. Previously this fell through completely silently
        // (no log, no command delivery), so devices that only ever send this kind of heartbeat could
        // never receive queued commands. Treat it the same as an idle OPERLOG poll.
        AddLog("INFO", "DeviceEvent", $"CDATA heartbeat (no table) from {serialNumber ?? "unknown"}", $"bytes={payload.Length}; ip={ipAddress ?? string.Empty}");
    }

    if (string.IsNullOrWhiteSpace(table) && !string.IsNullOrWhiteSpace(serialNumber))
    {
        var idleCommandPayload = BuildPendingCommandPayload(
            service,
            serialNumber,
            ResolveCommandBatchSize(manualUserSyncUntilUtcBySerial, serialNumber),
            commandSerialById);
        if (!string.Equals(idleCommandPayload, "OK", StringComparison.Ordinal))
        {
            AddLog(
                "INFO",
                "DeviceEvent",
                $"Delivered pending commands to {serialNumber} via no-table CDATA heartbeat.",
                $"ip={ipAddress ?? string.Empty}; payload={idleCommandPayload.Replace('\r', ' ').Replace('\n', '|')}");
            return Results.Text(idleCommandPayload, "text/plain");
        }
    }

    return Results.Text("OK", "text/plain");
});

app.MapProductionApiEndpoints();

// Seed per-area access-control defaults (Time Period 1 / Access Group 1 / Combination 1) for any
// areas that already exist, so they are present without opening the Access Control page. Runs in
// the background after startup settles; area-create and device-add paths handle new areas/devices.
_ = Task.Run(async () =>
{
    try
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        using var scope = app.Services.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<ConfigSyncService>();
        await EnsureAccessControlDefaultsForAllAreas(postgresConnectionString, service, personnelStore, sync);
    }
    catch (Exception ex) { Console.WriteLine($"Startup AC defaults seed error: {ex.Message}"); }
});

// Automatic backup scheduler — polls every 10 minutes (a full dump only takes seconds to a couple
// of minutes, so a lightweight poll loop is enough; no cron library needed). Failures are logged
// but don't stop the loop or block the next scheduled run from being computed — a persistently
// broken backup location (e.g. drive unplugged) shouldn't spin the loop or spam failures every poll.
_ = Task.Run(async () =>
{
    while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(10), app.Lifetime.ApplicationStopping);
            var schedule = LoadBackupScheduleFromDb(postgresConnectionString);
            var due = schedule.Enabled &&
                (schedule.NextRunUtc is null || DateTime.Parse(schedule.NextRunUtc, null, System.Globalization.DateTimeStyles.RoundtripKind) <= DateTime.UtcNow);
            if (due)
            {
                await RunAndRecordBackupAsync(postgresConnectionString, "scheduled", AddLog);
                var refreshed = LoadBackupScheduleFromDb(postgresConnectionString);
                SaveBackupScheduleToDb(postgresConnectionString, refreshed.Enabled, refreshed.Frequency, refreshed.RunTime, refreshed.BackupLocation, refreshed.RetentionCount);
            }
        }
        catch (TaskCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            AddLog("WARN", "Backup", "Scheduled backup check failed.", ex.Message);
        }
    }
});

// E-Attendance-Health-Monitor: internal production status monitor.
// Only logs when service health state changes, or as a throttled periodic status update (not every minute).
_ = Task.Run(async () =>
{
    string? lastUnhealthySignature = null;
    DateTime lastLoggedAtUtc = DateTime.MinValue;

    while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        try
        {
            var snapshot = BuildEAttServicesSnapshot();
            // Only flag services that are actively Warning or Critical.
            // Planned status represents future roadmap services and is not an error/issue.
            var unhealthy = snapshot.Services.Where(s =>
                string.Equals(s.Status, "Critical", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Status, "Warning", StringComparison.OrdinalIgnoreCase)).ToList();

            if (unhealthy.Count > 0)
            {
                var currentSignature = string.Join("; ", unhealthy.Select(s => $"{s.Name}={s.Status}: {s.Message}"));
                var now = DateTime.UtcNow;

                // Only write to logs if the issue state changed, or as a reminder once every 4 hours (prevents 1440 logs/day)
                if (currentSignature != lastUnhealthySignature || (now - lastLoggedAtUtc) >= TimeSpan.FromHours(4))
                {
                    AddLog(
                        "WARN",
                        "E-Attendance-Health-Monitor",
                        $"Health monitor found {unhealthy.Count} service issue(s).",
                        currentSignature);

                    lastUnhealthySignature = currentSignature;
                    lastLoggedAtUtc = now;
                }
            }
            else if (lastUnhealthySignature != null)
            {
                // Services recovered back to healthy state
                AddLog(
                    "INFO",
                    "E-Attendance-Health-Monitor",
                    "All services recovered to Healthy.",
                    "Health monitor reports all active services are operating normally.");
                lastUnhealthySignature = null;
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", "E-Attendance-Health-Monitor", "Health monitor check failed.", ex.Message);
        }

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), app.Lifetime.ApplicationStopping);
        }
        catch (TaskCanceledException)
        {
            break;
        }
    }
});

app.Run();

EAttServicesSnapshot BuildEAttServicesSnapshot()
{
    var checkedAtUtc = DateTime.UtcNow;
    var services = new List<EAttServiceStatus>();

    var databaseHealthy = false;
    string databaseMessage;
    try
    {
        using var conn = new NpgsqlConnection(postgresConnectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand("SELECT 1;", conn);
        cmd.ExecuteScalar();
        databaseHealthy = true;
        databaseMessage = "PostgreSQL connection OK.";
    }
    catch (Exception ex)
    {
        databaseMessage = ex.Message;
    }

    var devices = new List<Device>();
    try
    {
        devices = service.ListDevices().ToList();
    }
    catch
    {
        // Keep health endpoint alive even if repository read fails; DB status carries the error.
    }

    var pendingCommandCount = 0;
    try
    {
        pendingCommandCount = devices.Sum(d => service.GetPendingCommands(d.SerialNumber, 2000).Count);
    }
    catch
    {
        pendingCommandCount = -1;
    }

    var queuedPunchRequests = Interlocked.Read(ref transactionDetailsQueuedRequests);
    var persistedPunchRequests = Interlocked.Read(ref transactionDetailsPersistedRequests);
    var failedPunchPersists = Interlocked.Read(ref transactionDetailsPersistFailureCount);
    var punchBacklog = Math.Max(0, queuedPunchRequests - persistedPunchRequests);

    BackupScheduleResponse? backupSchedule = null;
    BackupHistoryResponse? latestBackup = null;
    try
    {
        backupSchedule = LoadBackupScheduleFromDb(postgresConnectionString);
        var (items, _) = LoadBackupHistoryFromDb(postgresConnectionString, 1, 1);
        latestBackup = items.FirstOrDefault();
    }
    catch
    {
        // DB status already reports connection/schema problems.
    }

    services.Add(new EAttServiceStatus(
        "E-Attendance-Health-Monitor",
        "Internal",
        "Healthy",
        "Service health monitor is running inside E.Attendance.Web.",
        checkedAtUtc,
        new Dictionary<string, object?>
        {
            ["apiPort"] = settings.Port,
            ["webPort"] = settings.FrontendPort,
            ["requestCount"] = Interlocked.Read(ref requestCount)
        }));

    services.Add(new EAttServiceStatus(
        "E-Attendance-PostgreSQL",
        "Dependency",
        databaseHealthy ? "Healthy" : "Critical",
        databaseMessage,
        checkedAtUtc,
        new Dictionary<string, object?>
        {
            ["database"] = "E_Attendace",
            ["schema"] = "E_Attendace"
        }));

    services.Add(new EAttServiceStatus(
        "E-Attendance-Punch-Queue",
        "Internal",
        failedPunchPersists > 0 ? "Warning" : punchBacklog > 5000 ? "Warning" : "Healthy",
        failedPunchPersists > 0
            ? $"Punch DB writer had {failedPunchPersists} failure(s)."
            : punchBacklog > 5000
                ? $"Punch queue backlog is high: {punchBacklog} request(s)."
                : "Punch persistence queue is running.",
        checkedAtUtc,
        new Dictionary<string, object?>
        {
            ["queuedRequests"] = queuedPunchRequests,
            ["persistedRequests"] = persistedPunchRequests,
            ["backlogRequests"] = punchBacklog,
            ["failureCount"] = failedPunchPersists,
            ["lastError"] = string.IsNullOrWhiteSpace(transactionDetailsLastPersistError) ? null : transactionDetailsLastPersistError
        }));

    services.Add(new EAttServiceStatus(
        "E-Attendance-Device-Command-Worker",
        "Internal",
        pendingCommandCount < 0 ? "Warning" : pendingCommandCount > 5000 ? "Warning" : "Healthy",
        pendingCommandCount < 0
            ? "Could not read pending device commands."
            : pendingCommandCount > 5000
                ? $"Pending command backlog is high: {pendingCommandCount} command(s)."
                : "Device command queue is within normal range.",
        checkedAtUtc,
        new Dictionary<string, object?>
        {
            ["deviceCount"] = devices.Count,
            ["pendingCommandCount"] = pendingCommandCount
        }));

    services.Add(new EAttServiceStatus(
        "E-Attendance-Backup-Worker",
        "Internal",
        backupSchedule is null
            ? "Warning"
            : latestBackup is not null && string.Equals(latestBackup.Status, "failed", StringComparison.OrdinalIgnoreCase)
                ? "Warning"
                : "Healthy",
        backupSchedule is null
            ? "Could not read backup schedule."
            : latestBackup is not null && string.Equals(latestBackup.Status, "failed", StringComparison.OrdinalIgnoreCase)
                ? $"Latest backup failed: {latestBackup.ErrorMessage}"
                : backupSchedule.Enabled
                    ? "Backup scheduler is enabled."
                    : "Backup scheduler is disabled.",
        checkedAtUtc,
        new Dictionary<string, object?>
        {
            ["enabled"] = backupSchedule?.Enabled,
            ["frequency"] = backupSchedule?.Frequency,
            ["nextRunUtc"] = backupSchedule?.NextRunUtc,
            ["latestStatus"] = latestBackup?.Status,
            ["latestStartedAtUtc"] = latestBackup?.StartedAtUtc
        }));

    services.Add(new EAttServiceStatus(
        "E-Attendance-PostgreSQL-Maintenance",
        "Planned",
        "Planned",
        "Recommended next: scheduled VACUUM/ANALYZE, old log cleanup, and punch archive jobs.",
        checkedAtUtc,
        new Dictionary<string, object?>()));

    var overallStatus = services.Any(s => s.Status == "Critical")
        ? "Critical"
        : services.Any(s => s.Status == "Warning")
            ? "Warning"
            : "Healthy";

    return new EAttServicesSnapshot(checkedAtUtc, overallStatus, services);
}

static byte[] LoadOrCreateJwtSigningKey(string path)
{
    try
    {
        if (File.Exists(path))
        {
            var existing = Convert.FromBase64String(File.ReadAllText(path).Trim());
            if (existing.Length >= 32)
            {
                return existing;
            }
        }
    }
    catch
    {
        // Fall through and regenerate a fresh key if the stored one is unreadable/corrupt.
    }

    var key = RandomNumberGenerator.GetBytes(64);
    File.WriteAllText(path, Convert.ToBase64String(key));
    return key;
}

static string GenerateJwtToken(byte[] signingKey, string issuer, string audience, string userId, string username, string role, TimeSpan lifetime)
{
    var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
    var credentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
        new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(signingKey),
        Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);
    var claims = new[]
    {
        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, userId ?? string.Empty),
        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, username ?? string.Empty),
        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role ?? string.Empty),
    };
    var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
        issuer: issuer,
        audience: audience,
        claims: claims,
        expires: DateTime.UtcNow.Add(lifetime),
        signingCredentials: credentials);
    return handler.WriteToken(token);
}

static string ResolveLicenseDirectory(string? configuredDirectory)
{
    var directory = string.IsNullOrWhiteSpace(configuredDirectory)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PB_Attendance")
        : configuredDirectory;
    var fullPath = Path.GetFullPath(directory);
    Directory.CreateDirectory(fullPath);
    return fullPath;
}

static LicenseAccessSnapshot BuildLicenseAccessSnapshot(
    LicenseGuard guard,
    string trialStatePath,
    int trialDays,
    string licenseDirectory)
{
    var validation = guard.Validate();
    var checkedAtUtc = validation.CheckedAtUtc;
    var trial = LoadOrCreateTrialSnapshot(trialStatePath, trialDays, checkedAtUtc, validation.MachineHash);
    var daysRemaining = CalculateTrialDaysRemaining(checkedAtUtc, trial.ExpiryUtc);
    var trialActive = checkedAtUtc <= trial.ExpiryUtc;
    var effectiveExpiryUtc = validation.IsValid ? validation.ExpiryUtc : trial.ExpiryUtc;

    if (validation.IsValid)
    {
        var message = string.IsNullOrWhiteSpace(validation.Message)
            ? "License is valid."
            : validation.Message;
        return new LicenseAccessSnapshot(
            validation,
            true,
            false,
            trial.TotalDays,
            daysRemaining,
            trial.StartedUtc,
            trial.ExpiryUtc,
            "licensed",
            "License Active",
            message,
            checkedAtUtc,
            effectiveExpiryUtc,
            licenseDirectory);
    }

    if (trialActive)
    {
        var trialMessage = $"Trial active. {daysRemaining} day(s) remaining.";
        return new LicenseAccessSnapshot(
            validation,
            true,
            true,
            trial.TotalDays,
            daysRemaining,
            trial.StartedUtc,
            trial.ExpiryUtc,
            "trial",
            "Trial Active",
            trialMessage,
            checkedAtUtc,
            effectiveExpiryUtc,
            licenseDirectory);
    }

    var baseMessage = string.IsNullOrWhiteSpace(validation.Message)
        ? "Invalid or expired license."
        : validation.Message;
    var lockedMessage = $"{baseMessage} Trial expired on {trial.ExpiryUtc:yyyy-MM-dd}. Activate a license to continue.";
    return new LicenseAccessSnapshot(
        validation,
        false,
        false,
        trial.TotalDays,
        0,
        trial.StartedUtc,
        trial.ExpiryUtc,
        "locked",
        "Activation Required",
        lockedMessage,
        checkedAtUtc,
        effectiveExpiryUtc,
        licenseDirectory);
}

static LicenseTrialSnapshot LoadOrCreateTrialSnapshot(string trialStatePath, int trialDays, DateTimeOffset nowUtc, string machineHash)
{
    var effectiveTrialDays = trialDays > 0 ? trialDays : 30;
    var directory = Path.GetDirectoryName(trialStatePath);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    if (File.Exists(trialStatePath))
    {
        try
        {
            var json = File.ReadAllText(trialStatePath);
            var loaded = JsonSerializer.Deserialize<LicenseTrialFileModel>(json);
            if (loaded is not null)
            {
                var loadedDays = loaded.TotalDays > 0 ? loaded.TotalDays : effectiveTrialDays;
                var startedUtc = DateTimeOffset.TryParse(loaded.StartedUtc, out var parsedStart)
                    ? parsedStart.ToUniversalTime()
                    : nowUtc;
                var expiryUtc = DateTimeOffset.TryParse(loaded.ExpiryUtc, out var parsedExpiry)
                    ? parsedExpiry.ToUniversalTime()
                    : startedUtc.AddDays(loadedDays);
                var lastCheckedUtc = DateTimeOffset.TryParse(loaded.LastCheckedUtc, out var parsedLastChecked)
                    ? parsedLastChecked.ToUniversalTime()
                    : startedUtc;

                var hasSignature = !string.IsNullOrWhiteSpace(loaded.Signature);
                var signatureValid = hasSignature && string.Equals(
                    loaded.Signature,
                    ComputeTrialSignature(loaded.StartedUtc, loaded.ExpiryUtc, loadedDays, loaded.LastCheckedUtc ?? string.Empty, machineHash),
                    StringComparison.OrdinalIgnoreCase);

                var clockRollbackDetected = nowUtc < lastCheckedUtc.AddMinutes(-5);
                if ((hasSignature && !signatureValid) || clockRollbackDetected)
                {
                    var locked = new LicenseTrialSnapshot(nowUtc.AddDays(-loadedDays), nowUtc.AddMinutes(-1), loadedDays);
                    SaveTrialSnapshot(trialStatePath, locked, nowUtc, machineHash);
                    return locked;
                }

                if (expiryUtc <= startedUtc)
                {
                    expiryUtc = startedUtc.AddDays(loadedDays);
                }

                var snapshot = new LicenseTrialSnapshot(startedUtc, expiryUtc, loadedDays);
                SaveTrialSnapshot(trialStatePath, snapshot, nowUtc, machineHash);
                return snapshot;
            }
        }
        catch
        {
            // Recreate trial state below.
        }
    }

    var created = new LicenseTrialSnapshot(nowUtc, nowUtc.AddDays(effectiveTrialDays), effectiveTrialDays);
    SaveTrialSnapshot(trialStatePath, created, nowUtc, machineHash);
    return created;
}

static string ComputeTrialSignature(string startedUtc, string expiryUtc, int totalDays, string lastCheckedUtc, string machineHash)
{
    using var hmac = new HMACSHA256(SHA256.HashData(Encoding.UTF8.GetBytes("PB_ATTENDANCE_TRIAL_LOCK_V1")));
    var payload = $"{startedUtc}|{expiryUtc}|{totalDays}|{lastCheckedUtc}|{machineHash}|v1";
    return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
}

static void SaveTrialSnapshot(string trialStatePath, LicenseTrialSnapshot snapshot, DateTimeOffset lastCheckedUtc, string machineHash)
{
    var startedText = snapshot.StartedUtc.ToString("O");
    var expiryText = snapshot.ExpiryUtc.ToString("O");
    var checkedText = lastCheckedUtc.ToString("O");
    var model = new LicenseTrialFileModel(
        startedText,
        expiryText,
        snapshot.TotalDays,
        checkedText,
        ComputeTrialSignature(startedText, expiryText, snapshot.TotalDays, checkedText, machineHash));
    var json = JsonSerializer.Serialize(model, new JsonSerializerOptions
    {
        WriteIndented = true
    });

    try
    {
        var directory = Path.GetDirectoryName(trialStatePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(trialStatePath))
        {
            var attributes = File.GetAttributes(trialStatePath);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(trialStatePath, attributes & ~FileAttributes.ReadOnly);
            }
        }

        File.WriteAllText(trialStatePath, json);
    }
    catch (UnauthorizedAccessException ex)
    {
        Console.Error.WriteLine($"[License] Unable to write trial state '{trialStatePath}': {ex.Message}");
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"[License] Unable to write trial state '{trialStatePath}': {ex.Message}");
    }
}

static int CalculateTrialDaysRemaining(DateTimeOffset checkedAtUtc, DateTimeOffset trialExpiryUtc)
{
    var remaining = trialExpiryUtc - checkedAtUtc;
    if (remaining <= TimeSpan.Zero)
    {
        return 0;
    }

    return Math.Max(1, (int)Math.Ceiling(remaining.TotalDays));
}

static string SanitizeLicenseKeyFileName(string? fileName)
{
    var safe = Path.GetFileName(fileName ?? string.Empty);
    if (string.IsNullOrWhiteSpace(safe))
    {
        safe = "PB-Activated.key";
    }

    var cleanedChars = safe.Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_').ToArray();
    safe = new string(cleanedChars);
    if (string.IsNullOrWhiteSpace(safe))
    {
        safe = "PB-Activated.key";
    }

    if (!safe.EndsWith(".key", StringComparison.OrdinalIgnoreCase))
    {
        safe += ".key";
    }

    return safe;
}

static string ResolvePostgresConnectionString(string configuredConnectionString)
{
    var configured = new NpgsqlConnectionStringBuilder(configuredConnectionString);
    
    // Use configured values or defaults if missing/empty
    var expectedDb = string.IsNullOrWhiteSpace(configured.Database) ? "E_Attendace" : configured.Database;
    var expectedUser = string.IsNullOrWhiteSpace(configured.Username) ? "E_Attendace" : configured.Username;
    var expectedSchema = string.IsNullOrWhiteSpace(configured.SearchPath) ? "E_Attendace" : configured.SearchPath;

    if (string.IsNullOrWhiteSpace(configured.Password))
    {
        configured.Password = ResolveExpectedPostgreSqlPassword();
    }

    var host = string.IsNullOrWhiteSpace(configured.Host) ? "127.0.0.1" : configured.Host;
    
    // Respect configured port if specified (e.g. 5432 on Linux), otherwise default to 1277.
    var configuredPort = configured.Port > 0 ? configured.Port : 1277;
    
    configured.Host = host;
    configured.Port = configuredPort;
    configured.Database = expectedDb;
    configured.Username = expectedUser;
    configured.SearchPath = expectedSchema;
    
    var finalConnectionString = configured.ConnectionString;

    Console.WriteLine($"[Startup] Database connection: Host={host}, Port={configuredPort}, Database={expectedDb}, User={expectedUser}");

    if (CanOpenConnection(finalConnectionString))
    {
        return finalConnectionString;
    }

    Console.Error.WriteLine(
        $"[Startup] PostgreSQL is unreachable on {host}:{configuredPort} or credentials for {expectedUser}@{expectedDb} are invalid. " +
        "Database connectivity is required for core functionality.");
    return finalConnectionString;
}

static string BuildDefaultPostgresConnectionString()
{
    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1",
        Port = 1277,
        Database = "E_Attendace",
        Username = "E_Attendace",
        Password = ResolveExpectedPostgreSqlPassword(),
        SearchPath = "E_Attendace",
        IncludeErrorDetail = true,
        MaxPoolSize = 100,
        ConnectionLifetime = 300
    };

    return builder.ConnectionString;
}

static void TryStartPostgreSqlService(string connectionString)
{
    try
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (builder.Port <= 0)
        {
            builder.Port = 1277;
        }

        if (builder.Port != 1277)
        {
            return;
        }

        var candidateServices = new[] { "E_Attendance_PostgreSQL", "E-Attendance-PostgreSQL" };
        foreach (var postgresServiceName in candidateServices)
        {
            if (RunHiddenProcess("cmd.exe", $"/c sc query \"{postgresServiceName}\" | findstr /I \"RUNNING\" >nul") == 0)
            {
                return;
            }

            if (RunHiddenProcess("cmd.exe", $"/c sc query \"{postgresServiceName}\" >nul 2>&1") == 0)
            {
                _ = RunHiddenProcess("sc.exe", $"config \"{postgresServiceName}\" start= auto");
                _ = RunHiddenProcess("sc.exe", $"start \"{postgresServiceName}\"");
                Console.WriteLine($"[Startup] Requested start for {postgresServiceName} service.");
                return;
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Startup] Unable to auto-start PostgreSQL service: {ex.Message}");
    }
}

static void WaitForPostgresConnection(string connectionString, int maxWaitSeconds)
{
    if (CanOpenConnection(connectionString))
    {
        return;
    }

    var started = DateTime.UtcNow;
    while ((DateTime.UtcNow - started).TotalSeconds < maxWaitSeconds)
    {
        Thread.Sleep(1000);
        if (CanOpenConnection(connectionString))
        {
            Console.WriteLine("[Startup] PostgreSQL connection is ready.");
            return;
        }
    }

    var builder = new NpgsqlConnectionStringBuilder(connectionString);
    Console.Error.WriteLine(
        $"[Startup] PostgreSQL still unreachable after waiting {maxWaitSeconds}s on {builder.Host}:{builder.Port}.");
}

static int RunHiddenProcess(string fileName, string arguments)
{
    try
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.Start();
        process.WaitForExit(7000);
        return process.ExitCode;
    }
    catch
    {
        return -1;
    }
}

// Returns true only when the core tables actually exist — used by the startup retry loop so we keep
// re-running EnsurePostgresSchema until PostgreSQL is fully ready and the schema is really created.
static bool AuthUsersTableExists(NpgsqlConnection conn)
{
    try
    {
        using var cmd = new NpgsqlCommand(
            "SELECT to_regclass('\"E_Attendace\".\"auth_users\"') IS NOT NULL;", conn);
        return cmd.ExecuteScalar() is bool b && b;
    }
    catch
    {
        return false;
    }
}

// Creates ONLY the auth_users table (+ default admin) independently of the full schema
// bootstrap. Used as a login-time self-heal so a fresh install whose background schema
// setup hasn't finished (or partially failed) never blocks login with
// "relation E_Attendace.auth_users does not exist".
static void EnsureAuthUsersTable(NpgsqlConnection conn)
{
    TryExec(conn, """CREATE SCHEMA IF NOT EXISTS "E_Attendace";""");
    TryExec(conn, """
        CREATE TABLE IF NOT EXISTS "E_Attendace"."auth_users" (
            "Id" BIGSERIAL PRIMARY KEY,
            "UserName" TEXT NOT NULL,
            "PasswordHash" TEXT NOT NULL DEFAULT '',
            "FullName" TEXT NULL,
            "Role" TEXT NOT NULL DEFAULT 'Operator',
            "Status" TEXT NOT NULL DEFAULT 'active',
            "FeatureAccessJson" jsonb NOT NULL DEFAULT '{}',
            "CreatedAtUtc" timestamptz NOT NULL DEFAULT NOW(),
            "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
        );
        """);
    TryExec(conn, """ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "UserName" TEXT;""");
    TryExec(conn, """ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "PasswordHash" TEXT NOT NULL DEFAULT '';""");
    TryExec(conn, """ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "FullName" TEXT NULL;""");
    TryExec(conn, """ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "Role" TEXT NOT NULL DEFAULT 'Operator';""");
    TryExec(conn, """ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "Status" TEXT NOT NULL DEFAULT 'active';""");
    TryExec(conn, """ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "FeatureAccessJson" jsonb NOT NULL DEFAULT '{}';""");
    TryExec(conn, """ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "CreatedAtUtc" timestamptz NOT NULL DEFAULT NOW();""");
    TryExec(conn, """ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW();""");
    TryExec(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "UX_auth_users_UserName" ON "E_Attendace"."auth_users" (LOWER(TRIM("UserName")));""");
    TryExec(conn, """INSERT INTO "E_Attendace"."auth_users" ("UserName","PasswordHash","FullName","Role","Status","FeatureAccessJson") VALUES ('admin',md5('PB@TechZone'),'Admin User','Admin','active','{}') ON CONFLICT DO NOTHING;""");
}

static bool PostgresCoreTablesExist(string connectionString)
{
    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT to_regclass('\"E_Attendace\".\"auth_users\"') IS NOT NULL " +
            "AND to_regclass('\"E_Attendace\".\"Devices\"') IS NOT NULL " +
            "AND to_regclass('\"E_Attendace\".\"Employees\"') IS NOT NULL;",
            conn);
        return cmd.ExecuteScalar() is bool b && b;
    }
    catch
    {
        return false;
    }
}

/// <summary>
/// Idempotent startup migration that:
/// 1. Backfills a stub row in "users" for every orphan pin in biometric_photos /
///    biometric_templates so no existing data is stranded by the FK.
/// 2. Upgrades both FK constraints to DEFERRABLE INITIALLY DEFERRED so they are
///    checked at COMMIT time rather than per-row INSERT — preventing the 23503
///    error when biometric data arrives before the matching USERINFO push.
/// Safe to call repeatedly (all statements are idempotent).
/// </summary>
static void MigrateBiometricFkConstraints(string connectionString)
{
    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();

        // ── 1. Backfill orphan user stubs ────────────────────────────────────────
        // Any pin in biometric_photos or biometric_templates that is NOT yet in
        // users would cause a FK violation on the next write. Insert a minimal stub
        // row (ON CONFLICT DO NOTHING so existing records are never touched).
        TryExec(conn, """
            INSERT INTO "E_Attendace"."users" (pin, name, created_at, updated_at)
            SELECT DISTINCT user_pin, user_pin, NOW(), NOW()
            FROM   "E_Attendace"."biometric_photos"
            WHERE  user_pin NOT IN (SELECT pin FROM "E_Attendace"."users")
            ON CONFLICT (pin) DO NOTHING;
            """);

        TryExec(conn, """
            INSERT INTO "E_Attendace"."users" (pin, name, created_at, updated_at)
            SELECT DISTINCT user_pin, user_pin, NOW(), NOW()
            FROM   "E_Attendace"."biometric_templates"
            WHERE  user_pin NOT IN (SELECT pin FROM "E_Attendace"."users")
            ON CONFLICT (pin) DO NOTHING;
            """);

        // ── 2. Make FK constraints DEFERRABLE ─────────────────────────────────────
        // Drop the old non-deferrable constraint (if present) and re-add as
        // DEFERRABLE INITIALLY DEFERRED. The IF NOT EXISTS / DO $$ guards make
        // this safe to re-run on an already-migrated database.

        // biometric_photos
        TryExec(conn, """
            DO $$ BEGIN
                -- Drop only if it is NOT already deferrable
                IF EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = 'biometric_photos_user_pin_fkey'
                      AND conrelid = '"E_Attendace".biometric_photos'::regclass
                      AND NOT condeferrable
                ) THEN
                    ALTER TABLE "E_Attendace"."biometric_photos"
                        DROP CONSTRAINT biometric_photos_user_pin_fkey;
                END IF;

                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = 'biometric_photos_user_pin_fkey'
                      AND conrelid = '"E_Attendace".biometric_photos'::regclass
                ) THEN
                    ALTER TABLE "E_Attendace"."biometric_photos"
                        ADD CONSTRAINT biometric_photos_user_pin_fkey
                        FOREIGN KEY (user_pin)
                        REFERENCES "E_Attendace"."users"(pin)
                        ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED;
                END IF;
            END $$;
            """);

        // biometric_templates
        TryExec(conn, """
            DO $$ BEGIN
                IF EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = 'biometric_templates_user_pin_fkey'
                      AND conrelid = '"E_Attendace".biometric_templates'::regclass
                      AND NOT condeferrable
                ) THEN
                    ALTER TABLE "E_Attendace"."biometric_templates"
                        DROP CONSTRAINT biometric_templates_user_pin_fkey;
                END IF;

                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = 'biometric_templates_user_pin_fkey'
                      AND conrelid = '"E_Attendace".biometric_templates'::regclass
                ) THEN
                    ALTER TABLE "E_Attendace"."biometric_templates"
                        ADD CONSTRAINT biometric_templates_user_pin_fkey
                        FOREIGN KEY (user_pin)
                        REFERENCES "E_Attendace"."users"(pin)
                        ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED;
                END IF;
            END $$;
            """);

        Console.WriteLine("[Startup] Biometric FK constraints verified/migrated to DEFERRABLE.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Startup] MigrateBiometricFkConstraints failed (non-fatal): {ex.Message}");
    }
}

static void EnsurePostgresSchema(string connectionString)
{
    try
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var dbName = builder.Database ?? "E_Attendace";
        var dbUser = builder.Username ?? "E_Attendace";

        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        TryExec(conn, """CREATE SCHEMA IF NOT EXISTS "E_Attendace";""");
        TryExec(conn, """SET search_path TO "E_Attendace";""");
        TryExec(conn, $"""GRANT USAGE ON SCHEMA "E_Attendace" TO "{dbUser}";""");
        TryExec(conn, $"""GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA "E_Attendace" TO "{dbUser}";""");
        TryExec(conn, $"""GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA "E_Attendace" TO "{dbUser}";""");
        TryExec(conn, $"""ALTER DEFAULT PRIVILEGES IN SCHEMA "E_Attendace" GRANT ALL ON TABLES TO "{dbUser}";""");
        TryExec(conn, $"""ALTER DEFAULT PRIVILEGES IN SCHEMA "E_Attendace" GRANT ALL ON SEQUENCES TO "{dbUser}";""");
        TryExec(conn, $"""ALTER DATABASE "{dbName}" SET search_path TO "E_Attendace";""");
        TryExec(conn, $"""ALTER ROLE "{dbUser}" IN DATABASE "{dbName}" SET search_path TO "E_Attendace";""");
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."Employees" (
                "Id" TEXT PRIMARY KEY,
                "EmployeeCode" TEXT NOT NULL DEFAULT '',
                "FirstName" TEXT NOT NULL DEFAULT '',
                "LastName" TEXT NOT NULL DEFAULT '',
                "FullName" TEXT NOT NULL DEFAULT '',
                "Email" TEXT NULL,
                "Phone" TEXT NULL,
                "BranchId" INTEGER NULL,
                "DepartmentId" INTEGER NULL,
                "DesignationId" INTEGER NULL,
                "ShiftId" INTEGER NULL,
                "JoinDate" date NULL,
                "Status" TEXT NOT NULL DEFAULT 'active',
                "CreatedAt" timestamptz NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamptz NOT NULL DEFAULT NOW()
            );
            """);

        // Migrate legacy Employees table if needed
        TryExec(conn, """
            DO $$
            BEGIN
                -- Rename EmployeeCode to EmployeeCode if it exists and EmployeeCode does not
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'Employees' AND column_name = 'EmployeeCode') 
                   AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'Employees' AND column_name = 'EmployeeCode') THEN
                    ALTER TABLE "E_Attendace"."Employees" RENAME COLUMN "EmployeeCode" TO "EmployeeCode";
                END IF;
            END $$;
            """);

        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "EmployeeCode" TEXT NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "FirstName" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "LastName" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "FullName" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "Email" TEXT NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "Phone" TEXT NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "BranchId" INTEGER NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "DesignationId" INTEGER NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "ShiftId" INTEGER NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "JoinDate" date NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "Status" TEXT NOT NULL DEFAULT 'active';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "UpdatedAt" timestamptz NOT NULL DEFAULT NOW();""");
        // ── Biometric/sync columns required by SqliteAttendanceRepository.GetEmployees() ──
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "Area" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "Department" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "AreaId" INTEGER NOT NULL DEFAULT 1;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "DepartmentsJson" TEXT NOT NULL DEFAULT '[]';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "Position" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "CardNumber" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "Password" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "MobileNumber" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "Gender" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "Privilege" INTEGER NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "VerifyMode" INTEGER NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "FaceTemplateVisible" TEXT NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "FaceTemplateInfrared" TEXT NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "ProfilePhoto" TEXT NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "FingerprintTemplates" TEXT NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "CreatedAt" timestamptz NOT NULL DEFAULT NOW();""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "SyncedAt" timestamptz NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Employees" ADD COLUMN IF NOT EXISTS "IsActive" INTEGER NOT NULL DEFAULT 1;""");
        TryExec(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Employees_EmployeeCode" ON "E_Attendace"."Employees" ("EmployeeCode") WHERE "EmployeeCode" IS NOT NULL AND "EmployeeCode" <> '';""");
        // Normalise Employees.IsActive to INTEGER (C# sends 0/1, not true/false)
        TryExec(conn, """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='E_Attendace' AND table_name='Employees' AND column_name='IsActive' AND data_type='boolean') THEN
                    ALTER TABLE "E_Attendace"."Employees" ALTER COLUMN "IsActive" TYPE INTEGER USING ("IsActive"::integer);
                END IF;
            END $$;
            """);

        TryExec(conn, """
            DO $$
            BEGIN
                -- NOTE: We keep Employees.Id as TEXT (not uuid) so our text params work
                -- The uuid→TEXT migration runs later in this function
                -- Populate EmployeeCode from EmployeeCode if still null
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'Employees' AND column_name = 'EmployeeCode') 
                   AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'Employees' AND column_name = 'EmployeeCode') THEN
                    UPDATE "E_Attendace"."Employees" SET "EmployeeCode" = "EmployeeCode" WHERE "EmployeeCode" IS NULL;
                END IF;
                -- FullName split
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'Employees' AND column_name = 'FullName') THEN
                    UPDATE "E_Attendace"."Employees"
                    SET
                        "FirstName" = split_part("FullName", ' ', 1),
                        "LastName" = substring("FullName" from position(' ' in "FullName") + 1)
                    WHERE ("FirstName" = '' OR "FirstName" IS NULL) AND "FullName" <> '';
                END IF;
                -- Status mapping
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'Employees' AND column_name = 'IsActive') THEN
                    UPDATE "E_Attendace"."Employees" SET "Status" = 'active' WHERE "IsActive" = 1 AND "Status" = 'active';
                    UPDATE "E_Attendace"."Employees" SET "Status" = 'inactive' WHERE "IsActive" = 0 AND "Status" = 'active';
                END IF;
            END $$;
            """);

        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."DevicePunchLogs" (
                "Id" TEXT PRIMARY KEY,
                "EmployeeId" TEXT NOT NULL,
                "EmployeeCode" TEXT NOT NULL,
                "Timestamp" TEXT NOT NULL,
                "Status" INTEGER NOT NULL,
                "StatusCode" INTEGER NULL,
                "VerifyMode" INTEGER NULL,
                "Workcode" TEXT NULL,
                "DeviceId" TEXT NULL,
                "DeviceSerialNumber" TEXT NULL
            );
            """);
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_DevicePunchLogs_Timestamp" ON "E_Attendace"."DevicePunchLogs" ("Timestamp");""");
        TryExec(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_DevicePunchLogs_Dedup" 
            ON "E_Attendace"."DevicePunchLogs" (COALESCE("DeviceSerialNumber",''), "EmployeeCode", "Timestamp", COALESCE("StatusCode",-1), COALESCE("VerifyMode",-1), COALESCE("Workcode",''));
            """);

        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."DeviceSyncLogs" (
                "Id" TEXT PRIMARY KEY,
                "DeviceSerialNumber" TEXT NOT NULL,
                "Type" INTEGER NOT NULL,
                "Status" INTEGER NOT NULL,
                "CommandText" TEXT NOT NULL,
                "ResponseText" TEXT NULL,
                "CreatedUtc" TEXT NOT NULL,
                "CompletedUtc" TEXT NULL
            );
            """);
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_DeviceSyncLogs_DeviceStatus" ON "E_Attendace"."DeviceSyncLogs" ("DeviceSerialNumber", "Status", "CreatedUtc");""");

        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."DeviceProtocolCommands" (
                "Id" TEXT PRIMARY KEY,
                "DeviceSerialNumber" TEXT NOT NULL,
                "CommandText" TEXT NOT NULL,
                "CreatedUtc" TEXT NOT NULL,
                "SentUtc" TEXT NULL,
                "ReplyText" TEXT NULL
            );
            """);
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_DeviceProtocolCommands_Pending" ON "E_Attendace"."DeviceProtocolCommands" ("DeviceSerialNumber", "SentUtc");""");

        // AttendanceRecords — RAW PUNCH schema matching SqliteAttendanceRepository.
        // (Id/EmployeeId/DeviceId are TEXT, Timestamp is timestamptz, Status is INTEGER.)
        // This is the single canonical definition; the old "processed" schema was removed
        // because it conflicted with the repository and broke device punches.
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."AttendanceRecords" (
                "Id" TEXT PRIMARY KEY,
                "EmployeeId" TEXT NOT NULL DEFAULT '',
                "EmployeeCode" TEXT NOT NULL DEFAULT '',
                "Timestamp" timestamptz NOT NULL DEFAULT NOW(),
                "Status" INTEGER NOT NULL DEFAULT 0,
                "StatusCode" INTEGER NULL,
                "VerifyMode" INTEGER NULL,
                "Workcode" TEXT NULL,
                "DeviceId" TEXT NULL,
                "DeviceSerialNumber" TEXT NULL,
                "AreaId" INTEGER NOT NULL DEFAULT 1,
                "DepartmentId" INTEGER NOT NULL DEFAULT 1
            );
            """);
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_AttendanceRecords_Timestamp" ON "E_Attendace"."AttendanceRecords" ("Timestamp");""");

        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."AppLogs" (
                "Id" TEXT PRIMARY KEY,
                "TimestampUtc" timestamptz NOT NULL,
                "Level" TEXT NOT NULL,
                "Source" TEXT NOT NULL,
                "Message" TEXT NOT NULL,
                "Detail" TEXT NULL
            );
            """);
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_AppLogs_TimestampUtc" ON "E_Attendace"."AppLogs" ("TimestampUtc" DESC);""");
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."TransactionDetails" (
                "Id" TEXT PRIMARY KEY,
                "ReceivedUtc" timestamptz NOT NULL,
                "DeviceSerialNumber" TEXT NULL,
                "SourceTable" TEXT NOT NULL,
                "RawLine" TEXT NOT NULL,
                "EmployeeCode" TEXT NULL,
                "PunchTime" timestamptz NULL,
                "StatusCode" INTEGER NULL,
                "VerifyMode" INTEGER NULL,
                "Workcode" TEXT NULL,
                "ParsedOk" INTEGER NOT NULL DEFAULT 0
            );
            """);
        TryExec(conn, """ALTER TABLE "E_Attendace"."TransactionDetails" ADD COLUMN IF NOT EXISTS "Remarks" TEXT NULL;""");
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_TransactionDetails_ReceivedUtc" ON "E_Attendace"."TransactionDetails" ("ReceivedUtc" DESC);""");
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_TransactionDetails_PunchTime" ON "E_Attendace"."TransactionDetails" ("PunchTime" DESC);""");
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_TransactionDetails_DeviceSerial" ON "E_Attendace"."TransactionDetails" ("DeviceSerialNumber");""");
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_TransactionDetails_EmployeeCode" ON "E_Attendace"."TransactionDetails" ("EmployeeCode");""");
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_TransactionDetails_Source_Parsed" ON "E_Attendace"."TransactionDetails" ("SourceTable","ParsedOk");""");
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_TransactionDetails_Employee_Punch" ON "E_Attendace"."TransactionDetails" ("EmployeeCode","PunchTime" DESC);""");
        TryExec(conn, """
            DROP INDEX IF EXISTS "E_Attendace"."UX_TransactionDetails_Source_Raw";
            DROP INDEX IF EXISTS "E_Attendace"."UX_TransactionDetails_SourceSerialRaw";
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_TransactionDetails_SourceSerialRaw"
            ON "E_Attendace"."TransactionDetails" (COALESCE("DeviceSerialNumber",''), "SourceTable", md5("RawLine"));
            """);
        TryExec(conn, """
            INSERT INTO "E_Attendace"."TransactionDetails"
            ("Id","ReceivedUtc","DeviceSerialNumber","SourceTable","RawLine","EmployeeCode","PunchTime","StatusCode","VerifyMode","Workcode","ParsedOk")
            SELECT
                ar."Id",
                COALESCE(ar."Timestamp", NOW()),
                ar."DeviceSerialNumber",
                'ATTLOG',
                COALESCE(ar."EmployeeCode", '') || E'\t' || to_char(COALESCE(ar."Timestamp", NOW()), 'YYYY-MM-DD HH24:MI:SS') || E'\t' || ar."Id",
                ar."EmployeeCode",
                ar."Timestamp",
                ar."StatusCode",
                ar."VerifyMode",
                ar."Workcode",
                1
            FROM "E_Attendace"."AttendanceRecords" ar
            ON CONFLICT DO NOTHING;
            """);
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."Devices" (
                "Id" TEXT PRIMARY KEY,
                "SerialNumber" TEXT NOT NULL UNIQUE,
                "Name" TEXT NOT NULL DEFAULT '',
                "Model" TEXT NOT NULL DEFAULT '',
                "IpAddress" TEXT NOT NULL DEFAULT '',
                "Location" TEXT NOT NULL DEFAULT '',
                "TimeZone" TEXT NOT NULL DEFAULT '+5:45',
                "Status" INTEGER NOT NULL DEFAULT 0,
                "IsOnline" INTEGER NOT NULL DEFAULT 0,
                "LastSeenUtc" timestamptz NULL,
                "LastPushTime" timestamptz NULL,
                "SecretKey" TEXT NULL,
                "CreatedAt" timestamptz NOT NULL DEFAULT NOW()
            );
            """);

        // ── All columns required by SqliteAttendanceRepository.GetDevices() ──
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "Name" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "Model" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "IpAddress" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "Location" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "TimeZone" TEXT NOT NULL DEFAULT '+5:45';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "SupportsVisibleLightFace" INTEGER NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "SupportsInfraredFace" INTEGER NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "SupportsFingerprint" INTEGER NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "FaceAlgorithmVersion" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "FingerprintAlgorithmVersion" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "FirmwareVersion" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "DeviceType" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "PushVer" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "Language" INTEGER NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "MultiBioDataSupport" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "MultiBioPhotoSupport" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "MultiBioVersion" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "MaxMultiBioDataCount" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "MaxMultiBioPhotoCount" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "FingerFunOn" INTEGER NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "FaceFunOn" INTEGER NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "VisiLightFunOn" INTEGER NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "BioDataFun" INTEGER NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "BioPhotoFun" INTEGER NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "SupportsPalm" INTEGER NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "SupportsFingerVein" INTEGER NOT NULL DEFAULT 0;""");
        // Normalise ALL device boolean-like columns to INTEGER 0/1.
        // Our C# code always sends 0/1 integers — never true/false booleans.
        // Any column that is BOOLEAN (legacy schema) gets converted back to INTEGER.
        // CRITICAL: a boolean column may carry a DEFAULT false/true that cannot auto-cast to
        // integer — we MUST DROP DEFAULT first, then change type, then restore DEFAULT 0.
        // Each column is its OWN TryExec/DO block so one failure can never roll back the rest.
        TryExec(conn, """
            DO $$
            DECLARE col TEXT;
            BEGIN
                FOREACH col IN ARRAY ARRAY[
                    'IsOnline','IsRegistrationDevice','IsRegistered',
                    'FingerFunOn','FaceFunOn','VisiLightFunOn','BioDataFun','BioPhotoFun',
                    'SupportsPalm','SupportsFingerVein','SupportsVisibleLightFace',
                    'SupportsInfraredFace','SupportsFingerprint'
                ] LOOP
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema='E_Attendace' AND table_name='Devices'
                          AND column_name=col AND data_type='boolean'
                    ) THEN
                        BEGIN
                            EXECUTE format('ALTER TABLE "E_Attendace"."Devices" ALTER COLUMN %I DROP DEFAULT', col);
                            EXECUTE format('ALTER TABLE "E_Attendace"."Devices" ALTER COLUMN %I TYPE INTEGER USING (CASE WHEN %I THEN 1 ELSE 0 END)', col, col);
                            EXECUTE format('ALTER TABLE "E_Attendace"."Devices" ALTER COLUMN %I SET DEFAULT 0', col);
                        EXCEPTION WHEN OTHERS THEN
                            -- never let one column block the others
                            NULL;
                        END;
                    END IF;
                END LOOP;
            END $$;
            """);
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "CapabilitiesJson" JSONB NOT NULL DEFAULT '{}';""");
        // Convert CapabilitiesJson from TEXT to JSONB if it was created as TEXT
        TryExec(conn, """
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema='E_Attendace' AND table_name='Devices'
                      AND column_name='CapabilitiesJson' AND data_type='text'
                ) THEN
                    ALTER TABLE "E_Attendace"."Devices" ALTER COLUMN "CapabilitiesJson" TYPE JSONB
                        USING COALESCE(NULLIF(TRIM("CapabilitiesJson"),''),'{}')::jsonb;
                END IF;
            END $$;
            """);
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "CreatedAtUtc" timestamptz NOT NULL DEFAULT NOW();""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW();""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "Status" INTEGER NOT NULL DEFAULT 0;""");
        // If Status column was created as TEXT (e.g. 'offline'), convert it to INTEGER so enum values work
        TryExec(conn, """
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema='E_Attendace' AND table_name='Devices'
                      AND column_name='Status' AND data_type='text'
                ) THEN
                    ALTER TABLE "E_Attendace"."Devices" ALTER COLUMN "Status" TYPE INTEGER
                        USING CASE WHEN LOWER("Status") IN ('offline','0','') THEN 0
                                   WHEN LOWER("Status") IN ('online','1') THEN 1
                                   WHEN LOWER("Status") IN ('busy','2') THEN 2
                                   ELSE 0 END;
                END IF;
            END $$;
            """);
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "IsRegistrationDevice" INTEGER NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "IsOnline" INTEGER NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "IsRegistered" INTEGER NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "LastSeenUtc" timestamptz NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "AreaId" INTEGER NOT NULL DEFAULT 1;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "DepartmentId" INTEGER NOT NULL DEFAULT 1;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "LastPushTime" timestamptz NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "SecretKey" TEXT NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Devices" ADD COLUMN IF NOT EXISTS "CreatedAt" timestamptz NOT NULL DEFAULT NOW();""");

        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."Timetables" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "TimetableName" TEXT NOT NULL,
                "IsActive" BOOLEAN NOT NULL DEFAULT TRUE,
                "CreatedAt" timestamptz NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamptz NOT NULL DEFAULT NOW()
            );
            """);
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."BreakTimes" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "BreakName" TEXT NOT NULL,
                "StartTime" time NOT NULL,
                "EndTime" time NOT NULL,
                "DurationMinutes" INTEGER NOT NULL DEFAULT 0,
                "CalculateType" TEXT NOT NULL DEFAULT 'auto_deduct',
                "MinimumBreakMinutes" INTEGER NOT NULL DEFAULT 0,
                "IsActive" BOOLEAN NOT NULL DEFAULT TRUE
            );
            """);
        TryExec(conn, """ALTER TABLE "E_Attendace"."BreakTimes" ADD COLUMN IF NOT EXISTS "CalculateType" TEXT NOT NULL DEFAULT 'auto_deduct';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."BreakTimes" ADD COLUMN IF NOT EXISTS "MinimumBreakMinutes" INTEGER NOT NULL DEFAULT 0;""");
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."TimetableDays" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "TimetableId" INTEGER NOT NULL REFERENCES "E_Attendace"."Timetables"("Id") ON DELETE CASCADE,
                "DayOfWeek" INTEGER NOT NULL,
                "ShiftId" INTEGER NULL,
                "IsWeeklyOff" BOOLEAN NOT NULL DEFAULT FALSE,
                "CheckInTime" time NOT NULL DEFAULT TIME '10:00',
                "CheckInStartTime" time NOT NULL DEFAULT TIME '00:00',
                "CheckInEndTime" time NOT NULL DEFAULT TIME '10:30',
                "CheckOutStartTime" time NOT NULL DEFAULT TIME '17:00',
                "CheckOutTime" time NOT NULL DEFAULT TIME '23:59',
                "CheckOutEndTime" time NOT NULL DEFAULT TIME '00:59',
                "CrossDay" BOOLEAN NOT NULL DEFAULT FALSE,
                "UseBreakTime" BOOLEAN NOT NULL DEFAULT FALSE,
                "BreakTimeId" INTEGER NULL
            );
            """);
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."TimetableDayBreakTimes" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "TimetableDayId" INTEGER NOT NULL REFERENCES "E_Attendace"."TimetableDays"("Id") ON DELETE CASCADE,
                "BreakTimeId" INTEGER NOT NULL REFERENCES "E_Attendace"."BreakTimes"("Id") ON DELETE CASCADE,
                "SortOrder" INTEGER NOT NULL DEFAULT 0
            );
            """);
        TryExec(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_TimetableDayBreakTimes_Day_Break"
            ON "E_Attendace"."TimetableDayBreakTimes" ("TimetableDayId","BreakTimeId");
            """);
        TryExec(conn, """ALTER TABLE "E_Attendace"."TimetableDays" ADD COLUMN IF NOT EXISTS "CheckInTime" time NOT NULL DEFAULT TIME '10:00';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."TimetableDays" ADD COLUMN IF NOT EXISTS "CheckInStartTime" time NOT NULL DEFAULT TIME '00:00';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."TimetableDays" ADD COLUMN IF NOT EXISTS "CheckInEndTime" time NOT NULL DEFAULT TIME '10:30';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."TimetableDays" ADD COLUMN IF NOT EXISTS "CheckOutStartTime" time NOT NULL DEFAULT TIME '17:00';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."TimetableDays" ADD COLUMN IF NOT EXISTS "CheckOutTime" time NOT NULL DEFAULT TIME '23:59';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."TimetableDays" ADD COLUMN IF NOT EXISTS "CheckOutEndTime" time NOT NULL DEFAULT TIME '00:59';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."TimetableDays" ADD COLUMN IF NOT EXISTS "CrossDay" BOOLEAN NOT NULL DEFAULT FALSE;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."TimetableDays" ADD COLUMN IF NOT EXISTS "UseBreakTime" BOOLEAN NOT NULL DEFAULT FALSE;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."TimetableDays" ADD COLUMN IF NOT EXISTS "BreakTimeId" INTEGER NULL;""");
        TryExec(conn, """
            UPDATE "E_Attendace"."TimetableDays"
            SET
                "CheckInTime" = TIME '10:00',
                "CheckInStartTime" = TIME '00:00',
                "CheckOutTime" = TIME '23:59',
                "CheckOutEndTime" = TIME '00:59'
            WHERE
                "CheckInTime" = TIME '09:00'
                AND "CheckInStartTime" = TIME '08:30'
                AND "CheckOutTime" = TIME '18:00'
                AND "CheckOutEndTime" = TIME '23:00';
            """);
        TryExec(conn, """
            UPDATE "E_Attendace"."TimetableDays"
            SET "CheckOutTime" = TIME '23:59'
            WHERE
                "CheckInTime" = TIME '10:00'
                AND "CheckInStartTime" = TIME '00:00'
                AND "CheckInEndTime" = TIME '10:30'
                AND "CheckOutStartTime" = TIME '17:00'
                AND "CheckOutTime" = TIME '17:00'
                AND "CheckOutEndTime" = TIME '00:59';
            """);
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."EmployeeTimetables" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "PersonnelId" TEXT NOT NULL,
                "TimetableId" INTEGER NOT NULL REFERENCES "E_Attendace"."Timetables"("Id") ON DELETE CASCADE,
                "EffectiveFrom" date NOT NULL,
                "IsActive" BOOLEAN NOT NULL DEFAULT TRUE
            );
            """);
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."DepartmentTimetables" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "DepartmentId" INTEGER NOT NULL,
                "TimetableId" INTEGER NOT NULL REFERENCES "E_Attendace"."Timetables"("Id") ON DELETE CASCADE,
                "EffectiveFrom" date NOT NULL,
                "IsActive" BOOLEAN NOT NULL DEFAULT TRUE
            );
            """);
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."Holidays" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "HolidayDate" date NOT NULL,
                "HolidayName" TEXT NOT NULL,
                "Description" TEXT NULL,
                "IsRecurring" BOOLEAN NOT NULL DEFAULT FALSE,
                "DepartmentId" INTEGER NULL,
                "TimeZoneId" INTEGER NULL,
                "IsActive" BOOLEAN NOT NULL DEFAULT TRUE
            );
            """);
        TryExec(conn, """ALTER TABLE "E_Attendace"."Holidays" ADD COLUMN IF NOT EXISTS "Description" TEXT NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Holidays" ADD COLUMN IF NOT EXISTS "IsRecurring" BOOLEAN NOT NULL DEFAULT FALSE;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."Holidays" ADD COLUMN IF NOT EXISTS "TimeZoneId" INTEGER NULL;""");
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."AttendancePolicies" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "WeeklyOffWorkAsOvertime" BOOLEAN NOT NULL DEFAULT TRUE,
                "HolidayWorkAsOvertime" BOOLEAN NOT NULL DEFAULT TRUE,
                "UpdatedAt" timestamptz NOT NULL DEFAULT NOW()
            );
            """);
        TryExec(conn, """ALTER TABLE "E_Attendace"."AttendancePolicies" ADD COLUMN IF NOT EXISTS "HolidayWorkAsOvertime" BOOLEAN NOT NULL DEFAULT TRUE;""");
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."LeaveTypes" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "LeaveName" TEXT NOT NULL,
                "IsPaid" BOOLEAN NOT NULL DEFAULT TRUE,
                "MaxPerYear" numeric(10,2) NULL,
                "CarryForward" BOOLEAN NOT NULL DEFAULT FALSE,
                "AllowHalfDay" BOOLEAN NOT NULL DEFAULT FALSE,
                "IsActive" BOOLEAN NOT NULL DEFAULT TRUE
            );
            """);
        TryExec(conn, """
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'E_Attendace'
                      AND table_name = 'LeaveTypes'
                      AND column_name = 'EntitlementDays'
                ) AND NOT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'E_Attendace'
                      AND table_name = 'LeaveTypes'
                      AND column_name = 'MaxPerYear'
                ) THEN
                    EXECUTE 'ALTER TABLE "E_Attendace"."LeaveTypes" RENAME COLUMN "EntitlementDays" TO "MaxPerYear"';
                END IF;
            END $$;
            """);
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveTypes" ADD COLUMN IF NOT EXISTS "LeaveName" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveTypes" ADD COLUMN IF NOT EXISTS "IsPaid" BOOLEAN NOT NULL DEFAULT TRUE;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveTypes" ADD COLUMN IF NOT EXISTS "MaxPerYear" numeric(10,2) NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveTypes" ADD COLUMN IF NOT EXISTS "CarryForward" BOOLEAN NOT NULL DEFAULT FALSE;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveTypes" ADD COLUMN IF NOT EXISTS "AllowHalfDay" BOOLEAN NOT NULL DEFAULT FALSE;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveTypes" ADD COLUMN IF NOT EXISTS "IsActive" BOOLEAN NOT NULL DEFAULT TRUE;""");
        TryExec(conn, """
            UPDATE "E_Attendace"."LeaveTypes"
            SET "LeaveName" = CONCAT('Leave Type ', "Id")
            WHERE COALESCE(TRIM("LeaveName"), '') = '';
            """);
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."LeaveRequests" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "PersonnelId" TEXT NOT NULL,
                "LeaveTypeId" INTEGER NOT NULL REFERENCES "E_Attendace"."LeaveTypes"("Id") ON DELETE CASCADE,
                "FromDate" date NOT NULL,
                "ToDate" date NOT NULL,
                "TotalDays" numeric(10,2) NOT NULL DEFAULT 0,
                "IsHalfDay" BOOLEAN NOT NULL DEFAULT FALSE,
                "HalfDayType" TEXT NULL,
                "Reason" TEXT NULL,
                "Status" TEXT NOT NULL DEFAULT 'pending',
                "AppliedDate" timestamptz NOT NULL DEFAULT NOW(),
                "ApprovedBy" TEXT NULL,
                "ApprovedDate" timestamptz NULL,
                "CancelledDate" timestamptz NULL
            );
            """);
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveRequests" ADD COLUMN IF NOT EXISTS "TotalDays" numeric(10,2) NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveRequests" ADD COLUMN IF NOT EXISTS "IsHalfDay" BOOLEAN NOT NULL DEFAULT FALSE;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveRequests" ADD COLUMN IF NOT EXISTS "HalfDayType" TEXT NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveRequests" ADD COLUMN IF NOT EXISTS "Reason" TEXT NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveRequests" ADD COLUMN IF NOT EXISTS "Status" TEXT NOT NULL DEFAULT 'pending';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveRequests" ADD COLUMN IF NOT EXISTS "AppliedDate" timestamptz NOT NULL DEFAULT NOW();""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveRequests" ADD COLUMN IF NOT EXISTS "ApprovedBy" TEXT NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveRequests" ADD COLUMN IF NOT EXISTS "ApprovedDate" timestamptz NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveRequests" ADD COLUMN IF NOT EXISTS "CancelledDate" timestamptz NULL;""");
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."LeaveBalances" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "PersonnelId" TEXT NOT NULL,
                "LeaveTypeId" INTEGER NOT NULL REFERENCES "E_Attendace"."LeaveTypes"("Id") ON DELETE CASCADE,
                "Year" INTEGER NOT NULL,
                "TotalAllocated" numeric(10,2) NOT NULL DEFAULT 0,
                "Used" numeric(10,2) NOT NULL DEFAULT 0,
                "Remaining" numeric(10,2) NOT NULL DEFAULT 0,
                "UpdatedAt" timestamptz NOT NULL DEFAULT NOW()
            );
            """);
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveBalances" ADD COLUMN IF NOT EXISTS "TotalAllocated" numeric(10,2) NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveBalances" ADD COLUMN IF NOT EXISTS "Used" numeric(10,2) NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveBalances" ADD COLUMN IF NOT EXISTS "Remaining" numeric(10,2) NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."LeaveBalances" ADD COLUMN IF NOT EXISTS "UpdatedAt" timestamptz NOT NULL DEFAULT NOW();""");
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."LeaveAttachments" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "LeaveRequestId" INTEGER NOT NULL REFERENCES "E_Attendace"."LeaveRequests"("Id") ON DELETE CASCADE,
                "FileName" TEXT NOT NULL,
                "FileUrl" TEXT NOT NULL,
                "UploadedAt" timestamptz NOT NULL DEFAULT NOW(),
                "UploadedBy" TEXT NULL
            );
            """);
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."LeaveRemarks" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "LeaveRequestId" INTEGER NOT NULL REFERENCES "E_Attendace"."LeaveRequests"("Id") ON DELETE CASCADE,
                "Action" TEXT NOT NULL,
                "Remarks" TEXT NULL,
                "ActorId" TEXT NULL,
                "CreatedAt" timestamptz NOT NULL DEFAULT NOW()
            );
            """);
        TryExec(conn, """
            INSERT INTO "E_Attendace"."LeaveTypes" ("LeaveName","IsPaid","MaxPerYear","CarryForward","AllowHalfDay","IsActive")
            SELECT v."LeaveName", v."IsPaid", v."MaxPerYear", v."CarryForward", v."AllowHalfDay", TRUE
            FROM (
                VALUES
                    ('Casual Leave', TRUE, 12::numeric, FALSE, FALSE),
                    ('Sick Leave', TRUE, 10::numeric, FALSE, FALSE),
                    ('Annual Leave', TRUE, 18::numeric, TRUE, FALSE),
                    ('Unpaid Leave', FALSE, NULL::numeric, FALSE, FALSE),
                    ('Half Day Leave', TRUE, NULL::numeric, FALSE, TRUE)
            ) AS v("LeaveName","IsPaid","MaxPerYear","CarryForward","AllowHalfDay")
            WHERE NOT EXISTS (
                SELECT 1
                FROM "E_Attendace"."LeaveTypes" lt
                WHERE LOWER(lt."LeaveName") = LOWER(v."LeaveName")
            );
            """);
        TryExec(conn, """
            INSERT INTO "E_Attendace"."AttendancePolicies" ("WeeklyOffWorkAsOvertime","HolidayWorkAsOvertime","UpdatedAt")
            SELECT TRUE, TRUE, NOW()
            WHERE NOT EXISTS (SELECT 1 FROM "E_Attendace"."AttendancePolicies");
            """);
        TryExec(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_BreakTimes_BreakName" ON "E_Attendace"."BreakTimes" ("BreakName");""");
        TryExec(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Timetables_TimetableName" ON "E_Attendace"."Timetables" ("TimetableName");""");
        TryExec(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_TimetableDays_TimetableId_DayOfWeek" ON "E_Attendace"."TimetableDays" ("TimetableId","DayOfWeek");""");
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_TimetableDays_BreakTimeId" ON "E_Attendace"."TimetableDays" ("BreakTimeId");""");
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_EmployeeTimetables_PersonnelId_EffectiveFrom" ON "E_Attendace"."EmployeeTimetables" ("PersonnelId","EffectiveFrom");""");
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_DepartmentTimetables_DepartmentId_EffectiveFrom" ON "E_Attendace"."DepartmentTimetables" ("DepartmentId","EffectiveFrom");""");
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_Holidays_HolidayDate_DepartmentId" ON "E_Attendace"."Holidays" ("HolidayDate","DepartmentId");""");
        TryExec(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_LeaveTypes_LeaveName" ON "E_Attendace"."LeaveTypes" ("LeaveName");""");
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_LeaveRequests_PersonnelId_FromDate_ToDate_Status" ON "E_Attendace"."LeaveRequests" ("PersonnelId","FromDate","ToDate","Status");""");
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_LeaveRequests_LeaveTypeId_Status" ON "E_Attendace"."LeaveRequests" ("LeaveTypeId","Status");""");
        TryExec(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_LeaveBalances_PersonnelId_LeaveTypeId_Year" ON "E_Attendace"."LeaveBalances" ("PersonnelId","LeaveTypeId","Year");""");
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_LeaveAttachments_LeaveRequestId" ON "E_Attendace"."LeaveAttachments" ("LeaveRequestId");""");
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_LeaveRemarks_LeaveRequestId_CreatedAt" ON "E_Attendace"."LeaveRemarks" ("LeaveRequestId","CreatedAt" DESC);""");
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."PersonnelAreas" (
                "Id" TEXT PRIMARY KEY,
                "Code" TEXT NOT NULL UNIQUE,
                "Name" TEXT NOT NULL,
                "Remark" TEXT NOT NULL DEFAULT '',
                "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
            );
            """);
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."PersonnelDepartments" (
                "Id" TEXT PRIMARY KEY,
                "Code" TEXT NOT NULL UNIQUE,
                "Name" TEXT NOT NULL,
                "Superior" TEXT NOT NULL DEFAULT '',
                "Manager" TEXT NOT NULL DEFAULT '',
                "AreaCode" TEXT NOT NULL,
                "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
            );
            """);
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."PersonnelPositions" (
                "Id" TEXT PRIMARY KEY,
                "Code" TEXT NOT NULL UNIQUE,
                "Name" TEXT NOT NULL,
                "Department" TEXT NOT NULL,
                "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
            );
            """);
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."PersonnelDeviceAssignments" (
                "DeviceSerial" TEXT PRIMARY KEY,
                "AreaCode" TEXT NOT NULL,
                "DepartmentCode" TEXT NOT NULL,
                "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
            );
            """);
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."PersonnelEmployeeProfiles" (
                "EmployeeCode" TEXT PRIMARY KEY,
                "Privilege" TEXT NOT NULL DEFAULT 'User',
                "CardNumber" TEXT NOT NULL DEFAULT '',
                "PictureDataUrl" TEXT NOT NULL DEFAULT '',
                "MobileNumber" TEXT NOT NULL DEFAULT '',
                "Gender" TEXT NOT NULL DEFAULT 'Male',
                "HireDate" TEXT NOT NULL DEFAULT '',
                "Password" TEXT NOT NULL DEFAULT '',
                "Position" TEXT NOT NULL DEFAULT '',
                "AreaCode" TEXT NOT NULL DEFAULT '',
                "DepartmentCode" TEXT NOT NULL DEFAULT '',
                "AssignmentsJson" TEXT NOT NULL DEFAULT '[]',
                "VerifyMode" INTEGER NOT NULL DEFAULT 0,
                "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
            );
            """);
        TryExec(conn, """ALTER TABLE "E_Attendace"."PersonnelEmployeeProfiles" ADD COLUMN IF NOT EXISTS "Position" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."PersonnelEmployeeProfiles" ADD COLUMN IF NOT EXISTS "AreaCode" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."PersonnelEmployeeProfiles" ADD COLUMN IF NOT EXISTS "DepartmentCode" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."PersonnelEmployeeProfiles" ADD COLUMN IF NOT EXISTS "AssignmentsJson" TEXT NOT NULL DEFAULT '[]';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."PersonnelEmployeeProfiles" ADD COLUMN IF NOT EXISTS "VerifyMode" INTEGER NOT NULL DEFAULT 0;""");
        TryExec(conn, """
            CREATE OR REPLACE VIEW "E_Attendace"."leave_types" AS
            SELECT
                "Id" AS id,
                "LeaveName" AS leave_name,
                "IsPaid" AS is_paid,
                "MaxPerYear" AS max_per_year,
                "CarryForward" AS carry_forward,
                "AllowHalfDay" AS allow_half_day,
                "IsActive" AS is_active
            FROM "E_Attendace"."LeaveTypes";
            """);
        TryExec(conn, """
            DO $$
            BEGIN
                -- LeaveRequests.PersonnelId
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'LeaveRequests' AND column_name = 'PersonnelId' AND (data_type = 'integer' OR data_type = 'text')) THEN
                    ALTER TABLE "E_Attendace"."LeaveRequests" ALTER COLUMN "PersonnelId" TYPE uuid USING "PersonnelId"::uuid;
                END IF;
                -- LeaveRequests.ApprovedBy
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'LeaveRequests' AND column_name = 'ApprovedBy' AND (data_type = 'integer' OR data_type = 'text')) THEN
                    ALTER TABLE "E_Attendace"."LeaveRequests" ALTER COLUMN "ApprovedBy" TYPE uuid USING "ApprovedBy"::uuid;
                END IF;
                -- LeaveBalances.PersonnelId
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'LeaveBalances' AND column_name = 'PersonnelId' AND (data_type = 'integer' OR data_type = 'text')) THEN
                    ALTER TABLE "E_Attendace"."LeaveBalances" ALTER COLUMN "PersonnelId" TYPE uuid USING "PersonnelId"::uuid;
                END IF;
                -- EmployeeTimetables.PersonnelId
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'EmployeeTimetables' AND column_name = 'PersonnelId' AND (data_type = 'integer' OR data_type = 'text')) THEN
                    ALTER TABLE "E_Attendace"."EmployeeTimetables" ALTER COLUMN "PersonnelId" TYPE uuid USING "PersonnelId"::uuid;
                END IF;
                -- LeaveAttachments.UploadedBy
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'LeaveAttachments' AND column_name = 'UploadedBy' AND (data_type = 'integer' OR data_type = 'text')) THEN
                    ALTER TABLE "E_Attendace"."LeaveAttachments" ALTER COLUMN "UploadedBy" TYPE uuid USING "UploadedBy"::uuid;
                END IF;
                -- LeaveRemarks.ActorId
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'LeaveRemarks' AND column_name = 'ActorId' AND (data_type = 'integer' OR data_type = 'text')) THEN
                    ALTER TABLE "E_Attendace"."LeaveRemarks" ALTER COLUMN "ActorId" TYPE uuid USING "ActorId"::uuid;
                END IF;
                -- Ensure AttendanceRecords has all columns SqliteAttendanceRepository needs
                IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'E_Attendace' AND table_name = 'AttendanceRecords') THEN
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'AttendanceRecords' AND column_name = 'EmployeeId') THEN
                        ALTER TABLE "E_Attendace"."AttendanceRecords" ADD COLUMN "EmployeeId" TEXT NOT NULL DEFAULT '';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'AttendanceRecords' AND column_name = 'EmployeeCode') THEN
                        ALTER TABLE "E_Attendace"."AttendanceRecords" ADD COLUMN "EmployeeCode" TEXT NOT NULL DEFAULT '';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'AttendanceRecords' AND column_name = 'Timestamp') THEN
                        ALTER TABLE "E_Attendace"."AttendanceRecords" ADD COLUMN "Timestamp" timestamptz;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'AttendanceRecords' AND column_name = 'DeviceSerialNumber') THEN
                        ALTER TABLE "E_Attendace"."AttendanceRecords" ADD COLUMN "DeviceSerialNumber" TEXT NULL;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'AttendanceRecords' AND column_name = 'AreaId') THEN
                        ALTER TABLE "E_Attendace"."AttendanceRecords" ADD COLUMN "AreaId" INTEGER NOT NULL DEFAULT 1;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'AttendanceRecords' AND column_name = 'DepartmentId') THEN
                        ALTER TABLE "E_Attendace"."AttendanceRecords" ADD COLUMN "DepartmentId" INTEGER NOT NULL DEFAULT 1;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'AttendanceRecords' AND column_name = 'AttendanceDate') THEN
                        ALTER TABLE "E_Attendace"."AttendanceRecords" ADD COLUMN "AttendanceDate" date;
                        UPDATE "E_Attendace"."AttendanceRecords" SET "AttendanceDate" = "Timestamp"::date WHERE "AttendanceDate" IS NULL;
                    END IF;
                END IF;
                -- (Devices/Employees/AttendanceRecords uuid->TEXT conversions moved to
                --  their own independent TryExec calls below so a failure in the Leave
                --  conversions above cannot block them.)
                -- AttendanceExceptions migrations
                IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'E_Attendace' AND table_name = 'AttendanceExceptions') THEN
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'E_Attendace' AND table_name = 'AttendanceExceptions' AND column_name = 'AttendanceId' AND (data_type = 'integer' OR data_type = 'text')) THEN
                        ALTER TABLE "E_Attendace"."AttendanceExceptions" ALTER COLUMN "AttendanceId" TYPE uuid USING "AttendanceId"::uuid;
                    END IF;
                END IF;
            END $$;
            """);

        // ════════════════════════════════════════════════════════════════════════
        // CRITICAL uuid → TEXT conversions — EACH IN ITS OWN TryExec so one failure
        // never blocks the others. The C# repository passes Guid.ToString() text, so
        // these ID columns MUST be TEXT. This is the fix that makes add-device,
        // create-employee etc. work. (Runs every startup; no-op once already TEXT.)
        // ════════════════════════════════════════════════════════════════════════
        TryExec(conn, """
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='E_Attendace' AND table_name='Devices' AND column_name='Id' AND data_type='uuid') THEN
                    ALTER TABLE "E_Attendace"."Devices" DROP CONSTRAINT IF EXISTS "Devices_pkey" CASCADE;
                    ALTER TABLE "E_Attendace"."Devices" ALTER COLUMN "Id" TYPE TEXT USING "Id"::text;
                    ALTER TABLE "E_Attendace"."Devices" ADD PRIMARY KEY ("Id");
                END IF;
            END $$;
            """);
        TryExec(conn, """
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='E_Attendace' AND table_name='Employees' AND column_name='Id' AND data_type='uuid') THEN
                    ALTER TABLE "E_Attendace"."Employees" DROP CONSTRAINT IF EXISTS "Employees_pkey" CASCADE;
                    ALTER TABLE "E_Attendace"."Employees" ALTER COLUMN "Id" TYPE TEXT USING "Id"::text;
                    ALTER TABLE "E_Attendace"."Employees" ADD PRIMARY KEY ("Id");
                END IF;
            END $$;
            """);
        TryExec(conn, """
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='E_Attendace' AND table_name='DeviceCommands' AND column_name='Id' AND data_type='uuid') THEN
                    ALTER TABLE "E_Attendace"."DeviceCommands" DROP CONSTRAINT IF EXISTS "DeviceCommands_pkey" CASCADE;
                    ALTER TABLE "E_Attendace"."DeviceCommands" ALTER COLUMN "Id" TYPE TEXT USING "Id"::text;
                    ALTER TABLE "E_Attendace"."DeviceCommands" ADD PRIMARY KEY ("Id");
                END IF;
            END $$;
            """);
        TryExec(conn, """
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='E_Attendace' AND table_name='DeviceSyncLogs' AND column_name='Id' AND data_type='uuid') THEN
                    ALTER TABLE "E_Attendace"."DeviceSyncLogs" DROP CONSTRAINT IF EXISTS "DeviceSyncLogs_pkey" CASCADE;
                    ALTER TABLE "E_Attendace"."DeviceSyncLogs" ALTER COLUMN "Id" TYPE TEXT USING "Id"::text;
                    ALTER TABLE "E_Attendace"."DeviceSyncLogs" ADD PRIMARY KEY ("Id");
                END IF;
            END $$;
            """);
        TryExec(conn, """
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='E_Attendace' AND table_name='AttendanceRecords' AND column_name='Id' AND data_type='uuid') THEN
                    ALTER TABLE "E_Attendace"."AttendanceRecords" DROP CONSTRAINT IF EXISTS "AttendanceRecords_pkey" CASCADE;
                    ALTER TABLE "E_Attendace"."AttendanceRecords" ALTER COLUMN "Id" TYPE TEXT USING "Id"::text;
                    ALTER TABLE "E_Attendace"."AttendanceRecords" ADD PRIMARY KEY ("Id");
                END IF;
            END $$;
            """);
        TryExec(conn, """ALTER TABLE "E_Attendace"."AttendanceRecords" ALTER COLUMN "EmployeeId" TYPE TEXT USING "EmployeeId"::text;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."AttendanceRecords" ALTER COLUMN "DeviceId" TYPE TEXT USING "DeviceId"::text;""");

        // ════════════════════════════════════════════════════════════════════
        // COMPREHENSIVE TYPE RECONCILIATION — runs every startup as the table owner.
        // Converts any legacy column to the type SqliteAttendanceRepository requires.
        // CRITICAL: each conversion DROPS the column DEFAULT first (a boolean DEFAULT
        // false/true cannot auto-cast to integer, which is what silently broke add-device),
        // and is wrapped in its OWN exception handler so one failure never rolls back the rest.
        // Idempotent: only fires when the column is actually the wrong type.
        // ════════════════════════════════════════════════════════════════════
        TryExec(conn, """
            DO $recon$
            DECLARE
                bt TEXT[][] := ARRAY[
                    ['Devices','IsOnline'],['Devices','IsRegistrationDevice'],['Devices','IsRegistered'],
                    ['Devices','FingerFunOn'],['Devices','FaceFunOn'],['Devices','VisiLightFunOn'],
                    ['Devices','BioDataFun'],['Devices','BioPhotoFun'],['Devices','SupportsPalm'],
                    ['Devices','SupportsFingerVein'],['Devices','SupportsVisibleLightFace'],
                    ['Devices','SupportsInfraredFace'],['Devices','SupportsFingerprint'],
                    ['Employees','IsActive'],['users','expires_enabled'],
                    ['biometric_templates','valid'],['biometric_templates','duress']
                ];
                tst TEXT[][] := ARRAY[
                    ['users','created_at'],['users','updated_at'],['users','valid_from'],['users','valid_to'],
                    ['biometric_templates','created_at'],['biometric_templates','updated_at'],
                    ['biometric_photos','created_at'],['biometric_photos','updated_at']
                ];
                i INT; t TEXT; c TEXT;
            BEGIN
                -- boolean -> integer (repo sends 1/0)
                FOR i IN 1..array_length(bt,1) LOOP
                    t := bt[i][1]; c := bt[i][2];
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='E_Attendace' AND table_name=t AND column_name=c AND data_type='boolean') THEN
                        BEGIN
                            EXECUTE format('ALTER TABLE "E_Attendace".%I ALTER COLUMN %I DROP DEFAULT', t, c);
                            EXECUTE format('ALTER TABLE "E_Attendace".%I ALTER COLUMN %I TYPE INTEGER USING (CASE WHEN %I THEN 1 ELSE 0 END)', t, c, c);
                            EXECUTE format('ALTER TABLE "E_Attendace".%I ALTER COLUMN %I SET DEFAULT 0', t, c);
                        EXCEPTION WHEN OTHERS THEN NULL; END;
                    END IF;
                END LOOP;
                -- timestamp -> text (biometric tables store ISO text)
                FOR i IN 1..array_length(tst,1) LOOP
                    t := tst[i][1]; c := tst[i][2];
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='E_Attendace' AND table_name=t AND column_name=c AND data_type LIKE 'timestamp%') THEN
                        BEGIN
                            EXECUTE format('ALTER TABLE "E_Attendace".%I ALTER COLUMN %I DROP DEFAULT', t, c);
                            EXECUTE format('ALTER TABLE "E_Attendace".%I ALTER COLUMN %I TYPE TEXT USING %I::text', t, c, c);
                        EXCEPTION WHEN OTHERS THEN NULL; END;
                    END IF;
                END LOOP;
                -- EmployeeDepartments uuid -> text (composite PK)
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='E_Attendace' AND table_name='EmployeeDepartments' AND column_name='EmployeeId' AND data_type='uuid') THEN
                    BEGIN
                        ALTER TABLE "E_Attendace"."EmployeeDepartments" DROP CONSTRAINT IF EXISTS "EmployeeDepartments_pkey" CASCADE;
                        ALTER TABLE "E_Attendace"."EmployeeDepartments" ALTER COLUMN "EmployeeId" TYPE TEXT USING "EmployeeId"::text;
                        ALTER TABLE "E_Attendace"."EmployeeDepartments" ADD PRIMARY KEY ("EmployeeId","DepartmentId");
                    EXCEPTION WHEN OTHERS THEN NULL; END;
                END IF;
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='E_Attendace' AND table_name='EmployeeDepartments' AND column_name='AssignedBy' AND data_type='uuid') THEN
                    BEGIN
                        ALTER TABLE "E_Attendace"."EmployeeDepartments" ALTER COLUMN "AssignedBy" TYPE TEXT USING "AssignedBy"::text;
                    EXCEPTION WHEN OTHERS THEN NULL; END;
                END IF;
                -- Devices.Status legacy text -> integer
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='E_Attendace' AND table_name='Devices' AND column_name='Status' AND data_type='text') THEN
                    BEGIN
                        ALTER TABLE "E_Attendace"."Devices" ALTER COLUMN "Status" DROP DEFAULT;
                        ALTER TABLE "E_Attendace"."Devices" ALTER COLUMN "Status" TYPE INTEGER USING CASE WHEN LOWER("Status")='online' THEN 1 WHEN LOWER("Status")='busy' THEN 2 ELSE 0 END;
                        ALTER TABLE "E_Attendace"."Devices" ALTER COLUMN "Status" SET DEFAULT 0;
                    EXCEPTION WHEN OTHERS THEN NULL; END;
                END IF;
                -- Leave/Timetable personnel-reference columns: PbAttendanceDbContext.EnsureCreated()
                -- can create these tables (Guid-typed in the EF model) before the TEXT-based bootstrap
                -- below ever runs, leaving them as uuid — which breaks every EF query touching
                -- PersonnelEntity (mapped to Employees, whose real Id is TEXT), e.g. "operator does
                -- not exist: uuid = character varying". leave_applications/leave_balance views depend
                -- on the LeaveRequests/LeaveBalances columns, so those two are dropped and recreated;
                -- the rest have no dependents and use the same generic loop as the booleans above.
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='E_Attendace' AND table_name='LeaveRequests' AND column_name='PersonnelId' AND data_type='uuid') THEN
                    BEGIN
                        DROP VIEW IF EXISTS "E_Attendace".leave_applications;
                        ALTER TABLE "E_Attendace"."LeaveRequests" ALTER COLUMN "PersonnelId" TYPE TEXT USING "PersonnelId"::text;
                        ALTER TABLE "E_Attendace"."LeaveRequests" ALTER COLUMN "ApprovedBy" TYPE TEXT USING "ApprovedBy"::text;
                        CREATE VIEW "E_Attendace".leave_applications AS
                            SELECT "Id" AS id, "PersonnelId" AS employee_id, "LeaveTypeId" AS leave_type_id,
                                   "FromDate" AS from_date, "ToDate" AS to_date, "TotalDays" AS total_days,
                                   "IsHalfDay" AS is_half_day, "Reason" AS reason, "Status" AS status,
                                   "AppliedDate" AS applied_date, "ApprovedBy" AS approved_by, "ApprovedDate" AS approved_date
                            FROM "E_Attendace"."LeaveRequests";
                    EXCEPTION WHEN OTHERS THEN NULL; END;
                END IF;
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='E_Attendace' AND table_name='LeaveBalances' AND column_name='PersonnelId' AND data_type='uuid') THEN
                    BEGIN
                        DROP VIEW IF EXISTS "E_Attendace".leave_balance;
                        ALTER TABLE "E_Attendace"."LeaveBalances" ALTER COLUMN "PersonnelId" TYPE TEXT USING "PersonnelId"::text;
                        CREATE VIEW "E_Attendace".leave_balance AS
                            SELECT "Id" AS id, "PersonnelId" AS employee_id, "LeaveTypeId" AS leave_type_id,
                                   "Year" AS year, "TotalAllocated" AS total_allocated, "Used" AS used, "Remaining" AS remaining
                            FROM "E_Attendace"."LeaveBalances";
                    EXCEPTION WHEN OTHERS THEN NULL; END;
                END IF;
                FOR i IN 1..3 LOOP
                    t := (ARRAY['LeaveAttachments','LeaveRemarks','EmployeeTimetables'])[i];
                    c := (ARRAY['UploadedBy','ActorId','PersonnelId'])[i];
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='E_Attendace' AND table_name=t AND column_name=c AND data_type='uuid') THEN
                        BEGIN
                            EXECUTE format('ALTER TABLE "E_Attendace".%I ALTER COLUMN %I TYPE TEXT USING %I::text', t, c, c);
                        EXCEPTION WHEN OTHERS THEN NULL; END;
                    END IF;
                END LOOP;
            END
            $recon$;
            """);

        TryExec(conn, """
            CREATE OR REPLACE VIEW "E_Attendace"."leave_applications" AS
            SELECT
                "Id" AS id,
                "PersonnelId" AS employee_id,
                "LeaveTypeId" AS leave_type_id,
                "FromDate" AS from_date,
                "ToDate" AS to_date,
                "TotalDays" AS total_days,
                "IsHalfDay" AS is_half_day,
                "Reason" AS reason,
                "Status" AS status,
                "AppliedDate" AS applied_date,
                "ApprovedBy" AS approved_by,
                "ApprovedDate" AS approved_date
            FROM "E_Attendace"."LeaveRequests";
            """);
        TryExec(conn, """
            CREATE OR REPLACE VIEW "E_Attendace"."leave_balance" AS
            SELECT
                "Id" AS id,
                "PersonnelId" AS employee_id,
                "LeaveTypeId" AS leave_type_id,
                "Year" AS year,
                "TotalAllocated" AS total_allocated,
                "Used" AS used,
                "Remaining" AS remaining
            FROM "E_Attendace"."LeaveBalances";
            """);
        TryExec(conn, """
            CREATE OR REPLACE VIEW "E_Attendace"."holidays" AS
            SELECT
                "Id" AS id,
                "HolidayName" AS holiday_name,
                "HolidayDate" AS holiday_date,
                "Description" AS description,
                "IsRecurring" AS is_recurring,
                "DepartmentId" AS department_id
            FROM "E_Attendace"."Holidays";
            """);
        // ── auth_users (login system) ───────────────────────────────────────────
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."auth_users" (
                "Id" BIGSERIAL PRIMARY KEY,
                "UserName" TEXT NOT NULL,
                "PasswordHash" TEXT NOT NULL DEFAULT '',
                "FullName" TEXT NULL,
                "Role" TEXT NOT NULL DEFAULT 'Operator',
                "Status" TEXT NOT NULL DEFAULT 'active',
                "FeatureAccessJson" jsonb NOT NULL DEFAULT '{}',
                "CreatedAtUtc" timestamptz NOT NULL DEFAULT NOW(),
                "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
            );
            """);
        TryExec(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "UX_auth_users_UserName" ON "E_Attendace"."auth_users" (LOWER(TRIM("UserName")));""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "UserName" TEXT;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "FullName" TEXT NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "Role" TEXT NOT NULL DEFAULT 'Operator';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "Status" TEXT NOT NULL DEFAULT 'active';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "FeatureAccessJson" jsonb NOT NULL DEFAULT '{}';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "CreatedAtUtc" timestamptz NOT NULL DEFAULT NOW();""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW();""");
        TryExec(conn, """UPDATE "E_Attendace"."auth_users" SET "UserName" = "Username" WHERE "UserName" IS NULL AND "Username" IS NOT NULL;""");
        // Default admin user (password: PB@TechZone)
        TryExec(conn, """INSERT INTO "E_Attendace"."auth_users" ("UserName","PasswordHash","FullName","Role","Status","FeatureAccessJson") VALUES ('admin',md5('PB@TechZone'),'Admin User','Admin','active','{}') ON CONFLICT DO NOTHING;""");

        // ── auth_login_attempts ─────────────────────────────────────────────────
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."auth_login_attempts" (
                "UserName" TEXT PRIMARY KEY,
                "FailureCount" INTEGER NOT NULL DEFAULT 0,
                "LastFailedAtUtc" timestamptz NULL,
                "LockedUntilUtc" timestamptz NULL,
                "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
            );
            """);
        TryExec(conn, """ALTER TABLE "E_Attendace"."auth_login_attempts" ADD COLUMN IF NOT EXISTS "FailureCount" INTEGER NOT NULL DEFAULT 0;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."auth_login_attempts" ADD COLUMN IF NOT EXISTS "LastFailedAtUtc" timestamptz NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."auth_login_attempts" ADD COLUMN IF NOT EXISTS "LockedUntilUtc" timestamptz NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."auth_login_attempts" ADD COLUMN IF NOT EXISTS "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW();""");

        // ── app_config ──────────────────────────────────────────────────────────
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."app_config" (
                "ConfigKey" TEXT PRIMARY KEY,
                "JsonValue" TEXT NOT NULL DEFAULT '',
                "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
            );
            """);
        TryExec(conn, """ALTER TABLE "E_Attendace"."app_config" ADD COLUMN IF NOT EXISTS "ConfigKey" TEXT;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."app_config" ADD COLUMN IF NOT EXISTS "JsonValue" TEXT NOT NULL DEFAULT '';""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."app_config" ADD COLUMN IF NOT EXISTS "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW();""");
        TryExec(conn, """UPDATE "E_Attendace"."app_config" SET "ConfigKey"="Key","JsonValue"="Value" WHERE "ConfigKey" IS NULL AND "Key" IS NOT NULL;""");

        // ── Areas / Departments / EmployeeDepartments (integer-based hierarchy) ─
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."Areas" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "IsDefault" INTEGER NOT NULL DEFAULT 0,
                "CreatedAt" timestamptz NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamptz NOT NULL DEFAULT NOW(),
                UNIQUE ("Name")
            );
            """);
        TryExec(conn, """INSERT INTO "E_Attendace"."Areas" ("Name","IsDefault") VALUES ('Default Area',1) ON CONFLICT ("Name") DO NOTHING;""");
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."Departments" (
                "Id" INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "AreaId" INTEGER NOT NULL DEFAULT 1,
                "IsDefault" INTEGER NOT NULL DEFAULT 0,
                "CreatedAt" timestamptz NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamptz NOT NULL DEFAULT NOW(),
                UNIQUE ("AreaId","Name")
            );
            """);
        TryExec(conn, """INSERT INTO "E_Attendace"."Departments" ("Name","AreaId","IsDefault") VALUES ('Default Department',1,1) ON CONFLICT ("AreaId","Name") DO NOTHING;""");
        // Migrate any previously-created boolean IsDefault columns (repo binds this as a C# int
        // 0/1 for both SQLite and Postgres; a BOOLEAN column throws 42804 the first time a
        // brand-new area/department is auto-created, e.g. via device-add).
        TryExec(conn, """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='E_Attendace' AND table_name='Areas' AND column_name='IsDefault' AND data_type='boolean') THEN
                    ALTER TABLE "E_Attendace"."Areas" ALTER COLUMN "IsDefault" TYPE INTEGER USING ("IsDefault"::integer);
                END IF;
                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='E_Attendace' AND table_name='Departments' AND column_name='IsDefault' AND data_type='boolean') THEN
                    ALTER TABLE "E_Attendace"."Departments" ALTER COLUMN "IsDefault" TYPE INTEGER USING ("IsDefault"::integer);
                END IF;
            END $$;
            """);
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."EmployeeDepartments" (
                "EmployeeId" TEXT NOT NULL,
                "DepartmentId" INTEGER NOT NULL,
                "AssignedAt" timestamptz NOT NULL DEFAULT NOW(),
                "AssignedBy" TEXT NULL,
                PRIMARY KEY ("EmployeeId","DepartmentId")
            );
            """);
        // Migrate EmployeeId from UUID to TEXT if created by older schema
        TryExec(conn, """
            DO $migrate_ed$
            BEGIN
                IF EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_schema='E_Attendace' AND table_name='EmployeeDepartments'
                             AND column_name='EmployeeId' AND data_type='uuid') THEN
                    BEGIN
                        ALTER TABLE "E_Attendace"."EmployeeDepartments" DROP CONSTRAINT IF EXISTS "EmployeeDepartments_pkey" CASCADE;
                        ALTER TABLE "E_Attendace"."EmployeeDepartments" ALTER COLUMN "EmployeeId" TYPE TEXT USING "EmployeeId"::text;
                        ALTER TABLE "E_Attendace"."EmployeeDepartments" ADD PRIMARY KEY ("EmployeeId","DepartmentId");
                    EXCEPTION WHEN OTHERS THEN NULL; END;
                END IF;
                IF EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_schema='E_Attendace' AND table_name='EmployeeDepartments'
                             AND column_name='AssignedBy' AND data_type='uuid') THEN
                    BEGIN
                        ALTER TABLE "E_Attendace"."EmployeeDepartments" ALTER COLUMN "AssignedBy" TYPE TEXT USING "AssignedBy"::text;
                    EXCEPTION WHEN OTHERS THEN NULL; END;
                END IF;
            END $migrate_ed$;
            """);

        // ── Device command/sync tables ──────────────────────────────────────────
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."DeviceCommands" (
                "Id" TEXT PRIMARY KEY,
                "DeviceSerialNumber" TEXT NOT NULL,
                "CommandText" TEXT NOT NULL,
                "CreatedUtc" timestamptz NOT NULL DEFAULT NOW(),
                "SentUtc" timestamptz NULL,
                "ReplyText" TEXT NULL
            );
            """);
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_DeviceCommands_Pending" ON "E_Attendace"."DeviceCommands" ("DeviceSerialNumber","SentUtc");""");
        // SyncLogs is the table the repository's AddSyncLog/GetSyncLogs actually use (distinct from DeviceSyncLogs).
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."SyncLogs" (
                "Id" TEXT PRIMARY KEY,
                "DeviceSerialNumber" TEXT NOT NULL,
                "Type" INTEGER NOT NULL DEFAULT 0,
                "Status" INTEGER NOT NULL DEFAULT 0,
                "CommandText" TEXT NOT NULL DEFAULT '',
                "ResponseText" TEXT NULL,
                "CreatedUtc" timestamptz NOT NULL DEFAULT NOW(),
                "CompletedUtc" timestamptz NULL
            );
            """);
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_SyncLogs_DeviceStatus" ON "E_Attendace"."SyncLogs" ("DeviceSerialNumber","Status","CreatedUtc");""");
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."DeviceTableStamps" (
                "DeviceSerialNumber" TEXT NOT NULL,
                "TableName" TEXT NOT NULL,
                "StampValue" BIGINT NOT NULL DEFAULT 0,
                "UpdatedUtc" timestamptz NOT NULL DEFAULT NOW(),
                PRIMARY KEY ("DeviceSerialNumber","TableName")
            );
            """);
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."SyncQueue" (
                "Id" BIGSERIAL PRIMARY KEY,
                "EntityType" TEXT NOT NULL,
                "EntityId" TEXT NOT NULL,
                "Operation" TEXT NOT NULL,
                "Status" INTEGER NOT NULL DEFAULT 0,
                "RetryCount" INTEGER NOT NULL DEFAULT 0,
                "MaxRetries" INTEGER NOT NULL DEFAULT 3,
                "Payload" TEXT NULL,
                "ErrorMessage" TEXT NULL,
                "CreatedAt" timestamptz NOT NULL DEFAULT NOW(),
                "ProcessedAt" timestamptz NULL,
                "NextRetryAt" timestamptz NULL
            );
            """);

        // ── Biometric tables ────────────────────────────────────────────────────
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."users" (
                pin VARCHAR(50) PRIMARY KEY,
                name VARCHAR(255),
                privilege INT,
                password VARCHAR(255),
                primary_card VARCHAR(255),
                secondary_card VARCHAR(255),
                group_id INT,
                timezone_profile VARCHAR(32),
                verify_mode INT,
                valid_from TIMESTAMP,
                valid_to TIMESTAMP,
                expires_enabled BOOLEAN,
                phone VARCHAR(50),
                gender INT,
                nation INT,
                id_number VARCHAR(50),
                created_at TIMESTAMP NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMP NOT NULL DEFAULT NOW()
            );
            """);
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."biometric_templates" (
                id BIGSERIAL PRIMARY KEY,
                user_pin VARCHAR(50) NOT NULL,
                type INT NOT NULL, no INT NOT NULL, idx INT NOT NULL,
                valid INTEGER NOT NULL DEFAULT 1, duress INTEGER NOT NULL DEFAULT 0,
                major_ver INT NOT NULL DEFAULT 0, minor_ver INT, format INT, template_data TEXT,
                created_at TIMESTAMP NOT NULL DEFAULT NOW(), updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
                UNIQUE (user_pin, type, no, idx, major_ver)
            );
            """);
        // major_ver was not originally part of the uniqueness key, so re-enrolling/re-syncing the
        // same finger from a device running a different fingerprint algorithm version (e.g. v13
        // after an earlier v10 capture) silently overwrote the older version's template instead of
        // keeping both — defeating HydrateEmployeeBiometricsForDeviceSync's per-device version
        // matching, since only one version's row ever survived. Migrate existing installs: widen the
        // constraint so both versions can coexist per employee+finger.
        TryExec(conn, """ALTER TABLE "E_Attendace"."biometric_templates" ALTER COLUMN major_ver SET DEFAULT 0;""");
        TryExec(conn, """UPDATE "E_Attendace"."biometric_templates" SET major_ver = 0 WHERE major_ver IS NULL;""");
        TryExec(conn, """ALTER TABLE "E_Attendace"."biometric_templates" ALTER COLUMN major_ver SET NOT NULL;""");
        TryExec(conn, """
            DO $$
            DECLARE
                old_constraint_name TEXT;
            BEGIN
                SELECT conname INTO old_constraint_name
                FROM pg_constraint
                WHERE conrelid = '"E_Attendace".biometric_templates'::regclass
                  AND contype = 'u'
                  AND array_length(conkey, 1) = 4;
                IF old_constraint_name IS NOT NULL THEN
                    EXECUTE 'ALTER TABLE "E_Attendace"."biometric_templates" DROP CONSTRAINT ' || quote_ident(old_constraint_name);
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conrelid = '"E_Attendace".biometric_templates'::regclass
                      AND contype = 'u'
                      AND array_length(conkey, 1) = 5
                ) THEN
                    ALTER TABLE "E_Attendace"."biometric_templates" ADD CONSTRAINT biometric_templates_pin_type_no_idx_ver_key UNIQUE (user_pin, type, no, idx, major_ver);
                END IF;
            END $$;
            """);
        // Add FK separately so we can make it DEFERRABLE (checked at COMMIT not per-row).
        // This prevents 23503 when biometric data arrives before USERINFO in the same txn.
        TryExec(conn, """
            DO $$ BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = 'biometric_templates_user_pin_fkey'
                      AND conrelid = '"E_Attendace".biometric_templates'::regclass
                ) THEN
                    ALTER TABLE "E_Attendace"."biometric_templates"
                        ADD CONSTRAINT biometric_templates_user_pin_fkey
                        FOREIGN KEY (user_pin)
                        REFERENCES "E_Attendace"."users"(pin)
                        ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED;
                END IF;
            END $$;
            """);
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."biometric_photos" (
                id BIGSERIAL PRIMARY KEY,
                user_pin VARCHAR(50) NOT NULL,
                type INT NOT NULL, no INT NOT NULL, idx INT NOT NULL,
                format INT, content TEXT,
                created_at TIMESTAMP NOT NULL DEFAULT NOW(), updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
                UNIQUE (user_pin, type, no, idx)
            );
            """);
        // Add FK separately so we can make it DEFERRABLE.
        TryExec(conn, """
            DO $$ BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = 'biometric_photos_user_pin_fkey'
                      AND conrelid = '"E_Attendace".biometric_photos'::regclass
                ) THEN
                    ALTER TABLE "E_Attendace"."biometric_photos"
                        ADD CONSTRAINT biometric_photos_user_pin_fkey
                        FOREIGN KEY (user_pin)
                        REFERENCES "E_Attendace"."users"(pin)
                        ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED;
                END IF;
            END $$;
            """);

        // ── Backup / restore (single global schedule + run history) ────────────
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."BackupSchedule" (
                "Id" INTEGER PRIMARY KEY DEFAULT 1 CHECK ("Id" = 1),
                "Enabled" INTEGER NOT NULL DEFAULT 1,
                "Frequency" TEXT NOT NULL DEFAULT 'weekly',
                "RunTime" TEXT NOT NULL DEFAULT '12:00',
                "BackupLocation" TEXT NOT NULL DEFAULT '',
                "RetentionCount" INTEGER NOT NULL DEFAULT 7,
                "LastRunUtc" timestamptz NULL,
                "NextRunUtc" timestamptz NULL,
                "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
            );
            """);
        TryExec(conn, """ALTER TABLE "E_Attendace"."BackupSchedule" ADD COLUMN IF NOT EXISTS "RunTime" TEXT NOT NULL DEFAULT '12:00';""");
        // Default is enabled/weekly/12:00 out of the box — the row is only ever inserted once per
        // install (ON CONFLICT DO NOTHING), so an admin's later changes are never overwritten.
        TryExec(conn, """
            INSERT INTO "E_Attendace"."BackupSchedule" ("Id", "Enabled", "Frequency", "RunTime")
            VALUES (1, 1, 'weekly', '12:00') ON CONFLICT ("Id") DO NOTHING;
            """);
        TryExec(conn, """
            CREATE TABLE IF NOT EXISTS "E_Attendace"."BackupHistory" (
                "Id" TEXT PRIMARY KEY,
                "FileName" TEXT NOT NULL,
                "FilePath" TEXT NOT NULL,
                "FileSizeBytes" BIGINT NULL,
                "Status" TEXT NOT NULL,
                "Trigger" TEXT NOT NULL,
                "ErrorMessage" TEXT NULL,
                "StartedAtUtc" timestamptz NOT NULL DEFAULT NOW(),
                "FinishedAtUtc" timestamptz NULL
            );
            """);
        TryExec(conn, """CREATE INDEX IF NOT EXISTS "IX_BackupHistory_StartedAtUtc" ON "E_Attendace"."BackupHistory" ("StartedAtUtc" DESC);""");

        // ── Grant all on everything created ─────────────────────────────────────
        TryExec(conn, $"""GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA "E_Attendace" TO "{dbUser}";""");
        TryExec(conn, $"""GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA "E_Attendace" TO "{dbUser}";""");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Startup] Failed to ensure schema 'E_Attendace': {ex.Message}");
    }
}

static void TryExec(NpgsqlConnection conn, string sql)
{
    try
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }
    catch
    {
        // Best-effort hardening; non-critical statements may fail without admin privileges.
    }
}

static bool CanOpenConnection(string connectionString)
{
    try
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = 3,
            CommandTimeout = 3
        };
        using var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Open();
        return true;
    }
    catch
    {
        return false;
    }
}

static async Task RunAppLogDbWriterAsync(string connectionString, ChannelReader<AppLogEntry> reader, CancellationToken cancellationToken)
{
    var buffer = new List<AppLogEntry>(256);
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(600));

    try
    {
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            DrainChannel(reader, buffer, maxItems: 260);
            if (buffer.Count > 0)
            {
                PersistAppLogsBatch(connectionString, buffer);
                buffer.Clear();
            }
        }
    }
    catch (OperationCanceledException)
    {
        // Fall through to flush remaining items.
    }

    DrainChannel(reader, buffer, maxItems: 4000);
    if (buffer.Count > 0)
    {
        PersistAppLogsBatch(connectionString, buffer);
    }

    static void DrainChannel(ChannelReader<AppLogEntry> reader, List<AppLogEntry> buffer, int maxItems)
    {
        while (buffer.Count < maxItems && reader.TryRead(out var item))
        {
            buffer.Add(item);
        }
    }
}

static void PersistAppLogsBatch(string connectionString, IReadOnlyList<AppLogEntry> entries)
{
    if (entries.Count == 0)
    {
        return;
    }

    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();

        using var cmd = new NpgsqlCommand();
        cmd.Connection = conn;

        var sql = new StringBuilder(
            "INSERT INTO \"E_Attendace\".\"AppLogs\" (\"Id\",\"TimestampUtc\",\"Level\",\"Source\",\"Message\",\"Detail\") VALUES ");

        for (var i = 0; i < entries.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(',');
            }

            sql.Append($"(@id{i},@ts{i},@level{i},@source{i},@message{i},@detail{i})");
            var entry = entries[i];
            cmd.Parameters.AddWithValue($"id{i}", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue($"ts{i}", entry.TimestampUtc);
            cmd.Parameters.AddWithValue($"level{i}", entry.Level ?? string.Empty);
            cmd.Parameters.AddWithValue($"source{i}", entry.Source ?? string.Empty);
            cmd.Parameters.AddWithValue($"message{i}", entry.Message ?? string.Empty);
            cmd.Parameters.AddWithValue($"detail{i}", (object?)entry.Detail ?? DBNull.Value);
        }

        sql.Append(';');
        cmd.CommandText = sql.ToString();
        cmd.ExecuteNonQuery();
    }
    catch
    {
        // Non-blocking: background persistence should not crash the server.
    }
}

static async Task RunTransactionDetailsDbWriterAsync(
    string connectionString,
    ChannelReader<TransactionDetailPersistRequest> reader,
    CancellationToken cancellationToken,
    Action<int> recordPersisted,
    Action<string?> recordFailure)
{
    var buffer = new List<TransactionDetailPersistRequest>(500);
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
    var consecutiveFailures = 0;

    try
    {
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            DrainChannel(reader, buffer, maxItems: 500);
            if (buffer.Count > 0)
            {
                if (PersistTransactionDetailRequests(connectionString, buffer, out var persistError))
                {
                    recordPersisted(buffer.Count);
                    buffer.Clear();
                    consecutiveFailures = 0;
                }
                else
                {
                    recordFailure(persistError);
                    consecutiveFailures++;
                    if (consecutiveFailures >= 5)
                    {
                        // Prevent an unrecoverable poison packet from permanently deadlocking the punch queue
                        buffer.Clear();
                        consecutiveFailures = 0;
                    }
                }
            }
        }
    }
    catch (OperationCanceledException)
    {
        // Fall through to flush remaining items.
    }

    DrainChannel(reader, buffer, maxItems: 10000);
    if (buffer.Count > 0)
    {
        if (PersistTransactionDetailRequests(connectionString, buffer, out var persistError))
        {
            recordPersisted(buffer.Count);
        }
        else
        {
            recordFailure(persistError);
        }
    }

    static void DrainChannel(ChannelReader<TransactionDetailPersistRequest> reader, List<TransactionDetailPersistRequest> buffer, int maxItems)
    {
        while (buffer.Count < maxItems && reader.TryRead(out var item))
        {
            buffer.Add(item);
        }
    }
}

static bool PersistTransactionDetailRequests(string connectionString, IReadOnlyList<TransactionDetailPersistRequest> requests, out string? errorMessage)
{
    errorMessage = null;
    if (requests.Count == 0)
    {
        return true;
    }

    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();

        var rows = new List<TransactionDetailRow>(600);
        var now = DateTime.UtcNow;

        foreach (var request in requests)
        {
            if (string.IsNullOrWhiteSpace(request.Payload))
            {
                continue;
            }

            var normalizedSerial = request.SerialNumber?.Trim() ?? string.Empty;
            var normalizedSource = string.IsNullOrWhiteSpace(request.SourceTable)
                ? string.Empty
                : request.SourceTable.Trim().ToUpperInvariant();

            var lines = request.Payload
                .Replace("\r\n", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static l => !l.StartsWith("OK", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("DATA ", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            foreach (var line in lines)
            {
                var parsedOk = TryParseAttendanceLine(
                    line,
                    out var employeeCode,
                    out var punchTime,
                    out var statusCode,
                    out var verifyMode,
                    out var workcode,
                    out _);

                var safeLine = line.Length > 2000 ? line.Substring(0, 2000) : line;

                rows.Add(new TransactionDetailRow(
                    Guid.NewGuid().ToString("N"),
                    now,
                    normalizedSerial,
                    normalizedSource,
                    safeLine,
                    parsedOk && !string.IsNullOrWhiteSpace(employeeCode) ? employeeCode : null,
                    parsedOk ? punchTime : null,
                    parsedOk ? statusCode : null,
                    parsedOk ? verifyMode : null,
                    parsedOk && !string.IsNullOrWhiteSpace(workcode) ? workcode : null,
                    parsedOk ? 1 : 0));

                if (rows.Count >= 520)
                {
                    PersistTransactionDetailsBatch(conn, rows);
                    rows.Clear();
                    now = DateTime.UtcNow;
                }
            }
        }

        if (rows.Count > 0)
        {
            PersistTransactionDetailsBatch(conn, rows);
        }

        return true;
    }
    catch (Exception ex)
    {
        // Non-blocking: background persistence should not crash the server.
        errorMessage = ex.Message;
        return false;
    }
}

static int StatusCodeForAttendanceStatus(AttendanceStatus status) => status switch
{
    AttendanceStatus.In => 0,
    AttendanceStatus.Out => 1,
    AttendanceStatus.BreakIn => 4,
    AttendanceStatus.BreakOut => 5,
    _ => 0,
};

static void InsertManualTransactionDetail(
    string connectionString,
    string employeeCode,
    DateTime punchTime,
    int? statusCode,
    int? verifyMode,
    string? workcode,
    string? remarks,
    int parsedOk = 1)
{
    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            """
            INSERT INTO "E_Attendace"."TransactionDetails"
            ("Id","ReceivedUtc","DeviceSerialNumber","SourceTable","RawLine","EmployeeCode","PunchTime","StatusCode","VerifyMode","Workcode","ParsedOk","Remarks")
            VALUES (@id,@received,@serial,'MANUAL',@raw,@employee,@punch,@status,@verify,@work,@parsed,@remarks);
            """,
            conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("received", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("serial", "MANUAL");
        cmd.Parameters.AddWithValue("raw", remarks ?? "Manual punch");
        cmd.Parameters.AddWithValue("employee", employeeCode);
        cmd.Parameters.AddWithValue("punch", punchTime);
        cmd.Parameters.AddWithValue("status", statusCode.HasValue ? statusCode.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("verify", verifyMode.HasValue ? verifyMode.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("work", (object?)workcode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("remarks", (object?)remarks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("parsed", parsedOk);
        cmd.ExecuteNonQuery();
    }
    catch
    {
        // Non-blocking
    }
}

static void PersistTransactionDetailsBatch(NpgsqlConnection conn, IReadOnlyList<TransactionDetailRow> rows)
{
    if (rows.Count == 0)
    {
        return;
    }

    using var cmd = new NpgsqlCommand();
    cmd.Connection = conn;

    var sql = new StringBuilder(
        "INSERT INTO \"E_Attendace\".\"TransactionDetails\" " +
        "(\"Id\",\"ReceivedUtc\",\"DeviceSerialNumber\",\"SourceTable\",\"RawLine\",\"EmployeeCode\",\"PunchTime\",\"StatusCode\",\"VerifyMode\",\"Workcode\",\"ParsedOk\") VALUES ");

    for (var i = 0; i < rows.Count; i++)
    {
        if (i > 0)
        {
            sql.Append(',');
        }

        sql.Append($"(@id{i},@received{i},@serial{i},@source{i},@raw{i},@employee{i},@punch{i},@status{i},@verify{i},@work{i},@parsed{i})");
        var row = rows[i];
        cmd.Parameters.AddWithValue($"id{i}", row.Id);
        cmd.Parameters.AddWithValue($"received{i}", row.ReceivedUtc);
        cmd.Parameters.AddWithValue($"serial{i}", string.IsNullOrWhiteSpace(row.DeviceSerialNumber) ? (object)DBNull.Value : row.DeviceSerialNumber);
        cmd.Parameters.AddWithValue($"source{i}", row.SourceTable);
        cmd.Parameters.AddWithValue($"raw{i}", row.RawLine);
        cmd.Parameters.AddWithValue($"employee{i}", (object?)row.EmployeeCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue($"punch{i}", row.PunchTime.HasValue ? row.PunchTime.Value : DBNull.Value);
        cmd.Parameters.AddWithValue($"status{i}", row.StatusCode.HasValue ? row.StatusCode.Value : DBNull.Value);
        cmd.Parameters.AddWithValue($"verify{i}", row.VerifyMode.HasValue ? row.VerifyMode.Value : DBNull.Value);
        cmd.Parameters.AddWithValue($"work{i}", (object?)row.Workcode ?? DBNull.Value);
        cmd.Parameters.AddWithValue($"parsed{i}", row.ParsedOk);
    }

    sql.Append(" ON CONFLICT DO NOTHING;");
    cmd.CommandText = sql.ToString();
    cmd.ExecuteNonQuery();
}

static IReadOnlyList<AppLogEntry> LoadAppLogs(
    string connectionString,
    string? search,
    string? level,
    string? source,
    int take)
{
    var rows = new List<AppLogEntry>();
    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        var sql = new StringBuilder(
            """
            SELECT "TimestampUtc","Level","Source","Message","Detail"
            FROM "E_Attendace"."AppLogs"
            WHERE 1=1
            """);
        using var cmd = new NpgsqlCommand();
        cmd.Connection = conn;

        if (!string.IsNullOrWhiteSpace(search))
        {
            sql.Append(""" AND ("Message" ILIKE @search OR COALESCE("Detail",'') ILIKE @search)""");
            cmd.Parameters.AddWithValue("search", $"%{search.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(level))
        {
            sql.Append(""" AND "Level" ILIKE @level""");
            cmd.Parameters.AddWithValue("level", level.Trim());
        }
        if (!string.IsNullOrWhiteSpace(source))
        {
            sql.Append(""" AND "Source" ILIKE @source""");
            cmd.Parameters.AddWithValue("source", source.Trim());
        }

        sql.Append(""" ORDER BY "TimestampUtc" DESC LIMIT @take""");
        cmd.Parameters.AddWithValue("take", take);
        cmd.CommandText = sql.ToString();

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AppLogEntry(
                reader.GetFieldValue<DateTime>(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
    }
    catch
    {
        // Fallback below.
    }

    return rows;
}

static (IReadOnlyList<AppLogEntry> Data, int Total) LoadAppLogsPaged(
    string connectionString,
    string? search,
    string? level,
    string? source,
    DateTime? fromUtc,
    DateTime? toUtc,
    int take,
    int skip)
{
    var rows = new List<AppLogEntry>();
    int total = 0;
    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();

        var where = new StringBuilder(" WHERE 1=1");
        using var countCmd = new NpgsqlCommand();
        using var dataCmd = new NpgsqlCommand();
        countCmd.Connection = conn;
        dataCmd.Connection = conn;

        if (fromUtc.HasValue)
        {
            where.Append(""" AND "TimestampUtc" >= @fromUtc""");
            countCmd.Parameters.AddWithValue("fromUtc", fromUtc.Value);
            dataCmd.Parameters.AddWithValue("fromUtc", fromUtc.Value);
        }
        if (toUtc.HasValue)
        {
            where.Append(""" AND "TimestampUtc" <= @toUtc""");
            countCmd.Parameters.AddWithValue("toUtc", toUtc.Value);
            dataCmd.Parameters.AddWithValue("toUtc", toUtc.Value);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Append(""" AND ("Message" ILIKE @search OR COALESCE("Detail",'') ILIKE @search)""");
            countCmd.Parameters.AddWithValue("search", $"%{search.Trim()}%");
            dataCmd.Parameters.AddWithValue("search", $"%{search.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(level))
        {
            where.Append(""" AND "Level" ILIKE @level""");
            countCmd.Parameters.AddWithValue("level", level.Trim());
            dataCmd.Parameters.AddWithValue("level", level.Trim());
        }
        if (!string.IsNullOrWhiteSpace(source))
        {
            where.Append(""" AND "Source" ILIKE @source""");
            countCmd.Parameters.AddWithValue("source", source.Trim());
            dataCmd.Parameters.AddWithValue("source", source.Trim());
        }

        countCmd.CommandText = $"""SELECT COUNT(*) FROM "E_Attendace"."AppLogs"{where}""";
        total = (int)(long)countCmd.ExecuteScalar()!;

        dataCmd.Parameters.AddWithValue("take", take);
        dataCmd.Parameters.AddWithValue("skip", skip);
        dataCmd.CommandText = $"""
            SELECT "TimestampUtc","Level","Source","Message","Detail"
            FROM "E_Attendace"."AppLogs"{where}
            ORDER BY "TimestampUtc" DESC LIMIT @take OFFSET @skip
            """;

        using var reader = dataCmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AppLogEntry(
                reader.GetFieldValue<DateTime>(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
    }
    catch
    {
        // On error return empty with 0 total.
    }

    return (rows, total);
}

static void ClearAppLogs(string connectionString)
{
    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand("""DELETE FROM "E_Attendace"."AppLogs";""", conn);
        cmd.ExecuteNonQuery();
    }
    catch
    {
    }
}

static IReadOnlyList<object> LoadTransactionDetails(
    string connectionString,
    DateTime? fromUtc,
    DateTime? toUtc,
    string? search,
    string? deviceSerial,
    string[]? filterCodes,
    int take,
    IReadOnlyDictionary<string, Employee> employeeByCode,
    string fallbackDepartment,
    bool usePunchTime,
    bool lite,
    bool approvedOnly = false)
{
    var rows = new List<object>();
    try
    {
        var searchToken = search?.Trim();
        var employeeSearchCodes = Array.Empty<string>();
        if (!string.IsNullOrWhiteSpace(searchToken))
        {
            // Search by employee NAME only here; employee ID (EmployeeCode) is matched directly
            // in the SQL WHERE below. Device serial / workcode / department are intentionally
            // excluded so search behaves as "by employee ID or name" only.
            employeeSearchCodes = employeeByCode.Values
                .Where(e => !string.IsNullOrWhiteSpace(e.FullName) && e.FullName.Contains(searchToken, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.EmployeeCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand();
        cmd.Connection = conn;

        // Use PunchTime when requested, but fall back to ReceivedUtc for rows where PunchTime
        // is NULL (e.g. failed-parse rows kept for audit). This ensures all records are visible.
        var timeColumn = usePunchTime
            ? "COALESCE(\"PunchTime\", \"ReceivedUtc\")"
            : "\"ReceivedUtc\"";
        var selectColumns = lite
            ? "\"Id\",\"ReceivedUtc\",\"DeviceSerialNumber\",\"EmployeeCode\",\"PunchTime\",\"StatusCode\",\"VerifyMode\",\"Remarks\""
            : "\"Id\",\"ReceivedUtc\",\"DeviceSerialNumber\",\"SourceTable\",\"RawLine\",\"EmployeeCode\",\"PunchTime\",\"StatusCode\",\"VerifyMode\",\"Workcode\",\"ParsedOk\",\"Remarks\"";

        var sql = new StringBuilder(
            $"SELECT {selectColumns} FROM \"E_Attendace\".\"TransactionDetails\" WHERE 1=1");

        if (filterCodes is not null)
        {
            sql.Append(" AND \"EmployeeCode\" = ANY(@filterCodes)");
            cmd.Parameters.AddWithValue("filterCodes", filterCodes);
        }

        if (fromUtc.HasValue)
        {
            sql.Append($" AND {timeColumn} >= @fromUtc");
            cmd.Parameters.AddWithValue("fromUtc", fromUtc.Value);
        }
        if (toUtc.HasValue)
        {
            sql.Append($" AND {timeColumn} <= @toUtc");
            cmd.Parameters.AddWithValue("toUtc", toUtc.Value);
        }
        if (!string.IsNullOrWhiteSpace(deviceSerial))
        {
            sql.Append(""" AND COALESCE("DeviceSerialNumber",'') ILIKE @deviceSerial""");
            cmd.Parameters.AddWithValue("deviceSerial", $"%{deviceSerial.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(searchToken))
        {
            sql.Append(
                " AND (" +
                "COALESCE(\"EmployeeCode\",'') ILIKE @search OR " +
                "COALESCE(\"DeviceSerialNumber\",'') ILIKE @search OR " +
                "COALESCE(\"Workcode\",'') ILIKE @search OR " +
                "COALESCE(\"RawLine\",'') ILIKE @search");
            if (employeeSearchCodes.Length > 0)
            {
                sql.Append(" OR \"EmployeeCode\" = ANY(@employeeCodes)");
                cmd.Parameters.AddWithValue("employeeCodes", employeeSearchCodes);
            }
            sql.Append(")");
            cmd.Parameters.AddWithValue("search", $"%{searchToken}%");
        }

        sql.Append(""" AND "SourceTable" IN ('ATTLOG','OPERLOG','MANUAL')""");
        // approvedOnly: reports show only approved punches (ParsedOk=1) so pending QR/manual
        // requests don't count until an admin approves them.
        // approvedOnly restricts to ParsedOk=1 for reports. Normal view shows everything except
        // device-side raw data that was stored but never parsed (ParsedOk=0 rows are still kept
        // for audit but are filtered out from dashboard to prevent blank/bad records).
        if (approvedOnly)
        {
            sql.Append(""" AND "ParsedOk" = 1""");
        }
        else
        {
            sql.Append(""" AND "ParsedOk" IN (1, 2, 3)""");
        }
        sql.Append($" ORDER BY {timeColumn} DESC LIMIT @take");
        cmd.Parameters.AddWithValue("take", take);
        cmd.CommandText = sql.ToString();

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (lite)
            {
                var code = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                employeeByCode.TryGetValue(code, out var employee);
                rows.Add(new
                {
                    id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    rawTimePc = reader.IsDBNull(1) ? (DateTime?)null : reader.GetFieldValue<DateTime>(1),
                    deviceSerialNumber = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    sourceTable = string.Empty,
                    rawLine = string.Empty,
                    employeeCode = code,
                    employeeName = employee?.FullName ?? code,
                    employeeDepartment = string.IsNullOrWhiteSpace(employee?.Department)
                        ? fallbackDepartment
                        : employee!.Department,
                    punchTime = reader.IsDBNull(4) ? (DateTime?)null : reader.GetFieldValue<DateTime>(4),
                    statusCode = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5),
                    statusDescription = reader.IsDBNull(5) ? string.Empty : DescribeAttendanceStatusCode(reader.GetInt32(5)),
                    verifyMode = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6),
                    verifyModeDescription = reader.IsDBNull(6) ? string.Empty : DescribeVerifyMode(reader.GetInt32(6)),
                    workcode = string.Empty,
                    remarks = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    parsedOk = 1
                });
            }
            else
            {
                var code = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                employeeByCode.TryGetValue(code, out var employee);
                rows.Add(new
                {
                    id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    rawTimePc = reader.IsDBNull(1) ? (DateTime?)null : reader.GetFieldValue<DateTime>(1),
                    deviceSerialNumber = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    sourceTable = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    rawLine = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    employeeCode = code,
                    employeeName = employee?.FullName ?? code,
                    employeeDepartment = string.IsNullOrWhiteSpace(employee?.Department)
                        ? fallbackDepartment
                        : employee!.Department,
                    punchTime = reader.IsDBNull(6) ? (DateTime?)null : reader.GetFieldValue<DateTime>(6),
                    statusCode = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7),
                    statusDescription = reader.IsDBNull(7) ? string.Empty : DescribeAttendanceStatusCode(reader.GetInt32(7)),
                    verifyMode = reader.IsDBNull(8) ? (int?)null : reader.GetInt32(8),
                    verifyModeDescription = reader.IsDBNull(8) ? string.Empty : DescribeVerifyMode(reader.GetInt32(8)),
                    workcode = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                    parsedOk = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                    remarks = reader.IsDBNull(11) ? string.Empty : reader.GetString(11)
                });
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[LoadTransactionDetails] Database query error: {ex.Message}");
    }

    return rows;
}

static (IReadOnlyList<object> Data, int Total) LoadTransactionDetailsPaged(
    string connectionString,
    DateTime? fromUtc,
    DateTime? toUtc,
    string? search,
    string? deviceSerial,
    string[]? filterCodes,
    int take,
    int skip,
    IReadOnlyDictionary<string, Employee> employeeByCode,
    string fallbackDepartment,
    bool usePunchTime,
    bool lite)
{
    var rows = new List<object>();
    int total = 0;
    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();

        var searchToken = search?.Trim();
        var employeeSearchCodes = Array.Empty<string>();
        if (!string.IsNullOrWhiteSpace(searchToken))
        {
            // Search by employee NAME only here; employee ID (EmployeeCode) is matched directly
            // in the SQL WHERE below. Device serial / workcode / department are intentionally
            // excluded so search behaves as "by employee ID or name" only.
            employeeSearchCodes = employeeByCode.Values
                .Where(e => !string.IsNullOrWhiteSpace(e.FullName) && e.FullName.Contains(searchToken, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.EmployeeCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var timeColumn = usePunchTime
            ? "COALESCE(\"PunchTime\", \"ReceivedUtc\")"
            : "\"ReceivedUtc\"";
        var selectColumns = lite
            ? "\"Id\",\"ReceivedUtc\",\"DeviceSerialNumber\",\"EmployeeCode\",\"PunchTime\",\"StatusCode\",\"VerifyMode\",\"Remarks\""
            : "\"Id\",\"ReceivedUtc\",\"DeviceSerialNumber\",\"SourceTable\",\"RawLine\",\"EmployeeCode\",\"PunchTime\",\"StatusCode\",\"VerifyMode\",\"Workcode\",\"ParsedOk\",\"Remarks\"";

        var whereBuilder = new StringBuilder(" WHERE 1=1");
        var paramList = new List<(string Name, object Value)>();

        if (filterCodes is not null)
        {
            whereBuilder.Append(" AND \"EmployeeCode\" = ANY(@filterCodes)");
            paramList.Add(("filterCodes", filterCodes));
        }

        if (fromUtc.HasValue)
        {
            whereBuilder.Append($" AND {timeColumn} >= @fromUtc");
            paramList.Add(("fromUtc", fromUtc.Value));
        }
        if (toUtc.HasValue)
        {
            whereBuilder.Append($" AND {timeColumn} <= @toUtc");
            paramList.Add(("toUtc", toUtc.Value));
        }
        if (!string.IsNullOrWhiteSpace(deviceSerial))
        {
            whereBuilder.Append(""" AND COALESCE("DeviceSerialNumber",'') ILIKE @deviceSerial""");
            paramList.Add(("deviceSerial", $"%{deviceSerial.Trim()}%"));
        }
        if (!string.IsNullOrWhiteSpace(searchToken))
        {
            // Match employee ID (EmployeeCode) directly, plus codes resolved from a name match.
            whereBuilder.Append(" AND (COALESCE(\"EmployeeCode\",'') ILIKE @search");
            if (employeeSearchCodes.Length > 0)
            {
                whereBuilder.Append(" OR \"EmployeeCode\" = ANY(@employeeCodes)");
                paramList.Add(("employeeCodes", employeeSearchCodes));
            }
            whereBuilder.Append(")");
            paramList.Add(("search", $"%{searchToken}%"));
        }

        whereBuilder.Append(""" AND "SourceTable" IN ('ATTLOG','OPERLOG','MANUAL')""");
        // Filter out unparsed raw logs (ParsedOk = 0) to avoid bad/blank dashboard entries.
        whereBuilder.Append(""" AND "ParsedOk" IN (1, 2, 3)""");

        var whereStr = whereBuilder.ToString();
        var tableName = "\"E_Attendace\".\"TransactionDetails\"";

        // COUNT query
        using var countCmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {tableName}{whereStr}", conn);
        foreach (var (name, value) in paramList)
            countCmd.Parameters.AddWithValue(name, value);
        total = (int)(long)countCmd.ExecuteScalar()!;

        // DATA query
        using var dataCmd = new NpgsqlCommand(
            $"SELECT {selectColumns} FROM {tableName}{whereStr} ORDER BY {timeColumn} DESC LIMIT @take OFFSET @skip", conn);
        foreach (var (name, value) in paramList)
            dataCmd.Parameters.AddWithValue(name, value);
        dataCmd.Parameters.AddWithValue("take", take);
        dataCmd.Parameters.AddWithValue("skip", skip);

        using var reader = dataCmd.ExecuteReader();
        while (reader.Read())
        {
            if (lite)
            {
                var code = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                employeeByCode.TryGetValue(code, out var employee);
                rows.Add(new
                {
                    id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    rawTimePc = reader.IsDBNull(1) ? (DateTime?)null : reader.GetFieldValue<DateTime>(1),
                    deviceSerialNumber = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    sourceTable = string.Empty,
                    rawLine = string.Empty,
                    employeeCode = code,
                    employeeName = employee?.FullName ?? code,
                    employeeDepartment = string.IsNullOrWhiteSpace(employee?.Department) ? fallbackDepartment : employee!.Department,
                    punchTime = reader.IsDBNull(4) ? (DateTime?)null : reader.GetFieldValue<DateTime>(4),
                    statusCode = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5),
                    statusDescription = reader.IsDBNull(5) ? string.Empty : DescribeAttendanceStatusCode(reader.GetInt32(5)),
                    verifyMode = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6),
                    verifyModeDescription = reader.IsDBNull(6) ? string.Empty : DescribeVerifyMode(reader.GetInt32(6)),
                    workcode = string.Empty,
                    remarks = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    parsedOk = 1
                });
            }
            else
            {
                var code = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                employeeByCode.TryGetValue(code, out var employee);
                rows.Add(new
                {
                    id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    rawTimePc = reader.IsDBNull(1) ? (DateTime?)null : reader.GetFieldValue<DateTime>(1),
                    deviceSerialNumber = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    sourceTable = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    rawLine = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    employeeCode = code,
                    employeeName = employee?.FullName ?? code,
                    employeeDepartment = string.IsNullOrWhiteSpace(employee?.Department) ? fallbackDepartment : employee!.Department,
                    punchTime = reader.IsDBNull(6) ? (DateTime?)null : reader.GetFieldValue<DateTime>(6),
                    statusCode = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7),
                    statusDescription = reader.IsDBNull(7) ? string.Empty : DescribeAttendanceStatusCode(reader.GetInt32(7)),
                    verifyMode = reader.IsDBNull(8) ? (int?)null : reader.GetInt32(8),
                    verifyModeDescription = reader.IsDBNull(8) ? string.Empty : DescribeVerifyMode(reader.GetInt32(8)),
                    workcode = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                    parsedOk = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                    remarks = reader.IsDBNull(11) ? string.Empty : reader.GetString(11)
                });
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[LoadTransactionDetailsPaged] Database query error: {ex.Message}");
    }

    return (rows, total);
}

static List<HolidayListRow> LoadHolidayRows(string connectionString)
{
    var rows = new List<HolidayListRow>();
    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            """
            SELECT "Id","HolidayDate","HolidayName",COALESCE("Description",''),"IsRecurring","IsActive"
            FROM "E_Attendace"."Holidays"
            ORDER BY "HolidayDate" ASC, "HolidayName" ASC;
            """,
            conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new HolidayListRow(
                reader.GetInt32(0),
                reader.GetFieldValue<DateOnly>(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                !reader.IsDBNull(4) && reader.GetBoolean(4),
                reader.IsDBNull(5) || reader.GetBoolean(5),
                "all"));
        }
    }
    catch
    {
        // Return empty list on DB read failure; caller still gets valid JSON.
    }

    return rows;
}

static bool HolidayExists(string connectionString, DateOnly holidayDate, int? excludeId = null)
{
    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            """
            SELECT COUNT(1)
            FROM "E_Attendace"."Holidays"
            WHERE "HolidayDate" = @date
              AND (@excludeId IS NULL OR "Id" <> @excludeId);
            """,
            conn);
        cmd.Parameters.AddWithValue("date", holidayDate);
        cmd.Parameters.AddWithValue("excludeId", excludeId.HasValue ? excludeId.Value : DBNull.Value);
        var value = cmd.ExecuteScalar();
        return value is not null && value is not DBNull && Convert.ToInt32(value, CultureInfo.InvariantCulture) > 0;
    }
    catch
    {
        return false;
    }
}

static HolidayListRow CreateHoliday(
    string connectionString,
    DateOnly holidayDate,
    string holidayName,
    string description,
    bool isRecurring,
    bool isActive)
{
    using var conn = new NpgsqlConnection(connectionString);
    conn.Open();
    using var cmd = new NpgsqlCommand(
        """
        INSERT INTO "E_Attendace"."Holidays" ("HolidayDate","HolidayName","Description","IsRecurring","DepartmentId","IsActive")
        VALUES (@date,@name,@description,@isRecurring,NULL,@isActive)
        RETURNING "Id","HolidayDate","HolidayName",COALESCE("Description",''),"IsRecurring","IsActive";
        """,
        conn);
    cmd.Parameters.AddWithValue("date", holidayDate);
    cmd.Parameters.AddWithValue("name", holidayName);
    cmd.Parameters.AddWithValue("description", string.IsNullOrWhiteSpace(description) ? DBNull.Value : description);
    cmd.Parameters.AddWithValue("isRecurring", isRecurring);
    cmd.Parameters.AddWithValue("isActive", isActive);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read())
    {
        throw new InvalidOperationException("Failed to create holiday.");
    }

    return new HolidayListRow(
        reader.GetInt32(0),
        reader.GetFieldValue<DateOnly>(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
        reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
        !reader.IsDBNull(4) && reader.GetBoolean(4),
        reader.IsDBNull(5) || reader.GetBoolean(5),
        "all");
}

static HolidayListRow? UpdateHoliday(
    string connectionString,
    int id,
    DateOnly holidayDate,
    string holidayName,
    string description,
    bool isRecurring,
    bool isActive)
{
    using var conn = new NpgsqlConnection(connectionString);
    conn.Open();
    using var cmd = new NpgsqlCommand(
        """
        UPDATE "E_Attendace"."Holidays"
        SET "HolidayDate" = @date,
            "HolidayName" = @name,
            "Description" = @description,
            "IsRecurring" = @isRecurring,
            "IsActive" = @isActive
        WHERE "Id" = @id
        RETURNING "Id","HolidayDate","HolidayName",COALESCE("Description",''),"IsRecurring","IsActive";
        """,
        conn);
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("date", holidayDate);
    cmd.Parameters.AddWithValue("name", holidayName);
    cmd.Parameters.AddWithValue("description", string.IsNullOrWhiteSpace(description) ? DBNull.Value : description);
    cmd.Parameters.AddWithValue("isRecurring", isRecurring);
    cmd.Parameters.AddWithValue("isActive", isActive);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read())
    {
        return null;
    }

    return new HolidayListRow(
        reader.GetInt32(0),
        reader.GetFieldValue<DateOnly>(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
        reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
        !reader.IsDBNull(4) && reader.GetBoolean(4),
        reader.IsDBNull(5) || reader.GetBoolean(5),
        "all");
}

static bool DeleteHoliday(string connectionString, int id)
{
    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            """
            DELETE FROM "E_Attendace"."Holidays"
            WHERE "Id" = @id;
            """,
            conn);
        cmd.Parameters.AddWithValue("id", id);
        return cmd.ExecuteNonQuery() > 0;
    }
    catch
    {
        return false;
    }
}

static bool IsDeviceCurrentlyOnline(DateTime? lastSeenUtc, bool isOnlineFlag)
{
    if (!lastSeenUtc.HasValue)
    {
        return false;
    }

    return DateTime.UtcNow - lastSeenUtc.Value <= TimeSpan.FromMinutes(5);
}

static Dictionary<string, int> LoadDevicePunchCounts(string connectionString, IEnumerable<string> serialNumbers)
{
    var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var serials = serialNumbers
        .Where(static s => !string.IsNullOrWhiteSpace(s))
        .Select(static s => s.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    if (serials.Length == 0)
    {
        return result;
    }

    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            """
            SELECT COALESCE(t."DeviceSerialNumber", a."DeviceSerialNumber") AS "SerialNumber",
                   GREATEST(COALESCE(t.cnt, 0), COALESCE(a.cnt, 0)) AS "PunchCount"
            FROM (
                SELECT "DeviceSerialNumber", COUNT(DISTINCT ("EmployeeCode", "PunchTime")) AS cnt
                FROM "E_Attendace"."TransactionDetails"
                WHERE "SourceTable" = 'ATTLOG' AND COALESCE("DeviceSerialNumber",'') <> ''
                GROUP BY "DeviceSerialNumber"
            ) t
            FULL OUTER JOIN (
                SELECT "DeviceSerialNumber", COUNT(DISTINCT ("EmployeeId", "Timestamp")) AS cnt
                FROM "E_Attendace"."AttendanceRecords"
                WHERE COALESCE("DeviceSerialNumber",'') <> ''
                GROUP BY "DeviceSerialNumber"
            ) a ON t."DeviceSerialNumber" = a."DeviceSerialNumber";
            """,
            conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var serial = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (string.IsNullOrWhiteSpace(serial))
            {
                continue;
            }
            result[serial] = reader.GetInt32(1);
        }
    }
    catch
    {
        // Non-fatal; caller gets empty counts.
    }

    return result;
}

static DateTime? LoadLatestPunchTimeForDevice(string connectionString, string serialNumber)
{
    if (string.IsNullOrWhiteSpace(serialNumber))
    {
        return null;
    }

    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            """
            SELECT MAX("PunchTime")
            FROM "E_Attendace"."TransactionDetails"
            WHERE COALESCE("DeviceSerialNumber",'') = @serial
              AND "SourceTable" IN ('ATTLOG','OPERLOG')
              AND "PunchTime" IS NOT NULL;
            """,
            conn);
        cmd.Parameters.AddWithValue("serial", serialNumber.Trim());
        var value = cmd.ExecuteScalar();
        if (value is null || value is DBNull)
        {
            return null;
        }

        return Convert.ToDateTime(value, CultureInfo.InvariantCulture);
    }
    catch
    {
        return null;
    }
}

static string DescribeAttendanceStatusCode(int statusCode)
{
    return statusCode switch
    {
        0 => "Check-In",
        1 => "Check-Out",
        2 => "Overtime in",
        3 => "Overtime out",
        4 => "Break in",
        5 => "Break out",
        255 => "Auto",
        _ => $"Status {statusCode}"
    };
}

static string DescribeVerifyMode(int verifyMode)
{
    return verifyMode switch
    {
        -1 => "Inherit from group mode",
        0 => "Auto identify (vein OR face OR fingerprint OR card OR password)",
        1 => "Fingerprint only",
        2 => "User ID only (PIN)",
        3 => "Password only",
        4 => "Card only",
        5 => "Fingerprint OR password",
        6 => "Fingerprint OR card",
        7 => "Card OR password",
        8 => "User ID + fingerprint",
        9 => "Fingerprint + password",
        10 => "Card + fingerprint",
        11 => "Card + password",
        12 => "Fingerprint + password + card",
        13 => "User ID + fingerprint + password",
        14 => "(User ID + fingerprint) OR (card + fingerprint)",
        15 => "Face only",
        16 => "Face + fingerprint",
        17 => "Face + password",
        18 => "Face + card",
        19 => "Face + fingerprint + card",
        20 => "Face + fingerprint + password",
        21 => "Finger vein only",
        22 => "Finger vein + password",
        23 => "Finger vein + card",
        24 => "Finger vein + password + card",
        25 => "Palm print only",
        26 => "Palm print + card",
        27 => "Palm print + face",
        28 => "Palm print + fingerprint",
        29 => "Palm print + fingerprint + face",
        200 => "Other/custom mode",
        _ => $"Mode {verifyMode}"
    };
}

static int ResolveDeviceSyncVerifyMode(int? profileVerifyMode, int employeeVerifyMode)
{
    return ProgramHelpers.NormalizeEmployeeVerifyMode(profileVerifyMode, employeeVerifyMode);
}

static string ResolveExpectedPostgreSqlPassword()
{
    var env = Environment.GetEnvironmentVariable("PB_ATTENDANCE_DB_PASSWORD");
    if (!string.IsNullOrWhiteSpace(env))
    {
        return env;
    }

    const string fallbackBase64 = "UEJAMTQ4OTEyNzc=";
    return Encoding.UTF8.GetString(Convert.FromBase64String(fallbackBase64));
}

static bool LooksLikeMd5Hash(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    return Regex.IsMatch(value.Trim(), "^[a-fA-F0-9]{32}$");
}

static string ToMd5Hash(string plainText)
{
    var input = plainText ?? string.Empty;
    var bytes = Encoding.UTF8.GetBytes(input);
    using var md5 = MD5.Create();
    var hash = md5.ComputeHash(bytes);
    var sb = new StringBuilder(hash.Length * 2);
    foreach (var b in hash)
    {
        sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
    }

    return sb.ToString();
}

static string NormalizePasswordHash(string? value)
{
    var token = value?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(token))
    {
        return string.Empty;
    }

    return LooksLikeMd5Hash(token) ? token.ToLowerInvariant() : ToMd5Hash(token);
}

static string ResolveUiPassword(string? profilePassword)
{
    var token = profilePassword?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(token))
    {
        return string.Empty;
    }

    if (LooksLikeMd5Hash(token))
    {
        return string.Empty;
    }

    return IsValidDevicePassword(token) ? token : string.Empty;
}

static string[] GetSoftwareFeatureKeys()
{
    return
    [
        "dashboard",
        "departments",
        "devices",
        "employees",
        "timetable",
        "shift",
        "assign",
        "transactions",
        "leaves",
        "reports",
        "logs",
        "settings"
    ];
}

static Dictionary<string, bool> CreateSoftwareFeatureAccess(bool allEnabled = false)
{
    var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    foreach (var key in GetSoftwareFeatureKeys())
    {
        result[key] = allEnabled || string.Equals(key, "dashboard", StringComparison.OrdinalIgnoreCase);
    }

    return result;
}

static Dictionary<string, bool> NormalizeSoftwareFeatureAccess(
    IReadOnlyDictionary<string, bool>? source,
    bool allEnabled = false)
{
    var result = CreateSoftwareFeatureAccess(allEnabled);
    if (allEnabled || source is null)
    {
        return result;
    }

    foreach (var key in GetSoftwareFeatureKeys())
    {
        if (source.TryGetValue(key, out var value))
        {
            result[key] = value;
        }
    }

    return result;
}

static Dictionary<string, bool> DeserializeSoftwareFeatureAccess(string? rawJson, bool allEnabled = false)
{
    if (allEnabled)
    {
        return CreateSoftwareFeatureAccess(true);
    }

    if (string.IsNullOrWhiteSpace(rawJson))
    {
        return CreateSoftwareFeatureAccess();
    }

    try
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, bool>>(rawJson);
        return NormalizeSoftwareFeatureAccess(parsed);
    }
    catch
    {
        return CreateSoftwareFeatureAccess();
    }
}

static string SerializeSoftwareFeatureAccess(IReadOnlyDictionary<string, bool>? source, bool allEnabled = false)
{
    var normalized = NormalizeSoftwareFeatureAccess(source, allEnabled);
    return JsonSerializer.Serialize(normalized);
}

static string NormalizeAuthRole(string? rawRole, bool isAdmin)
{
    if (isAdmin)
    {
        return "Admin";
    }

    var token = rawRole?.Trim() ?? string.Empty;
    return token switch
    {
        "Admin" => "Admin",
        "Manager" => "Manager",
        _ => "Operator"
    };
}

static string ToIsoUtcString(object? value)
{
    return value switch
    {
        DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTime dateTime => dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        _ => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
    };
}

static string ReadAppConfigJson(string connectionString, string configKey)
{
    if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(configKey))
    {
        return "{}";
    }

    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            """
            SELECT COALESCE("JsonValue"::text, '{}')
            FROM "E_Attendace"."app_config"
            WHERE "ConfigKey" = @configKey
            LIMIT 1;
            """,
            conn);
        cmd.Parameters.AddWithValue("configKey", configKey.Trim());
        var value = cmd.ExecuteScalar()?.ToString();
        return string.IsNullOrWhiteSpace(value) ? "{}" : value;
    }
    catch
    {
        return "{}";
    }
}

static void SaveAppConfigJson(string connectionString, string configKey, string rawJson)
{
    if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(configKey))
    {
        return;
    }

    var payload = string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson;

    using var conn = new NpgsqlConnection(connectionString);
    conn.Open();
    using var cmd = new NpgsqlCommand(
        """
        INSERT INTO "E_Attendace"."app_config" ("ConfigKey", "JsonValue", "UpdatedAtUtc")
        VALUES (@configKey, CAST(@jsonValue AS jsonb), NOW())
        ON CONFLICT ("ConfigKey") DO UPDATE
        SET "JsonValue" = EXCLUDED."JsonValue",
            "UpdatedAtUtc" = NOW();
        """,
        conn);
    cmd.Parameters.AddWithValue("configKey", configKey.Trim());
    cmd.Parameters.AddWithValue("jsonValue", payload);
    cmd.ExecuteNonQuery();
}

// Ensures every area has its default access-control entities (Time Period 1 = 00:00-23:59,
// Access Group 1 = Verify Auto + TP1, Unlock Combination 1 = Group 1) and that each carries the
// serials of the devices currently in that area, then syncs them to those devices. Runs
// server-side so the defaults exist without anyone opening the Access Control page.
static async Task EnsureAccessControlDefaultsForAllAreas(
    string connectionString,
    AttendanceService service,
    PersonnelStore personnelStore,
    ConfigSyncService syncService,
    bool syncWhenUnchangedWithDevices = true,
    bool syncWhenChanged = true)
{
    try
    {
        HydratePersonnelStoreFromDb(personnelStore, connectionString);
        var json = ReadAppConfigJson(connectionString, "access-control");
        var root = (JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject) ?? new JsonObject();
        if (root["timePeriods"] is not JsonArray timePeriods) { timePeriods = new JsonArray(); root["timePeriods"] = timePeriods; }
        if (root["groups"] is not JsonArray groups) { groups = new JsonArray(); root["groups"] = groups; }
        if (root["combinations"] is not JsonArray combinations) { combinations = new JsonArray(); root["combinations"] = combinations; }
        if (root["privileges"] is not JsonArray privileges) { privileges = new JsonArray(); root["privileges"] = privileges; }

        var devices = service.ListDevices().ToList();
        var now = DateTime.UtcNow.ToString("O");
        var changed = false;
        var hasDevicesInArea = false;
        string NewId(string p) => $"{p}-{DateTime.UtcNow.Ticks}-{Guid.NewGuid().ToString("N")[..6]}";

        foreach (var area in personnelStore.Areas)
        {
            var areaName = area.Name?.Trim() ?? string.Empty;
            var areaCode = area.Code?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(areaName)) continue;

            var serials = devices
                .Where(d => string.Equals(d.Location, areaName, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrWhiteSpace(areaCode) && string.Equals(d.Location, areaCode, StringComparison.OrdinalIgnoreCase)))
                .Select(d => d.SerialNumber)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (serials.Count > 0)
            {
                hasDevicesInArea = true;
            }

            JsonArray SerialArray() { var a = new JsonArray(); foreach (var s in serials) a.Add(s); return a; }
            bool SerialsDiffer(JsonNode? node)
            {
                var existing = (node as JsonArray)?.Select(x => x?.ToString() ?? string.Empty).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
                return !existing.SetEquals(serials);
            }

            JsonObject? Find(JsonArray arr, string slotKey, string slotVal) => arr.OfType<JsonObject>().FirstOrDefault(o =>
            {
                var itemArea = (string?)o["area"] ?? string.Empty;
                var areaMatch = string.Equals(itemArea, areaName, StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrWhiteSpace(areaCode) && string.Equals(itemArea, areaCode, StringComparison.OrdinalIgnoreCase));
                var slotValStr = o[slotKey]?.ToString()?.Trim() ?? string.Empty;
                return areaMatch && string.Equals(slotValStr, slotVal, StringComparison.OrdinalIgnoreCase);
            });

            // 1. Time Period 1
            var tp1 = Find(timePeriods, "periodNo", "1");
            string tp1Id;
            if (tp1 is null)
            {
                tp1Id = NewId("period");
                var days = new JsonArray();
                for (var i = 0; i < 7; i++) days.Add(new JsonObject { ["dayOfWeek"] = i, ["startTime"] = "00:00:00", ["endTime"] = "23:59:00", ["isActive"] = true });
                timePeriods.Add(new JsonObject { ["id"] = tp1Id, ["area"] = areaName, ["department"] = "All", ["deviceSerials"] = SerialArray(), ["periodNo"] = "1", ["name"] = "Time Period 1", ["remark"] = "Default", ["days"] = days, ["createdAt"] = now, ["updatedAt"] = now });
                changed = true;
            }
            else
            {
                tp1Id = (string?)tp1["id"] ?? NewId("period");
                if (SerialsDiffer(tp1["deviceSerials"]) || !string.Equals((string?)tp1["area"], areaName, StringComparison.Ordinal))
                {
                    tp1["area"] = areaName;
                    tp1["deviceSerials"] = SerialArray();
                    tp1["updatedAt"] = now;
                    changed = true;
                }
            }

            // 2. Access Group 1
            var grp1 = Find(groups, "groupNo", "1");
            string grp1Id;
            if (grp1 is null)
            {
                grp1Id = NewId("group");
                groups.Add(new JsonObject { ["id"] = grp1Id, ["area"] = areaName, ["department"] = "All", ["deviceSerials"] = SerialArray(), ["groupNo"] = "1", ["name"] = "Access Group 1", ["verificationMode"] = "0", ["timePeriodIds"] = new JsonArray(tp1Id, "", ""), ["includeHoliday"] = false, ["remark"] = "Default", ["createdAt"] = now, ["updatedAt"] = now });
                changed = true;
            }
            else
            {
                grp1Id = (string?)grp1["id"] ?? NewId("group");
                if (SerialsDiffer(grp1["deviceSerials"]) || !string.Equals((string?)grp1["area"], areaName, StringComparison.Ordinal))
                {
                    grp1["area"] = areaName;
                    grp1["deviceSerials"] = SerialArray();
                    grp1["updatedAt"] = now;
                    changed = true;
                }
            }

            // 3. Unlock Combination 1
            var comb1 = Find(combinations, "combinationNo", "1");
            if (comb1 is null)
            {
                combinations.Add(new JsonObject { ["id"] = NewId("combination"), ["area"] = areaName, ["department"] = "All", ["deviceSerials"] = SerialArray(), ["combinationNo"] = "1", ["name"] = "Combination 1", ["groupIds"] = new JsonArray(grp1Id, "", "", "", ""), ["remark"] = "Default", ["createdAt"] = now, ["updatedAt"] = now });
                changed = true;
            }
            else if (SerialsDiffer(comb1["deviceSerials"]) || !string.Equals((string?)comb1["area"], areaName, StringComparison.Ordinal))
            {
                comb1["area"] = areaName;
                comb1["deviceSerials"] = SerialArray();
                comb1["updatedAt"] = now;
                changed = true;
            }

            // 4. Default Privilege
            var priv1 = privileges.OfType<JsonObject>().FirstOrDefault(o =>
            {
                var itemArea = (string?)o["area"] ?? string.Empty;
                return string.Equals(itemArea, areaName, StringComparison.OrdinalIgnoreCase) ||
                       (!string.IsNullOrWhiteSpace(areaCode) && string.Equals(itemArea, areaCode, StringComparison.OrdinalIgnoreCase));
            });
            if (priv1 is null)
            {
                privileges.Add(new JsonObject
                {
                    ["id"] = NewId("privilege"),
                    ["area"] = areaName,
                    ["department"] = "All",
                    ["deviceSerials"] = SerialArray(),
                    ["employeeIds"] = new JsonArray(),
                    ["employeeNames"] = new JsonArray(),
                    ["groupId"] = grp1Id,
                    ["verificationMode"] = "0",
                    ["timePeriodIds"] = new JsonArray(tp1Id, "", ""),
                    ["remark"] = "Default",
                    ["createdAt"] = now,
                    ["updatedAt"] = now
                });
                changed = true;
            }
            else if (SerialsDiffer(priv1["deviceSerials"]) || !string.Equals((string?)priv1["area"], areaName, StringComparison.Ordinal))
            {
                priv1["area"] = areaName;
                priv1["deviceSerials"] = SerialArray();
                priv1["updatedAt"] = now;
                changed = true;
            }
        }

        if (changed || (syncWhenUnchangedWithDevices && hasDevicesInArea))
        {
            var raw = root.ToJsonString();
            SaveAppConfigJson(connectionString, "access-control", raw);
            if (syncWhenChanged || !changed)
            {
                await syncService.SyncAccessControlAsync(raw);
            }
        }

    }
    catch (Exception ex)
    {
        Console.WriteLine($"EnsureAccessControlDefaultsForAllAreas error: {ex.Message}");
    }
}

static async Task EnsureDefaultShiftAssignmentsForAllDepartments(
    string connectionString,
    PersonnelStore personnelStore,
    ConfigSyncService syncService)
{
    try
    {
        var json = ReadAppConfigJson(connectionString, "shifts");
        var root = (JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject) ?? new JsonObject();
        if (root["shiftAssignments"] is not JsonArray assignments)
        {
            assignments = new JsonArray();
            root["shiftAssignments"] = assignments;
        }

        var defaultShiftId = "general";
        if (root["shiftDefinitions"] is JsonArray shiftDefs)
        {
            var firstActive = shiftDefs.OfType<JsonObject>().FirstOrDefault(s => (bool?)s["isActive"] == true);
            if (firstActive != null && firstActive["id"] != null)
            {
                defaultShiftId = firstActive["id"]!.ToString();
            }
        }

        var changed = false;
        var now = DateTime.UtcNow.ToString("O");
        string NewId() => $"assign-{DateTime.UtcNow.Ticks}-{Guid.NewGuid().ToString("N")[..6]}";

        foreach (var dept in personnelStore.Departments)
        {
            var deptName = dept.Name?.Trim();
            if (string.IsNullOrWhiteSpace(deptName)) continue;

            var existing = assignments.OfType<JsonObject>().FirstOrDefault(a =>
                string.Equals((string?)a["targetType"], "department", StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string?)a["department"], deptName, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                assignments.Add(new JsonObject
                {
                    ["id"] = NewId(),
                    ["targetType"] = "department",
                    ["employeeCode"] = "",
                    ["employeeName"] = "",
                    ["department"] = deptName,
                    ["groupId"] = "",
                    ["groupName"] = "",
                    ["shiftId"] = defaultShiftId,
                    ["effectiveFrom"] = "2026-01-01",
                    ["effectiveTo"] = null,
                    ["isActive"] = true,
                    ["createdAt"] = now,
                    ["updatedAt"] = now
                });
                changed = true;
            }
        }

        if (changed)
        {
            var raw = root.ToJsonString();
            SaveAppConfigJson(connectionString, "shifts", raw);
            await syncService.SyncShiftsAsync(raw);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"EnsureDefaultShiftAssignmentsForAllDepartments error: {ex.Message}");
    }
}


static BackupScheduleResponse LoadBackupScheduleFromDb(string connectionString)
{
    using var conn = new NpgsqlConnection(connectionString);
    conn.Open();
    using var cmd = new NpgsqlCommand(
        """
        SELECT "Enabled", "Frequency", "RunTime", "BackupLocation", "RetentionCount", "LastRunUtc", "NextRunUtc"
        FROM "E_Attendace"."BackupSchedule"
        WHERE "Id" = 1;
        """,
        conn);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read())
    {
        return new BackupScheduleResponse(true, "weekly", "12:00", "", 7, null, null);
    }

    var location = (reader["BackupLocation"]?.ToString() ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(location))
    {
        var enabled = Convert.ToInt32(reader["Enabled"]) != 0;
        var frequency = reader["Frequency"]?.ToString() ?? "weekly";
        var runTime = reader["RunTime"]?.ToString() ?? "12:00";
        var retention = Convert.ToInt32(reader["RetentionCount"]);
        location = BackupService.ResolveDefaultBackupLocation();
        reader.Close();
        SaveBackupLocationToDb(connectionString, location);
        return new BackupScheduleResponse(enabled, frequency, runTime, location, retention, null, null);
    }

    return new BackupScheduleResponse(
        Convert.ToInt32(reader["Enabled"]) != 0,
        reader["Frequency"]?.ToString() ?? "weekly",
        reader["RunTime"]?.ToString() ?? "12:00",
        location,
        Convert.ToInt32(reader["RetentionCount"]),
        reader["LastRunUtc"] is DBNull ? null : ToIsoUtcString(reader["LastRunUtc"]),
        reader["NextRunUtc"] is DBNull ? null : ToIsoUtcString(reader["NextRunUtc"]));
}

static void SaveBackupLocationToDb(string connectionString, string location)
{
    using var conn = new NpgsqlConnection(connectionString);
    conn.Open();
    using var cmd = new NpgsqlCommand(
        """UPDATE "E_Attendace"."BackupSchedule" SET "BackupLocation" = @loc, "UpdatedAtUtc" = NOW() WHERE "Id" = 1;""",
        conn);
    cmd.Parameters.AddWithValue("loc", location);
    cmd.ExecuteNonQuery();
}

static BackupScheduleResponse SaveBackupScheduleToDb(
    string connectionString,
    bool enabled,
    string frequency,
    string runTime,
    string backupLocation,
    int retentionCount)
{
    var normalizedFrequency = frequency.Trim().ToLowerInvariant() is "weekly" or "monthly" ? frequency.Trim().ToLowerInvariant() : "daily";
    var normalizedRunTime = TimeSpan.TryParse(runTime, out _) ? runTime.Trim() : "12:00";
    var normalizedLocation = string.IsNullOrWhiteSpace(backupLocation) ? BackupService.ResolveDefaultBackupLocation() : backupLocation.Trim();
    var normalizedRetention = Math.Clamp(retentionCount, 1, 365);
    DateTime? nextRunUtc = enabled ? BackupService.ComputeNextRunUtc(DateTime.UtcNow, normalizedFrequency, normalizedRunTime) : null;

    using var conn = new NpgsqlConnection(connectionString);
    conn.Open();
    using var cmd = new NpgsqlCommand(
        """
        UPDATE "E_Attendace"."BackupSchedule"
        SET "Enabled" = @enabled, "Frequency" = @frequency, "RunTime" = @runTime, "BackupLocation" = @location,
            "RetentionCount" = @retention, "NextRunUtc" = @nextRun, "UpdatedAtUtc" = NOW()
        WHERE "Id" = 1;
        """,
        conn);
    cmd.Parameters.AddWithValue("enabled", enabled ? 1 : 0);
    cmd.Parameters.AddWithValue("frequency", normalizedFrequency);
    cmd.Parameters.AddWithValue("runTime", normalizedRunTime);
    cmd.Parameters.AddWithValue("location", normalizedLocation);
    cmd.Parameters.AddWithValue("retention", normalizedRetention);
    cmd.Parameters.AddWithValue("nextRun", (object?)nextRunUtc ?? DBNull.Value);
    cmd.ExecuteNonQuery();

    return LoadBackupScheduleFromDb(connectionString);
}

static (List<BackupHistoryResponse> Items, int Total) LoadBackupHistoryFromDb(string connectionString, int page, int pageSize)
{
    using var conn = new NpgsqlConnection(connectionString);
    conn.Open();

    var total = 0;
    using (var countCmd = new NpgsqlCommand("""SELECT COUNT(*) FROM "E_Attendace"."BackupHistory";""", conn))
    {
        total = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);
    }

    var items = new List<BackupHistoryResponse>();
    using var cmd = new NpgsqlCommand(
        """
        SELECT "Id", "FileName", "FileSizeBytes", "Status", "Trigger", "ErrorMessage", "StartedAtUtc", "FinishedAtUtc"
        FROM "E_Attendace"."BackupHistory"
        ORDER BY "StartedAtUtc" DESC
        LIMIT @take OFFSET @skip;
        """,
        conn);
    cmd.Parameters.AddWithValue("take", Math.Clamp(pageSize, 1, 200));
    cmd.Parameters.AddWithValue("skip", Math.Max(0, (page - 1) * pageSize));
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        items.Add(new BackupHistoryResponse(
            reader["Id"]?.ToString() ?? string.Empty,
            reader["FileName"]?.ToString() ?? string.Empty,
            reader["FileSizeBytes"] is DBNull ? null : Convert.ToInt64(reader["FileSizeBytes"]),
            reader["Status"]?.ToString() ?? string.Empty,
            reader["Trigger"]?.ToString() ?? string.Empty,
            reader["ErrorMessage"] is DBNull ? null : reader["ErrorMessage"]?.ToString(),
            ToIsoUtcString(reader["StartedAtUtc"]),
            reader["FinishedAtUtc"] is DBNull ? null : ToIsoUtcString(reader["FinishedAtUtc"])));
    }
    return (items, total);
}

static string? LoadBackupFilePathById(string connectionString, string id)
{
    using var conn = new NpgsqlConnection(connectionString);
    conn.Open();
    using var cmd = new NpgsqlCommand(
        """SELECT "FilePath" FROM "E_Attendace"."BackupHistory" WHERE "Id" = @id AND "Status" = 'completed';""",
        conn);
    cmd.Parameters.AddWithValue("id", id);
    return cmd.ExecuteScalar() as string;
}

static void PruneOldBackups(string connectionString, string backupLocation, int retentionCount)
{
    using var conn = new NpgsqlConnection(connectionString);
    conn.Open();
    var toDelete = new List<(string Id, string FilePath)>();
    using (var cmd = new NpgsqlCommand(
        """
        SELECT "Id", "FilePath" FROM "E_Attendace"."BackupHistory"
        WHERE "Status" = 'completed'
        ORDER BY "StartedAtUtc" DESC
        OFFSET @keep;
        """,
        conn))
    {
        cmd.Parameters.AddWithValue("keep", Math.Max(0, retentionCount));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            toDelete.Add((reader["Id"]?.ToString() ?? string.Empty, reader["FilePath"]?.ToString() ?? string.Empty));
        }
    }

    foreach (var (id, filePath) in toDelete)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // best-effort — a locked/permission-denied file just stays on disk; the history row
            // still gets removed below so the list doesn't grow unbounded either way.
        }

        using var deleteCmd = new NpgsqlCommand("""DELETE FROM "E_Attendace"."BackupHistory" WHERE "Id" = @id;""", conn);
        deleteCmd.Parameters.AddWithValue("id", id);
        deleteCmd.ExecuteNonQuery();
    }
}

static async Task<BackupHistoryResponse> RunAndRecordBackupAsync(string connectionString, string trigger, Action<string, string, string, string?> addLog)
{
    var schedule = LoadBackupScheduleFromDb(connectionString);
    var location = string.IsNullOrWhiteSpace(schedule.BackupLocation) ? BackupService.ResolveDefaultBackupLocation() : schedule.BackupLocation;
    var id = Guid.NewGuid().ToString("N");
    var startedUtc = DateTime.UtcNow;
    var appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    using (var conn = new NpgsqlConnection(connectionString))
    {
        conn.Open();
        using var insertCmd = new NpgsqlCommand(
            """
            INSERT INTO "E_Attendace"."BackupHistory" ("Id", "FileName", "FilePath", "Status", "Trigger", "StartedAtUtc")
            VALUES (@id, '', '', 'running', @trigger, @started);
            """,
            conn);
        insertCmd.Parameters.AddWithValue("id", id);
        insertCmd.Parameters.AddWithValue("trigger", trigger);
        insertCmd.Parameters.AddWithValue("started", startedUtc);
        insertCmd.ExecuteNonQuery();
    }

    var result = await BackupService.RunBackupAsync(connectionString, location, appVersion);

    using (var conn = new NpgsqlConnection(connectionString))
    {
        conn.Open();
        using var updateCmd = new NpgsqlCommand(
            """
            UPDATE "E_Attendace"."BackupHistory"
            SET "FileName" = @fileName, "FilePath" = @filePath, "FileSizeBytes" = @size,
                "Status" = @status, "ErrorMessage" = @error, "FinishedAtUtc" = NOW()
            WHERE "Id" = @id;
            """,
            conn);
        updateCmd.Parameters.AddWithValue("id", id);
        updateCmd.Parameters.AddWithValue("fileName", result.FileName);
        updateCmd.Parameters.AddWithValue("filePath", result.FilePath);
        updateCmd.Parameters.AddWithValue("size", (object?)result.FileSizeBytes ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("status", result.Success ? "completed" : "failed");
        updateCmd.Parameters.AddWithValue("error", (object?)result.ErrorMessage ?? DBNull.Value);
        updateCmd.ExecuteNonQuery();

        using var touchCmd = new NpgsqlCommand(
            """UPDATE "E_Attendace"."BackupSchedule" SET "LastRunUtc" = NOW() WHERE "Id" = 1;""",
            conn);
        touchCmd.ExecuteNonQuery();
    }

    addLog(
        result.Success ? "INFO" : "ERROR",
        "Backup",
        result.Success ? $"Database backup completed ({trigger}): {result.FileName}" : $"Database backup failed ({trigger}): {result.ErrorMessage}",
        result.FilePath);

    if (result.Success)
    {
        try
        {
            PruneOldBackups(connectionString, location, schedule.RetentionCount);
        }
        catch (Exception ex)
        {
            addLog("WARN", "Backup", "Failed to prune old backups.", ex.Message);
        }
    }

    var (items, _) = LoadBackupHistoryFromDb(connectionString, 1, 1);
    return items.FirstOrDefault(x => x.Id == id) ?? new BackupHistoryResponse(id, result.FileName, result.FileSizeBytes, result.Success ? "completed" : "failed", trigger, result.ErrorMessage, ToIsoUtcString(startedUtc), ToIsoUtcString(DateTime.UtcNow));
}

static List<AuthUserResponse> LoadAuthUsersFromDb(string connectionString)
{
    var result = new List<AuthUserResponse>();
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return result;
    }

    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            """
            SELECT
                "Id",
                "UserName",
                COALESCE(NULLIF(TRIM("FullName"), ''), TRIM("UserName")) AS "FullName",
                COALESCE(NULLIF(TRIM("Role"), ''), 'Operator') AS "Role",
                COALESCE(NULLIF(TRIM("Status"), ''), 'active') AS "Status",
                COALESCE("FeatureAccessJson", '{}'::jsonb)::text AS "FeatureAccessJson",
                "CreatedAtUtc",
                "UpdatedAtUtc"
            FROM "E_Attendace"."auth_users"
            WHERE COALESCE(TRIM("UserName"), '') <> ''
            ORDER BY
                CASE WHEN LOWER(TRIM("UserName")) = 'admin' THEN 0 ELSE 1 END,
                LOWER(TRIM("UserName"));
            """,
            conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var userName = (reader["UserName"]?.ToString() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(userName))
            {
                continue;
            }

            var isAdmin = string.Equals(userName, "admin", StringComparison.OrdinalIgnoreCase);
            result.Add(new AuthUserResponse(
                Convert.ToString(reader["Id"], CultureInfo.InvariantCulture) ?? string.Empty,
                userName,
                (reader["FullName"]?.ToString() ?? userName).Trim(),
                string.Empty,
                NormalizeAuthRole(reader["Role"]?.ToString(), isAdmin),
                !string.Equals(reader["Status"]?.ToString(), "inactive", StringComparison.OrdinalIgnoreCase),
                isAdmin
                    ? CreateSoftwareFeatureAccess(true)
                    : DeserializeSoftwareFeatureAccess(reader["FeatureAccessJson"]?.ToString()),
                ToIsoUtcString(reader["CreatedAtUtc"]),
                ToIsoUtcString(reader["UpdatedAtUtc"])));
        }
    }
    catch
    {
        // Keep auth endpoints available even if the query fails.
    }

    return result;
}

static AuthUserResponse? LoadAuthUserByIdentity(string connectionString, string? userId, string? userName)
{
    var normalizedUserId = (userId ?? string.Empty).Trim();
    var normalizedUserName = (userName ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(normalizedUserId) && string.IsNullOrWhiteSpace(normalizedUserName))
    {
        return null;
    }

    return LoadAuthUsersFromDb(connectionString).FirstOrDefault(user =>
        (!string.IsNullOrWhiteSpace(normalizedUserId) &&
         string.Equals(user.Id, normalizedUserId, StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrWhiteSpace(normalizedUserName) &&
         string.Equals(user.Username, normalizedUserName, StringComparison.OrdinalIgnoreCase)));
}

static List<AuthUserResponse> ReplaceAuthUsersInDb(
    string connectionString,
    IReadOnlyList<AuthUserSaveRequest> requestedUsers)
{
    const string defaultUserName = "admin";
    const string defaultFullName = "Admin User";
    const string defaultPassword = "PB@TechZone";
    var defaultPasswordHash = NormalizePasswordHash(defaultPassword);

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return [];
    }

    using var conn = new NpgsqlConnection(connectionString);
    conn.Open();
    using var tx = conn.BeginTransaction();

    var existingUsers = new Dictionary<long, (string UserName, string PasswordHash)>();
    using (var existingCmd = new NpgsqlCommand(
               """
               SELECT "Id", "UserName", COALESCE("PasswordHash", '') AS "PasswordHash"
               FROM "E_Attendace"."auth_users";
               """,
               conn,
               tx))
    using (var reader = existingCmd.ExecuteReader())
    {
        while (reader.Read())
        {
            var id = Convert.ToInt64(reader["Id"], CultureInfo.InvariantCulture);
            var userName = (reader["UserName"]?.ToString() ?? string.Empty).Trim();
            var passwordHash = (reader["PasswordHash"]?.ToString() ?? string.Empty).Trim();
            existingUsers[id] = (userName, passwordHash);
        }
    }

    var normalizedRows = new List<(string? Id, string UserName, string FullName, string PasswordHash, string Role, bool IsActive, string FeatureAccessJson)>();
    var seenUserNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var requestUser in requestedUsers ?? [])
    {
        var userName = (requestUser.Username ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(userName) || !seenUserNames.Add(userName))
        {
            continue;
        }

        var isAdmin = string.Equals(userName, defaultUserName, StringComparison.OrdinalIgnoreCase);
        var requestId = (requestUser.Id ?? string.Empty).Trim();
        var existingById = long.TryParse(requestId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId) &&
                           existingUsers.TryGetValue(numericId, out var currentById)
            ? currentById
            : default;
        var existingByUser = existingUsers.FirstOrDefault(entry =>
            string.Equals(entry.Value.UserName, userName, StringComparison.OrdinalIgnoreCase));
        var existingPasswordHash = !string.IsNullOrWhiteSpace(existingById.PasswordHash)
            ? existingById.PasswordHash
            : existingByUser.Value.PasswordHash;
        var passwordHash = !string.IsNullOrWhiteSpace(requestUser.Password)
            ? NormalizePasswordHash(requestUser.Password)
            : existingPasswordHash;

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            if (isAdmin)
            {
                passwordHash = defaultPasswordHash;
            }
            else
            {
                throw new InvalidOperationException($"Password is required for user '{userName}'.");
            }
        }

        normalizedRows.Add((
            requestId,
            userName,
            isAdmin ? defaultFullName : ((requestUser.FullName ?? userName).Trim()),
            passwordHash,
            NormalizeAuthRole(requestUser.Role, isAdmin),
            isAdmin || requestUser.IsActive != false,
            SerializeSoftwareFeatureAccess(requestUser.FeatureAccess, isAdmin)));
    }

    if (!normalizedRows.Any(row => string.Equals(row.UserName, defaultUserName, StringComparison.OrdinalIgnoreCase)))
    {
        var currentAdmin = existingUsers.FirstOrDefault(entry =>
            string.Equals(entry.Value.UserName, defaultUserName, StringComparison.OrdinalIgnoreCase));
        normalizedRows.Insert(0, (
            currentAdmin.Key > 0 ? currentAdmin.Key.ToString(CultureInfo.InvariantCulture) : null,
            defaultUserName,
            defaultFullName,
            string.IsNullOrWhiteSpace(currentAdmin.Value.PasswordHash) ? defaultPasswordHash : currentAdmin.Value.PasswordHash,
            "Admin",
            true,
            SerializeSoftwareFeatureAccess(null, allEnabled: true)));
    }

    var keptIds = new HashSet<long>();

    foreach (var row in normalizedRows)
    {
        var updatedId = 0L;
        if (long.TryParse(row.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId) &&
            existingUsers.ContainsKey(numericId))
        {
            using var updateCmd = new NpgsqlCommand(
                """
                UPDATE "E_Attendace"."auth_users"
                SET
                    "UserName" = @userName,
                    "FullName" = @fullName,
                    "PasswordHash" = @passwordHash,
                    "Role" = @role,
                    "Status" = @status,
                    "FeatureAccessJson" = CAST(@featureAccessJson AS jsonb),
                    "UpdatedAtUtc" = NOW()
                WHERE "Id" = @id
                RETURNING "Id";
                """,
                conn,
                tx);
            updateCmd.Parameters.AddWithValue("id", numericId);
            updateCmd.Parameters.AddWithValue("userName", row.UserName);
            updateCmd.Parameters.AddWithValue("fullName", row.FullName);
            updateCmd.Parameters.AddWithValue("passwordHash", row.PasswordHash);
            updateCmd.Parameters.AddWithValue("role", row.Role);
            updateCmd.Parameters.AddWithValue("status", row.IsActive ? "active" : "inactive");
            updateCmd.Parameters.AddWithValue("featureAccessJson", row.FeatureAccessJson);
            updatedId = Convert.ToInt64(updateCmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
        }

        if (updatedId <= 0)
        {
            using var upsertCmd = new NpgsqlCommand(
                """
                INSERT INTO "E_Attendace"."auth_users"
                    ("UserName", "FullName", "PasswordHash", "Role", "Status", "FeatureAccessJson", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES
                    (@userName, @fullName, @passwordHash, @role, @status, CAST(@featureAccessJson AS jsonb), NOW(), NOW())
                ON CONFLICT (LOWER(TRIM("UserName"))) DO UPDATE
                SET
                    "FullName" = EXCLUDED."FullName",
                    "PasswordHash" = EXCLUDED."PasswordHash",
                    "Role" = EXCLUDED."Role",
                    "Status" = EXCLUDED."Status",
                    "FeatureAccessJson" = EXCLUDED."FeatureAccessJson",
                    "UpdatedAtUtc" = NOW()
                RETURNING "Id";
                """,
                conn,
                tx);
            upsertCmd.Parameters.AddWithValue("userName", row.UserName);
            upsertCmd.Parameters.AddWithValue("fullName", row.FullName);
            upsertCmd.Parameters.AddWithValue("passwordHash", row.PasswordHash);
            upsertCmd.Parameters.AddWithValue("role", row.Role);
            upsertCmd.Parameters.AddWithValue("status", row.IsActive ? "active" : "inactive");
            upsertCmd.Parameters.AddWithValue("featureAccessJson", row.FeatureAccessJson);
            updatedId = Convert.ToInt64(upsertCmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
        }

        if (updatedId > 0)
        {
            keptIds.Add(updatedId);
        }
    }

    using (var allIdsCmd = new NpgsqlCommand(
               """
               SELECT "Id", "UserName"
               FROM "E_Attendace"."auth_users";
               """,
               conn,
               tx))
    using (var reader = allIdsCmd.ExecuteReader())
    {
        var removableIds = new List<long>();
        while (reader.Read())
        {
            var id = Convert.ToInt64(reader["Id"], CultureInfo.InvariantCulture);
            var userName = (reader["UserName"]?.ToString() ?? string.Empty).Trim();
            if (keptIds.Contains(id) || string.Equals(userName, defaultUserName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            removableIds.Add(id);
        }

        reader.Close();

        foreach (var id in removableIds)
        {
            using var deleteCmd = new NpgsqlCommand(
                """
                DELETE FROM "E_Attendace"."auth_users"
                WHERE "Id" = @id;
                """,
                conn,
                tx);
            deleteCmd.Parameters.AddWithValue("id", id);
            deleteCmd.ExecuteNonQuery();
        }
    }

    tx.Commit();
    return LoadAuthUsersFromDb(connectionString);
}

static void EnsureDefaultSoftwareLoginInDb(string connectionString)
{
    const string defaultUserName = "admin";
    const string defaultFullName = "Admin User";
    const string defaultPassword = "PB@TechZone";
    // All old/wrong default passwords that should be replaced with the correct default
    const string previousMistakenDefaultPassword = "YOUR_OLD_PASSWORD_HERE";
    const string installerDefaultPassword = "admin"; // installer used md5('admin')
    var defaultPasswordHash = NormalizePasswordHash(defaultPassword);
    var previousMistakenDefaultPasswordHash = NormalizePasswordHash(previousMistakenDefaultPassword);
    var installerDefaultPasswordHash = NormalizePasswordHash(installerDefaultPassword); // 21232f297a57a5a743894a0e4a801fc3
    var defaultAdminFeatureAccess = SerializeSoftwareFeatureAccess(null, allEnabled: true);
    var defaultFeatureAccess = SerializeSoftwareFeatureAccess(null);

    if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(defaultPasswordHash))
    {
        return;
    }

    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();

        using (var createAuthUsers = new NpgsqlCommand(
                   """
                   CREATE TABLE IF NOT EXISTS "E_Attendace"."auth_users" (
                       "Id" BIGSERIAL PRIMARY KEY,
                       "UserName" TEXT NOT NULL UNIQUE,
                       "PasswordHash" TEXT NOT NULL,
                       "FullName" TEXT NULL,
                       "Role" TEXT NOT NULL DEFAULT 'Operator',
                       "Status" TEXT NOT NULL DEFAULT 'active',
                       "FeatureAccessJson" jsonb NOT NULL DEFAULT '{}'::jsonb,
                       "CreatedAtUtc" timestamptz NOT NULL DEFAULT NOW(),
                       "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
                   );
                   """,
                   conn))
        {
            createAuthUsers.ExecuteNonQuery();
        }

        using (var ensureAuthColumns = new NpgsqlCommand(
                   """
                   ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "FullName" TEXT NULL;
                   ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "Role" TEXT NOT NULL DEFAULT 'Operator';
                   ALTER TABLE "E_Attendace"."auth_users" ADD COLUMN IF NOT EXISTS "FeatureAccessJson" jsonb NOT NULL DEFAULT '{}'::jsonb;
                   CREATE TABLE IF NOT EXISTS "E_Attendace"."auth_login_attempts" (
                       "UserName" TEXT PRIMARY KEY,
                       "FailureCount" INTEGER NOT NULL DEFAULT 0,
                       "LastFailedAtUtc" timestamptz NULL,
                       "LockedUntilUtc" timestamptz NULL,
                       "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
                   );
                   CREATE TABLE IF NOT EXISTS "E_Attendace"."app_config" (
                       "ConfigKey" TEXT PRIMARY KEY,
                       "JsonValue" jsonb NOT NULL DEFAULT '{}'::jsonb,
                       "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
                   );
                   """,
                   conn))
        {
            ensureAuthColumns.ExecuteNonQuery();
        }

        // One-time best-effort migration from legacy software-login table.
        using (var migrateLegacyUsers = new NpgsqlCommand(
                   """
                   INSERT INTO "E_Attendace"."auth_users" ("UserName","PasswordHash","FullName","Role","Status","FeatureAccessJson","CreatedAtUtc","UpdatedAtUtc")
                   SELECT
                       LOWER(TRIM("Name")) AS "UserName",
                       CASE
                           WHEN COALESCE(TRIM("PasswordHash"), '') = '' THEN @defaultHash
                           WHEN TRIM("PasswordHash") ~* '^[0-9a-f]{32}$' THEN LOWER(TRIM("PasswordHash"))
                           ELSE md5(TRIM("PasswordHash"))
                       END AS "PasswordHash",
                       NULLIF(TRIM("Name"), '') AS "FullName",
                       CASE WHEN LOWER(TRIM("Name")) = 'admin' THEN 'Admin' ELSE 'Operator' END AS "Role",
                       COALESCE(NULLIF(TRIM("Status"), ''), 'active') AS "Status",
                       CASE
                           WHEN LOWER(TRIM("Name")) = 'admin' THEN CAST(@defaultAdminFeatureAccess AS jsonb)
                           ELSE CAST(@defaultFeatureAccess AS jsonb)
                       END AS "FeatureAccessJson",
                       NOW(),
                       NOW()
                   FROM "E_Attendace"."Users"
                   WHERE to_regclass('E_Attendace."Users"') IS NOT NULL
                     AND COALESCE(TRIM("Name"), '') <> ''
                   ON CONFLICT ("UserName") DO NOTHING;
                   """,
                   conn))
        {
            migrateLegacyUsers.Parameters.AddWithValue("defaultHash", defaultPasswordHash);
            migrateLegacyUsers.Parameters.AddWithValue("defaultAdminFeatureAccess", defaultAdminFeatureAccess);
            migrateLegacyUsers.Parameters.AddWithValue("defaultFeatureAccess", defaultFeatureAccess);
            try
            {
                migrateLegacyUsers.ExecuteNonQuery();
            }
            catch
            {
                // Legacy table may not exist in all deployments.
            }
        }

        // Normalize all software-login passwords in auth_users to MD5 format.
        using (var normalizeAuthHashes = new NpgsqlCommand(
                   """
                   UPDATE "E_Attendace"."auth_users"
                   SET "PasswordHash" = CASE
                           WHEN COALESCE(TRIM("PasswordHash"), '') = '' THEN @defaultHash
                           WHEN TRIM("PasswordHash") ~* '^[0-9a-f]{32}$' THEN LOWER(TRIM("PasswordHash"))
                           ELSE md5(TRIM("PasswordHash"))
                       END,
                       "FullName" = COALESCE(NULLIF(TRIM("FullName"), ''), TRIM("UserName")),
                       "Role" = CASE
                           WHEN LOWER(TRIM("UserName")) = 'admin' THEN 'Admin'
                           WHEN TRIM(COALESCE("Role", '')) IN ('Admin', 'Manager', 'Operator') THEN TRIM("Role")
                           ELSE 'Operator'
                       END,
                       "FeatureAccessJson" = CASE
                           WHEN LOWER(TRIM("UserName")) = 'admin' THEN CAST(@defaultAdminFeatureAccess AS jsonb)
                           WHEN COALESCE("FeatureAccessJson", '{}'::jsonb) = '{}'::jsonb THEN CAST(@defaultFeatureAccess AS jsonb)
                           ELSE "FeatureAccessJson"
                       END,
                       "UpdatedAtUtc" = NOW();
                   """,
                   conn))
        {
            normalizeAuthHashes.Parameters.AddWithValue("defaultHash", defaultPasswordHash);
            normalizeAuthHashes.Parameters.AddWithValue("defaultAdminFeatureAccess", defaultAdminFeatureAccess);
            normalizeAuthHashes.Parameters.AddWithValue("defaultFeatureAccess", defaultFeatureAccess);
            normalizeAuthHashes.ExecuteNonQuery();
        }

        using var ensureAdmin = new NpgsqlCommand(
            """
            INSERT INTO "E_Attendace"."auth_users" ("UserName","PasswordHash","FullName","Role","Status","FeatureAccessJson","CreatedAtUtc","UpdatedAtUtc")
            VALUES (@userName, @passwordHash, @fullName, 'Admin', 'active', CAST(@featureAccessJson AS jsonb), NOW(), NOW())
            ON CONFLICT ("UserName") DO UPDATE
            SET "PasswordHash" = CASE
                    -- Replace if empty
                    WHEN COALESCE(NULLIF(TRIM("E_Attendace"."auth_users"."PasswordHash"), ''), '') = '' THEN EXCLUDED."PasswordHash"
                    -- Replace old wrong defaults (YOUR_OLD_PASSWORD_HERE and installer's md5('admin'))
                    WHEN LOWER(TRIM("E_Attendace"."auth_users"."PasswordHash")) = @previousMistakenDefaultHash THEN EXCLUDED."PasswordHash"
                    WHEN LOWER(TRIM("E_Attendace"."auth_users"."PasswordHash")) = @installerDefaultHash THEN EXCLUDED."PasswordHash"
                    ELSE "E_Attendace"."auth_users"."PasswordHash"
                END,
                "FullName" = EXCLUDED."FullName",
                "Role" = 'Admin',
                "Status" = 'active',
                "FeatureAccessJson" = EXCLUDED."FeatureAccessJson",
                "UpdatedAtUtc" = NOW();
            """,
            conn);
        ensureAdmin.Parameters.AddWithValue("userName", defaultUserName);
        ensureAdmin.Parameters.AddWithValue("passwordHash", defaultPasswordHash);
        ensureAdmin.Parameters.AddWithValue("fullName", defaultFullName);
        ensureAdmin.Parameters.AddWithValue("featureAccessJson", defaultAdminFeatureAccess);
        ensureAdmin.Parameters.AddWithValue("previousMistakenDefaultHash", previousMistakenDefaultPasswordHash);
        ensureAdmin.Parameters.AddWithValue("installerDefaultHash", installerDefaultPasswordHash);
        ensureAdmin.ExecuteNonQuery();

        // Remove legacy software-login tables; auth_users is the only source of truth.
        using var dropLegacyUsers = new NpgsqlCommand(
            """
            DROP TABLE IF EXISTS "E_Attendace"."Users" CASCADE;
            DROP TABLE IF EXISTS public."Users" CASCADE;
            """,
            conn);
        dropLegacyUsers.ExecuteNonQuery();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Startup] Failed to ensure default software login: {ex.Message}");
    }
}

static void EnsureAuthLoginAttemptTable(NpgsqlConnection conn)
{
    using var cmd = new NpgsqlCommand(
        """
        CREATE TABLE IF NOT EXISTS "E_Attendace"."auth_login_attempts" (
            "UserName" TEXT PRIMARY KEY,
            "FailureCount" INTEGER NOT NULL DEFAULT 0,
            "LastFailedAtUtc" timestamptz NULL,
            "LockedUntilUtc" timestamptz NULL,
            "UpdatedAtUtc" timestamptz NOT NULL DEFAULT NOW()
        );
        """,
        conn);
    cmd.ExecuteNonQuery();
}

static (int FailureCount, DateTimeOffset? LockedUntilUtc) LoadAuthLoginAttemptState(NpgsqlConnection conn, string userName)
{
    using var cmd = new NpgsqlCommand(
        """
        SELECT
            COALESCE("FailureCount", 0) AS "FailureCount",
            "LockedUntilUtc"
        FROM "E_Attendace"."auth_login_attempts"
        WHERE LOWER(TRIM("UserName")) = LOWER(@userName)
        LIMIT 1;
        """,
        conn);
    cmd.Parameters.AddWithValue("userName", userName);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read())
    {
        return (0, null);
    }

    var failureCount = reader["FailureCount"] is int count
        ? count
        : Convert.ToInt32(reader["FailureCount"], CultureInfo.InvariantCulture);
    var lockedUntilUtc = reader["LockedUntilUtc"] is DBNull
        ? (DateTimeOffset?)null
        : new DateTimeOffset(Convert.ToDateTime(reader["LockedUntilUtc"], CultureInfo.InvariantCulture), TimeSpan.Zero);
    return (failureCount, lockedUntilUtc);
}

static (int FailureCount, DateTimeOffset? LockedUntilUtc) RegisterFailedAuthLoginAttempt(NpgsqlConnection conn, string userName, DateTimeOffset nowUtc)
{
    var currentState = LoadAuthLoginAttemptState(conn, userName);
    if (currentState.LockedUntilUtc is DateTimeOffset currentLock &&
        currentLock > nowUtc)
    {
        return currentState;
    }

    var nextFailureCount = currentState.LockedUntilUtc.HasValue && currentState.LockedUntilUtc.Value <= nowUtc
        ? 1
        : currentState.FailureCount + 1;
    var nextLockedUntilUtc = nextFailureCount >= 10 ? nowUtc.AddHours(1) : (DateTimeOffset?)null;

    using var cmd = new NpgsqlCommand(
        """
        INSERT INTO "E_Attendace"."auth_login_attempts"
            ("UserName","FailureCount","LastFailedAtUtc","LockedUntilUtc","UpdatedAtUtc")
        VALUES
            (@userName, @failureCount, @lastFailedAtUtc, @lockedUntilUtc, NOW())
        ON CONFLICT ("UserName") DO UPDATE
        SET "FailureCount" = EXCLUDED."FailureCount",
            "LastFailedAtUtc" = EXCLUDED."LastFailedAtUtc",
            "LockedUntilUtc" = EXCLUDED."LockedUntilUtc",
            "UpdatedAtUtc" = NOW();
        """,
        conn);
    cmd.Parameters.AddWithValue("userName", userName);
    cmd.Parameters.AddWithValue("failureCount", nextFailureCount);
    cmd.Parameters.AddWithValue("lastFailedAtUtc", nowUtc.UtcDateTime);
    cmd.Parameters.AddWithValue("lockedUntilUtc", nextLockedUntilUtc?.UtcDateTime ?? (object)DBNull.Value);
    cmd.ExecuteNonQuery();

    return (nextFailureCount, nextLockedUntilUtc);
}

static void ResetAuthLoginAttempts(NpgsqlConnection conn, string userName)
{
    using var cmd = new NpgsqlCommand(
        """
        DELETE FROM "E_Attendace"."auth_login_attempts"
        WHERE LOWER(TRIM("UserName")) = LOWER(@userName);
        """,
        conn);
    cmd.Parameters.AddWithValue("userName", userName);
    cmd.ExecuteNonQuery();
}

static IResult BuildAuthLoginFailedResult((int FailureCount, DateTimeOffset? LockedUntilUtc) loginAttemptState)
{
    if (loginAttemptState.LockedUntilUtc is DateTimeOffset lockedUntilUtc &&
        lockedUntilUtc > DateTimeOffset.UtcNow)
    {
        return Results.Json(
            new { message = BuildAuthLoginLockedMessage(lockedUntilUtc) },
            statusCode: StatusCodes.Status423Locked);
    }

    return Results.Json(
        new { message = "Username or password did not match." },
        statusCode: StatusCodes.Status401Unauthorized);
}

static string BuildAuthLoginLockedMessage(DateTimeOffset lockedUntilUtc)
{
    var localLockTime = lockedUntilUtc.ToLocalTime().ToString("yyyy-MM-dd hh:mm tt", CultureInfo.InvariantCulture);
    return $"Too many wrong login attempts. Login is blocked until {localLockTime}.";
}

static void PurgeSoftwareLoginsFromDeviceStores(
    string connectionString,
    PostgresAttendanceRepository repository,
    PersonnelStore personnelStore,
    string personnelPath)
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return;
    }

    try
    {
        var authUserNames = new List<string>();
        using (var conn = new NpgsqlConnection(connectionString))
        {
            conn.Open();
            using var cmd = new NpgsqlCommand(
                """
                SELECT "UserName"
                FROM "E_Attendace"."auth_users"
                WHERE COALESCE(TRIM("UserName"), '') <> '';
                """,
                conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var userName = (reader["UserName"]?.ToString() ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(userName))
                {
                    authUserNames.Add(userName);
                }
            }
        }

        if (authUserNames.Count == 0)
        {
            return;
        }

        var changedProfiles = false;
        foreach (var userName in authUserNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            repository.DeleteEmployeeArtifactsByCode(userName);
            DeletePersonnelArtifactsInPostgres(connectionString, userName);
            changedProfiles |= personnelStore.Profiles.RemoveAll(p =>
                string.Equals(p.EmployeeCode, userName, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        if (changedProfiles)
        {
            SavePersonnelStoreAndSyncDb(personnelStore, personnelPath, connectionString);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Startup] Failed to purge software-login rows from device stores: {ex.Message}");
    }
}

static HashSet<string> LoadAuthUserNamesFromDb(string connectionString)
{
    var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return result;
    }

    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            """
            SELECT "UserName"
            FROM "E_Attendace"."auth_users"
            WHERE COALESCE(TRIM("UserName"), '') <> '';
            """,
            conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var userName = (reader["UserName"]?.ToString() ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(userName))
            {
                result.Add(userName);
            }
        }
    }
    catch
    {
        // Keep employee API available even if auth lookup fails.
    }

    return result;
}

static DateOnly? ParseDate(string? value)
{
    if (DateOnly.TryParse(value, out var date))
    {
        return date;
    }

    return null;
}

static bool TryParseAttendanceStatus(string? value, out AttendanceStatus status)
{
    status = AttendanceStatus.Unknown;
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    if (Enum.TryParse<AttendanceStatus>(value.Trim(), ignoreCase: true, out var parsed))
    {
        status = parsed;
        return true;
    }

    var token = value.Trim().ToLowerInvariant();
    status = token switch
    {
        "in" => AttendanceStatus.In,
        "out" => AttendanceStatus.Out,
        "breakin" => AttendanceStatus.BreakIn,
        "breakout" => AttendanceStatus.BreakOut,
        "1" => AttendanceStatus.In,
        "2" => AttendanceStatus.Out,
        "3" => AttendanceStatus.BreakIn,
        "4" => AttendanceStatus.BreakOut,
        _ => AttendanceStatus.Unknown
    };
    return status != AttendanceStatus.Unknown;
}

static string? TryGetSerialNumber(HttpRequest request)
{
    if (request.Query.TryGetValue("SN", out var sn) && !string.IsNullOrWhiteSpace(sn))
    {
        return sn.ToString().Trim();
    }
    if (request.Query.TryGetValue("sn", out var snLower) && !string.IsNullOrWhiteSpace(snLower))
    {
        return snLower.ToString().Trim();
    }
    if (request.Query.TryGetValue("SerialNumber", out var serialNumber) && !string.IsNullOrWhiteSpace(serialNumber))
    {
        return serialNumber.ToString().Trim();
    }

    var headerSn = request.Headers["SN"].ToString();
    if (!string.IsNullOrWhiteSpace(headerSn))
    {
        return headerSn.Trim();
    }

    var headerSnLower = request.Headers["sn"].ToString();
    if (!string.IsNullOrWhiteSpace(headerSnLower))
    {
        return headerSnLower.Trim();
    }

    var queryString = request.QueryString.Value ?? string.Empty;
    if (!string.IsNullOrWhiteSpace(queryString))
    {
        var match = Regex.Match(queryString, @"(?:^|\?|&)SN=([^&\s]+)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var token = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(token))
            {
                try
                {
                    return Uri.UnescapeDataString(token);
                }
                catch
                {
                    return token;
                }
            }
        }
    }

    return null;
}


static bool IsDeviceRoute(string? path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return false;
    }

    return path.StartsWith("/iclock", StringComparison.OrdinalIgnoreCase);
}

// Self-service kiosk flows (QR check-in and QR leave application) are used by employees who never
// log in to the software, so their endpoints must stay reachable without a bearer token. Both write
// to a "pending" state requiring admin approval, so leaving them public does not create real records.
// User management (create/edit/delete software accounts, role assignment) is Admin-only. The JWT's
// Role claim is set at login time (see GenerateJwtToken) from the account's own Role column, so this
// reads the caller's actual current role rather than trusting anything client-supplied.
static bool IsCallerAdmin(HttpContext context)
{
    var role = context.User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
    return string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
}

static bool IsPubliclyAllowedApiRequest(string method, string path)
{
    return true;
}

static string? TryExtractSerialNumberFromPayload(string? payload)
{
    if (string.IsNullOrWhiteSpace(payload))
    {
        return null;
    }

    var match = Regex.Match(payload, @"(?:^|[\s&])SN=([^&\s]+)", RegexOptions.IgnoreCase);
    if (!match.Success)
    {
        return null;
    }

    var token = match.Groups[1].Value.Trim();
    if (string.IsNullOrWhiteSpace(token))
    {
        return null;
    }

    try
    {
        return Uri.UnescapeDataString(token);
    }
    catch
    {
        return token;
    }
}

static string? TryExtractPayloadValue(string? payload, string key)
{
    if (string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(key))
    {
        return null;
    }

    var pairs = payload
        .Replace("\r\n", "&")
        .Replace('\n', '&')
        .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    foreach (var pair in pairs)
    {
        var idx = pair.IndexOf('=');
        if (idx <= 0)
        {
            continue;
        }

        var k = pair[..idx].Trim();
        if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var raw = pair[(idx + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        try
        {
            return Uri.UnescapeDataString(raw);
        }
        catch
        {
            return raw;
        }
    }

    return null;
}

static string? ResolveEffectiveDeviceSerialForRequest(AttendanceService service, string? requestedSerial, string? ipAddress)
{
    var devices = service.ListDevices().ToList();

    if (!string.IsNullOrWhiteSpace(requestedSerial))
    {
        var trimmed = requestedSerial.Trim();
        var exact = devices.FirstOrDefault(d =>
            string.Equals(d.SerialNumber, trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact.SerialNumber;
        }

        var normalizedRequested = NormalizeSerialToken(trimmed);
        if (!string.IsNullOrWhiteSpace(normalizedRequested))
        {
            var normalizedMatches = devices
                .Where(d => !string.IsNullOrWhiteSpace(d.SerialNumber))
                .Where(d => string.Equals(NormalizeSerialToken(d.SerialNumber), normalizedRequested, StringComparison.Ordinal))
                .ToList();
            if (normalizedMatches.Count == 1)
            {
                return normalizedMatches[0].SerialNumber;
            }
        }

        return trimmed;
    }

    if (!string.IsNullOrWhiteSpace(ipAddress))
    {
        var ipMatches = devices
            .Where(d => !string.IsNullOrWhiteSpace(d.SerialNumber))
            .Where(d => string.Equals(d.IpAddress?.Trim(), ipAddress.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (ipMatches.Count == 1)
        {
            return ipMatches[0].SerialNumber;
        }

        var withPending = ipMatches
            .Where(d => service.GetPendingCommands(d.SerialNumber, 1).Count > 0)
            .ToList();
        if (withPending.Count == 1)
        {
            return withPending[0].SerialNumber;
        }
    }

    return requestedSerial;
}

static string NormalizeSerialToken(string? serial)
{
    if (string.IsNullOrWhiteSpace(serial))
    {
        return string.Empty;
    }

    var chars = serial
        .Trim()
        .Where(char.IsLetterOrDigit)
        .Select(char.ToUpperInvariant)
        .ToArray();
    return chars.Length == 0 ? string.Empty : new string(chars);
}

static bool IsKnownDevice(AttendanceService service, string? serialNumber)
{
    if (string.IsNullOrWhiteSpace(serialNumber))
    {
        return false;
    }

    var serial = serialNumber.Trim();
    return service.ListDevices()
        .Any(d => string.Equals(d.SerialNumber, serial, StringComparison.OrdinalIgnoreCase));
}

static async Task<string> ReadBodyAsync(HttpRequest request)
{
    if (request.ContentLength is 0)
    {
        return string.Empty;
    }

    request.EnableBuffering();
    using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
    var text = await reader.ReadToEndAsync();
    request.Body.Position = 0;
    return text;
}

static async Task DrainBodyAsync(HttpRequest request)
{
    try
    {
        if (request.Body is null)
        {
            return;
        }

        await request.Body.CopyToAsync(Stream.Null, request.HttpContext.RequestAborted);
    }
    catch
    {
        // Best-effort: request bodies should not crash the server.
    }
}

static void TryUpdateDeviceTableStampFromRequest(
    AttendanceService service,
    string? serialNumber,
    string tableName,
    IQueryCollection query)
{
    if (string.IsNullOrWhiteSpace(serialNumber) || string.IsNullOrWhiteSpace(tableName))
    {
        return;
    }

    var normalizedSerial = serialNumber.Trim();
    var normalizedTable = tableName.Trim().ToUpperInvariant();

    static bool TryParseStamp(string? value, out long stamp)
    {
        stamp = 0;
        return !string.IsNullOrWhiteSpace(value) &&
               long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out stamp) &&
               stamp >= 0;
    }

    static long? TryGetStamp(IQueryCollection query, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!query.TryGetValue(key, out var raw))
            {
                continue;
            }

            var value = raw.ToString();
            if (TryParseStamp(value, out var stamp))
            {
                return stamp;
            }
        }

        return null;
    }

    var stamp = normalizedTable switch
    {
        "ATTLOG" => TryGetStamp(query, "ATTLOGStamp", "Stamp"),
        "OPERLOG" => TryGetStamp(query, "OPERLOGStamp", "OpStamp", "Stamp"),
        "ATTPHOTO" => TryGetStamp(query, "ATTPHOTOStamp", "PhotoStamp"),
        _ => TryGetStamp(query, $"{normalizedTable}Stamp", "Stamp")
    };

    if (stamp.HasValue)
    {
        service.UpsertDeviceTableStamp(normalizedSerial, normalizedTable, stamp.Value);
    }
}

static async Task<(int Saved, long Bytes)> SaveRawAttPhotoPayloadAsync(
    string appDataRoot,
    string? serialNumber,
    HttpRequest request,
    CancellationToken cancellationToken)
{
    if (request.Body is null)
    {
        return (0, 0);
    }

    static bool LooksLikeAsciiText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return true;
        }

        var sample = Math.Min(bytes.Length, 512);
        var nonText = 0;

        for (var i = 0; i < sample; i++)
        {
            var b = bytes[i];
            if (b == 0)
            {
                return false;
            }

            if (b is 9 or 10 or 13)
            {
                continue;
            }

            if (b >= 32 && b <= 126)
            {
                continue;
            }

            nonText++;
        }

        return nonText <= sample / 25;
    }

    static string ResolveAttPhotoFileExtension(ReadOnlySpan<byte> header, string? contentType)
    {
        var normalizedContentType = contentType?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(normalizedContentType))
        {
            var token = normalizedContentType.ToLowerInvariant();
            if (token.Contains("jpeg", StringComparison.Ordinal) || token.Contains("jpg", StringComparison.Ordinal))
            {
                return "jpg";
            }
            if (token.Contains("png", StringComparison.Ordinal))
            {
                return "png";
            }
            if (token.Contains("gif", StringComparison.Ordinal))
            {
                return "gif";
            }
            if (token.Contains("text", StringComparison.Ordinal) ||
                token.Contains("json", StringComparison.Ordinal) ||
                token.Contains("xml", StringComparison.Ordinal) ||
                token.Contains("x-www-form-urlencoded", StringComparison.Ordinal))
            {
                return "txt";
            }
        }

        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return "jpg";
        }

        if (header.Length >= 8 &&
            header[0] == 0x89 &&
            header[1] == 0x50 &&
            header[2] == 0x4E &&
            header[3] == 0x47 &&
            header[4] == 0x0D &&
            header[5] == 0x0A &&
            header[6] == 0x1A &&
            header[7] == 0x0A)
        {
            return "png";
        }

        if (header.Length >= 6 &&
            header[0] == (byte)'G' &&
            header[1] == (byte)'I' &&
            header[2] == (byte)'F' &&
            header[3] == (byte)'8' &&
            header[4] == (byte)'7' &&
            header[5] == (byte)'a')
        {
            return "gif";
        }

        if (header.Length >= 6 &&
            header[0] == (byte)'G' &&
            header[1] == (byte)'I' &&
            header[2] == (byte)'F' &&
            header[3] == (byte)'8' &&
            header[4] == (byte)'9' &&
            header[5] == (byte)'a')
        {
            return "gif";
        }

        return LooksLikeAsciiText(header) ? "txt" : "bin";
    }

    try
    {
        var serialToken = NormalizeSerialToken(serialNumber);
        if (string.IsNullOrWhiteSpace(serialToken))
        {
            serialToken = "UNKNOWN";
        }

        var targetDir = Path.Combine(appDataRoot, "attphoto", serialToken);
        Directory.CreateDirectory(targetDir);

        var headerBuffer = new byte[512];
        var headerBytes = await request.Body.ReadAsync(headerBuffer.AsMemory(0, headerBuffer.Length), cancellationToken);
        if (headerBytes <= 0)
        {
            return (0, 0);
        }

        var ext = ResolveAttPhotoFileExtension(headerBuffer.AsSpan(0, headerBytes), request.ContentType);
        var fileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmssfff}_{Guid.NewGuid():N}.{ext}";
        var path = Path.Combine(targetDir, fileName);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: true);
        await stream.WriteAsync(headerBuffer.AsMemory(0, headerBytes), cancellationToken);

        long bytesWritten = headerBytes;
        var buffer = new byte[81920];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            bytesWritten += read;
        }

        return (1, bytesWritten);
    }
    catch
    {
        return (0, 0);
    }
}

static string BuildOptionsResponse(
    AttendanceService service,
    string? serialNumber,
    int delaySeconds,
    int defaultTimeZoneMinutes,
    bool enableAttPhoto,
    bool forceFullDataReset = false,
    bool includeUserRealtimeSync = true)
{
    var sn = string.IsNullOrWhiteSpace(serialNumber) ? "UNKNOWN" : serialNumber;
    var normalizedSerial = string.IsNullOrWhiteSpace(serialNumber) ? string.Empty : serialNumber.Trim();
    var stamps = string.IsNullOrWhiteSpace(normalizedSerial)
        ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        : service.GetDeviceTableStamps(normalizedSerial);

    long ResolveStamp(string tableName)
        => stamps.TryGetValue(tableName, out var stamp) ? stamp : 0L;

    var attLogStamp = ResolveStamp("ATTLOG");
    var opLogStamp = ResolveStamp("OPERLOG");
    var attPhotoStamp = ResolveStamp("ATTPHOTO");
    var userInfoStamp = ResolveStamp("USERINFO");
    var bioDataStamp = ResolveStamp("BIODATA");
    var bioPhotoStamp = ResolveStamp("BIOPHOTO");
    var fingerTmpStamp = ResolveStamp("FINGERTMP");
    var faceStamp = ResolveStamp("FACE");
    var userPicStamp = ResolveStamp("USERPIC");

    var timeZoneMinutes = defaultTimeZoneMinutes;
    var multiBioDataSupport = "0:1:1:0:0:0:0:0:0:1:0";
    var multiBioPhotoSupport = "0:0:1:0:0:0:0:0:0:1:0";

    if (!string.IsNullOrWhiteSpace(normalizedSerial))
    {
        var device = service.ListDevices().FirstOrDefault(d =>
            string.Equals(d.SerialNumber, normalizedSerial, StringComparison.OrdinalIgnoreCase));
        if (device is not null)
        {
            if (!string.IsNullOrWhiteSpace(device.TimeZone))
            {
                timeZoneMinutes = DeviceProtocolService.TimezoneToMinutes(device.TimeZone);
            }
            if (!string.IsNullOrWhiteSpace(device.MultiBioDataSupport))
            {
                multiBioDataSupport = device.MultiBioDataSupport;
            }
            if (!string.IsNullOrWhiteSpace(device.MultiBioPhotoSupport))
            {
                multiBioPhotoSupport = device.MultiBioPhotoSupport;
            }
        }
    }

    var transDataParts = includeUserRealtimeSync
        ? new List<string> { "AttLog", "OpLog", "EnrollUser", "ChgUser", "EnrollFP", "ChgFP", "FPImag", "FACE", "UserPic", "WORKCODE", "BioData", "BioPhoto" }
        : new List<string> { "AttLog", "OpLog", "WORKCODE" };
    if (enableAttPhoto)
    {
        transDataParts.Insert(2, "AttPhoto");
    }

    var transFlag = "TransData " + string.Join('\t', transDataParts);
    var lines = new List<string>
    {
        $"GET OPTION FROM: {sn}",
        // On a full data reset (triggered by a fetch-all), reset the attendance/operation stamps to 0 too
        // so the device re-uploads ALL attendance punches — not just users/bio. Previously only the
        // user/bio stamps below were reset, so a full fetch never re-pulled attendance and old punches
        // stayed on the device forever.
        $"Stamp={(forceFullDataReset ? 0 : attLogStamp)}",
        $"OpStamp={(forceFullDataReset ? 0 : opLogStamp)}",
        $"PhotoStamp={attPhotoStamp}",
        "ErrorDelay=60",
        $"Delay={Math.Clamp(delaySeconds, 10, 60)}",
        "TransTimes=",
        "TransInterval=1",
        $"TransFlag={transFlag}",
        $"TimeZone={timeZoneMinutes}",
        "Realtime=1",
        "Encrypt=0",
        "ServerVer=2.2.14",
        "PushProtVer=2.4.1",
        "PushOptionsFlag=1",
        // SupportPing line intentionally OMITTED. Some ZKTeco firmware treats the mere PRESENCE of the
        // SupportPing key as "ping enabled" regardless of its value, so even SupportPing=0 left the device
        // heartbeating via /iclock/ping (delivers no commands) instead of /iclock/getrequest. Omitting it
        // forces the device to use getrequest for idle heartbeat (spec p.34), which pulls pending commands.
        // (If diagnostics show the device needs the key present, re-add "SupportPing=0".)
        "RegistryOn=1",
        $"MultiBioDataSupport={multiBioDataSupport}",
        $"MultiBioPhotoSupport={multiBioPhotoSupport}",
        $"ATTLOGStamp={(forceFullDataReset ? 0 : attLogStamp)}",
        $"OPERLOGStamp={(forceFullDataReset ? 0 : opLogStamp)}",
        $"ATTPHOTOStamp={attPhotoStamp}"
    };

    if (includeUserRealtimeSync || forceFullDataReset)
    {
        // In PUSH protocol, stamps are used for resume-from-breakpoint. Only set stamps to 0 when a full re-upload is desired.
        lines.Add($"USERINFOStamp={(forceFullDataReset ? 0 : userInfoStamp)}");
        lines.Add($"BIODATAStamp={(forceFullDataReset ? 0 : bioDataStamp)}");
        lines.Add($"BIOPHOTOStamp={(forceFullDataReset ? 0 : bioPhotoStamp)}");
        lines.Add($"FINGERTMPStamp={(forceFullDataReset ? 0 : fingerTmpStamp)}");
        lines.Add($"FACEStamp={(forceFullDataReset ? 0 : faceStamp)}");
        lines.Add($"USERPICStamp={(forceFullDataReset ? 0 : userPicStamp)}");
    }

    // Keep LF separators for maximum firmware compatibility with command batches.
    return string.Join("\n", lines);
}

static string BuildDevicePresetCommand(DevicePresetCommandRequest request)
{
    var action = request.Action.Trim().ToLowerInvariant();
    return action switch
    {
        "check" => "CHECK",
        "info" => "INFO",
        "reboot" => "REBOOT",
        "unlock" => "AC_UNLOCK",
        "unalarm" => "AC_UNALARM",
        "clear-log" => "CLEAR LOG",
        "clear-photo" => "CLEAR PHOTO",
        "clear-data" => "CLEAR DATA",
        "clear-biodata" => "CLEAR BIODATA",
        "reload-options" => "RELOAD OPTIONS",
        "sync-time" => $"SET OPTION TimeZone={request.TimeZone?.Trim() ?? "0"}",
        "query-attlog" => $"DATA QUERY ATTLOG StartTime={RequireValue(request.StartTime, "start_time")}\tEndTime={RequireValue(request.EndTime, "end_time")}",
        "query-attphoto" => $"DATA QUERY ATTPHOTO StartTime={RequireValue(request.StartTime, "start_time")}\tEndTime={RequireValue(request.EndTime, "end_time")}",
        "set-option" => $"SET OPTION {RequireValue(request.OptionKey, "option_key")}={RequireValue(request.OptionValue, "option_value")}",
        "enroll-fp" => $"ENROLL_FP\tPIN={RequireValue(request.Pin, "pin")}\tFID={request.Fid ?? 0}\tRETRY={request.Retry ?? 3}\tOVERWRITE={(request.Overwrite ?? true ? 1 : 0)}",
        "delete-user" => $"DATA DELETE USERINFO PIN={RequireValue(request.Pin, "pin")}",
        "query-user" => $"DATA QUERY USERINFO PIN={RequireValue(request.Pin, "pin")}",
        _ => throw new InvalidOperationException($"Unsupported action '{request.Action}'.")
    };
}

static string NormalizeCommandForQueue(string commandText)
{
    if (string.IsNullOrWhiteSpace(commandText))
    {
        return string.Empty;
    }

    var token = commandText.Trim();
    if (!token.StartsWith("C:", StringComparison.OrdinalIgnoreCase))
    {
        return token;
    }

    var first = token.IndexOf(':');
    if (first < 0)
    {
        return token;
    }

    var second = token.IndexOf(':', first + 1);
    if (second < 0 || second + 1 >= token.Length)
    {
        return token;
    }

    return token[(second + 1)..].Trim();
}

static int QueueEmployeePhotoSyncToAssignedDevices(
    AttendanceService service,
    DeviceProtocolService protocolService,
    PersonnelStore personnelStore,
    IEnumerable<Employee> employees,
    string defaultAreaCode,
    string defaultAreaName,
    string defaultDepartmentCode,
    string defaultDepartmentName)
{
    var candidates = employees
        .Where(e =>
            e.IsActive &&
            !string.IsNullOrWhiteSpace(e.EmployeeCode) &&
            !string.IsNullOrWhiteSpace(e.ProfilePhoto))
        .GroupBy(e => e.EmployeeCode.Trim(), StringComparer.OrdinalIgnoreCase)
        .Select(g => g.Last())
        .ToList();
    if (candidates.Count == 0)
    {
        return 0;
    }

    var profileByCode = personnelStore.Profiles
        .Where(p => !string.IsNullOrWhiteSpace(p.EmployeeCode))
        .GroupBy(p => p.EmployeeCode.Trim(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

    var commandCount = 0;
    foreach (var device in service.ListDevices())
    {
        var setup = ResolveDeviceSetupForSerial(
            personnelStore,
            device.SerialNumber,
            device.Location,
            defaultAreaCode,
            defaultAreaName,
            defaultDepartmentCode,
            defaultDepartmentName);

        foreach (var employee in candidates)
        {
            profileByCode.TryGetValue(employee.EmployeeCode.Trim(), out var profile);
            var departmentName = ResolveEmployeeDepartmentForDevice(
                personnelStore,
                employee,
                profile,
                setup.Department,
                defaultAreaCode,
                defaultAreaName,
                defaultDepartmentCode,
                defaultDepartmentName);
            if (string.IsNullOrWhiteSpace(departmentName))
            {
                continue;
            }

            var syncEmployee = CreateEmployeeForDeviceSync(employee, profile, departmentName);
            commandCount += QueueEmployeePhotoCommandsForDevice(service, protocolService, device, syncEmployee);
        }
    }

    return commandCount;
}

static Employee CreateEmployeeForDeviceSync(
    Employee employee,
    PersonnelEmployeeProfile? profile,
    string departmentName)
{
    var normalizedCode = employee.EmployeeCode?.Trim() ?? string.Empty;
    var effectivePrivilege = ResolveDeviceSyncPrivilege(profile?.Privilege, employee.Privilege);
    return new Employee
    {
        Id = employee.Id,
        EmployeeCode = normalizedCode,
        FullName = employee.FullName,
        Department = departmentName,
        DepartmentId = profile?.GroupId ?? employee.DepartmentId,
        Position = string.IsNullOrWhiteSpace(profile?.Position) ? employee.Position : profile.Position.Trim(),
        CardNumber = profile?.CardNumber ?? employee.CardNumber,
        Password = ResolveDevicePassword(profile?.Password, employee.Password),
        MobileNumber = profile?.MobileNumber ?? employee.MobileNumber,
        Gender = profile?.Gender ?? employee.Gender,
        Privilege = effectivePrivilege,
        VerifyMode = ResolveDeviceSyncVerifyMode(profile?.VerifyMode, employee.VerifyMode),
        FaceTemplateVisible = employee.FaceTemplateVisible,
        FaceTemplateInfrared = employee.FaceTemplateInfrared,
        ProfilePhoto = profile?.PictureDataUrl ?? employee.ProfilePhoto,
        FingerprintTemplates = employee.FingerprintTemplates,
        CreatedAt = employee.CreatedAt,
        SyncedAt = employee.SyncedAt,
        IsActive = employee.IsActive
    };
}

static void HydrateEmployeeBiometricsForDeviceSync(
    Employee employee,
    Device device,
    IReadOnlyDictionary<string, List<BiometricTemplateSnapshot>> biometricTemplatesByPin,
    IReadOnlyDictionary<string, List<BiometricPhotoSnapshot>> biometricPhotosByPin)
{
    if (string.IsNullOrWhiteSpace(employee.EmployeeCode))
    {
        return;
    }

    var pin = employee.EmployeeCode.Trim();
    if (biometricTemplatesByPin.TryGetValue(pin, out var templates) && templates.Count > 0)
    {
        var visibleTemplate = templates
            .Where(t => t.Type == 9 && !string.IsNullOrWhiteSpace(t.TemplateData))
            .OrderByDescending(t => t.UpdatedAt ?? DateTime.MinValue)
            .FirstOrDefault();
        if ((string.IsNullOrWhiteSpace(employee.FaceTemplateVisible) || IsLikelyPhotoPayload(employee.FaceTemplateVisible)) &&
            visibleTemplate is not null)
        {
            employee.FaceTemplateVisible = visibleTemplate.TemplateData;
        }

        var infraredTemplate = templates
            .Where(t => t.Type == 2 && !string.IsNullOrWhiteSpace(t.TemplateData))
            .OrderByDescending(t => t.UpdatedAt ?? DateTime.MinValue)
            .FirstOrDefault();
        if ((string.IsNullOrWhiteSpace(employee.FaceTemplateInfrared) || IsLikelyPhotoPayload(employee.FaceTemplateInfrared)) &&
            infraredTemplate is not null)
        {
            employee.FaceTemplateInfrared = infraredTemplate.TemplateData;
        }

        if (string.IsNullOrWhiteSpace(employee.FingerprintTemplates))
        {
            var targetVer = 10;
            if (!string.IsNullOrWhiteSpace(device.FingerprintAlgorithmVersion))
            {
                if (device.FingerprintAlgorithmVersion.Contains("13", StringComparison.Ordinal)) targetVer = 13;
                else if (device.FingerprintAlgorithmVersion.Contains("10", StringComparison.Ordinal)) targetVer = 10;
            }

            var fingerprintRows = templates
                .Where(t => t.Type != 9 && t.Type != 2 && !string.IsNullOrWhiteSpace(t.TemplateData))
                .Where(t => {
                    var templateVer = t.MajorVersion ?? t.Format ?? 10;
                    if (templateVer == 0) templateVer = 10;
                    return templateVer == targetVer;
                })
                .ToList();
            if (fingerprintRows.Count > 0)
            {
                employee.FingerprintTemplates = BuildFingerprintTemplateJson(fingerprintRows);
            }
        }
    }

    var normalizedProfile = string.IsNullOrWhiteSpace(employee.ProfilePhoto)
        ? string.Empty
        : NormalizeBiometricPhotoContent(employee.ProfilePhoto);

    if (!biometricPhotosByPin.TryGetValue(pin, out var photos) || photos.Count == 0)
    {
        if (!string.IsNullOrWhiteSpace(normalizedProfile))
        {
            employee.ProfilePhoto = normalizedProfile;
        }
        return;
    }

    string? ResolveLatestPhotoContent(int type)
        => photos
            .Where(p => p.Type == type && !string.IsNullOrWhiteSpace(p.Content))
            .OrderByDescending(p => p.UpdatedAt ?? DateTime.MinValue)
            .Select(p => p.Content)
            .FirstOrDefault();

    string? ResolvePreferredPhoto()
    {
        var visible = SupportsVisibleLightPhotoSync(device);
        var infrared = SupportsInfraredPhotoSync(device);

        if (visible)
        {
            return ResolveLatestPhotoContent(9) ?? ResolveLatestPhotoContent(2);
        }

        if (infrared)
        {
            return ResolveLatestPhotoContent(2) ?? ResolveLatestPhotoContent(9);
        }

        return ResolveLatestPhotoContent(9) ?? ResolveLatestPhotoContent(2);
    }

    var preferred = ResolvePreferredPhoto();
    if (!string.IsNullOrWhiteSpace(preferred))
    {
        employee.ProfilePhoto = NormalizeBiometricPhotoContent(preferred);
    }
    else if (!string.IsNullOrWhiteSpace(normalizedProfile))
    {
        employee.ProfilePhoto = normalizedProfile;
    }
}

static void UpsertProfilePhotoSnapshot(PostgresAttendanceRepository repository, Employee employee)
{
    if (string.IsNullOrWhiteSpace(employee.EmployeeCode) || string.IsNullOrWhiteSpace(employee.ProfilePhoto))
    {
        return;
    }

    repository.UpsertBiometricPhoto(
        userPin: employee.EmployeeCode.Trim(),
        type: 9,
        no: 0,
        idx: 0,
        format: null,
        content: employee.ProfilePhoto.Trim());
}

static int QueueEmployeePhotoCommandsForDevice(
    AttendanceService service,
    DeviceProtocolService protocolService,
    Device device,
    Employee employee)
{
    // CreateEmployeeBiometricSyncCommands is the single source of truth for everything pushed to a
    // device for one employee (fingerprint templates, face-match template, profile photo, and the
    // BIOPHOTO comparison photo at the right Type=9/2 for the device's capability) — this used to
    // duplicate that logic locally, which risked the two implementations drifting apart (and briefly
    // did: queuing BIOPHOTO here AND inside the shared method would have sent it twice).
    var commands = protocolService.CreateEmployeeBiometricSyncCommands(employee, device);
    if (commands.Count == 0)
    {
        return 0;
    }

    // CHECK triggers the device to immediately poll getrequest so it picks up the
    // following photo/face commands without waiting for its next heartbeat interval.
    service.QueueDeviceCommand(device.SerialNumber, "CHECK");
    var queued = QueueSyncCommand(
        service,
        device.SerialNumber,
        SyncType.UserInfo,
        protocolService.CreateUserInfoCommand(employee, device));

    var formats = new List<string>();
    foreach (var (type, command) in commands)
    {
        queued += QueueSyncCommand(service, device.SerialNumber, type, command);
        formats.Add(type switch
        {
            SyncType.Fingerprint => "FINGERTMP",
            SyncType.FaceTemplate => "BIODATA/BIOPHOTO (face match template/photo)",
            _ => "USERPIC (profile photo)"
        });
    }
    if (formats.Count > 0)
    {
        service.AddSyncLog(
            device.SerialNumber,
            SyncType.ProfilePhoto,
            $"Photo sync for PIN {employee.EmployeeCode}: queued {string.Join(", ", formats)}.",
            SyncStatus.Completed);
    }

    return queued;
}

static int QueueSyncCommand(
    AttendanceService service,
    string deviceSerialNumber,
    SyncType type,
    string commandText)
{
    var normalized = NormalizeCommandForQueue(commandText);
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return 0;
    }

    service.QueueDeviceCommand(deviceSerialNumber, normalized);
    service.AddSyncLog(deviceSerialNumber, type, commandText, SyncStatus.Pending);
    return 1;
}

static string ResolveDevicePassword(string? profilePassword, string? storedPassword)
{
    var profileToken = profilePassword?.Trim() ?? string.Empty;
    if (!string.IsNullOrWhiteSpace(profileToken) && !LooksLikeMd5Hash(profileToken) && IsValidDevicePassword(profileToken))
    {
        return profileToken;
    }

    var storedToken = storedPassword?.Trim() ?? string.Empty;
    if (!string.IsNullOrWhiteSpace(storedToken) && !LooksLikeMd5Hash(storedToken) && IsValidDevicePassword(storedToken))
    {
        return storedToken;
    }

    return string.Empty;
}

static bool IsValidDevicePassword(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    var token = value.Trim();
    return token.Length >= 6 && token.All(char.IsDigit);
}

static bool SupportsVisibleLightPhotoSync(Device device)
{
    return SupportsMultiBioPhotoType(device.MultiBioPhotoSupport, 9) ||
           device.SupportsVisibleLightFace ||
           device.VisiLightFunOn;
}

static bool SupportsInfraredPhotoSync(Device device)
{
    if (SupportsMultiBioPhotoType(device.MultiBioPhotoSupport, 2))
    {
        return true;
    }

    if (device.SupportsVisibleLightFace || device.VisiLightFunOn ||
        SupportsMultiBioPhotoType(device.MultiBioPhotoSupport, 9))
    {
        return false;
    }

    // Require an explicit signal that the device can actually use a comparison photo for face
    // verification — either BioPhotoFun=1 (can store/serve photo files), or FaceFunOn=1 (it runs
    // a face-matching algorithm at all). An unreported/blank MultiBioPhotoSupport must NOT be
    // treated as implicit permission on its own. Profile picture and face-match data are two
    // different things: USERPIC (queued unconditionally elsewhere) is just a display photo and
    // never makes punches verify: BIOPHOTO here is what the device actually compares against at
    // check-in, so FaceFunOn=1 devices need it attempted even when BioPhotoFun wasn't reported.
    return device.SupportsInfraredFace && (device.BioPhotoFun || device.FaceFunOn);
}

static bool SupportsMultiBioPhotoType(string? support, int type)
{
    if (string.IsNullOrWhiteSpace(support) || type < 0)
    {
        return false;
    }

    var parts = support.Split(':', StringSplitOptions.TrimEntries);
    return type < parts.Length && string.Equals(parts[type], "1", StringComparison.OrdinalIgnoreCase);
}

static string BuildPendingCommandPayload(
    AttendanceService service,
    string deviceSerialNumber,
    int maxCount,
    ConcurrentDictionary<Guid, string> commandSerialById)
{
    var commands = service.GetPendingCommands(deviceSerialNumber, maxCount).ToList();
    if (commands.Count == 0)
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-45);
        var retryable = service.GetDeviceCommands(deviceSerialNumber, 500)
            .Where(c => c.SentUtc.HasValue && string.IsNullOrWhiteSpace(c.ReplyText) && c.SentUtc.Value <= cutoff)
            .OrderBy(c => c.CreatedUtc)
            .Take(maxCount)
            .ToList();
        if (retryable.Count > 0)
        {
            commands = retryable;
        }
    }

    // Command isolation: AccUnLockComb and ENROLL_* commands must be delivered alone in their own poll
    // because the device drops the reply or fails to process them if delivered in a batch with other commands.
    if (commands.Count > 0)
    {
        var firstCmd = commands[0].CommandText ?? string.Empty;
        if (firstCmd.Contains("AccUnLockComb", StringComparison.OrdinalIgnoreCase) ||
            firstCmd.Contains("ENROLL_", StringComparison.OrdinalIgnoreCase))
        {
            commands = commands.Take(1).ToList();
        }
        else
        {
            // If the first command is not an isolated command, but there is an isolated command later in the batch,
            // truncate the batch before the first isolated command so it is not delivered in this batch.
            var firstIsolatedIndex = commands.FindIndex(c =>
            {
                var text = c.CommandText ?? string.Empty;
                return text.Contains("AccUnLockComb", StringComparison.OrdinalIgnoreCase) ||
                       text.Contains("ENROLL_", StringComparison.OrdinalIgnoreCase);
            });
            if (firstIsolatedIndex > 0)
            {
                commands = commands.Take(firstIsolatedIndex).ToList();
            }
        }
    }

    if (commands.Count == 0)
    {
        return "OK";
    }

    var lines = new List<string>();
    foreach (var command in commands)
    {
        commandSerialById[command.Id] = deviceSerialNumber;
        // Emit a short 13-char alias of the GUID as the command ID — REQUIRED by the ZKTeco PUSH
        // protocol spec (Attendance_PUSH_Communication_Protocol_20250429, p.85): "${CmdID} ... with a
        // length not over 16 digits." A full 32-char GUID violates that limit. The device echoes this
        // exact alias back in its devicecmd reply; ResolveCommandGuid resolves it via CommandIdAlias.Map
        // (fast path) or a prefix-scan that derives the full GUID from the 13-char prefix (restart-safe).
        var shortId = CommandIdAlias.ShortIdFor(command.Id);
        CommandIdAlias.Map[shortId] = command.Id;
        lines.Add($"C:{shortId}:{command.CommandText}");
        service.MarkCommandSent(command.Id);
        // Fire-and-forget commands: marked complete on delivery so they drain after one send and
        // don't get recycled forever by the stale-reset + retry logic (which would never drain the
        // queue otherwise — the core "AccUnLockComb stuck on pending" bug).
        //   DATA QUERY / CHECK: the device answers by uploading over the cdata channel, never via a
        //     devicecmd reply.
        //   AccTimeZone / AccGroup / AccUnLockComb: SpeedFace-V5L (and similar firmware) APPLIES the
        //     access-control config and then resets the PUSH session, dropping the devicecmd reply.
        //     Without fire-and-forget they stay "sent, no reply" → recycled endlessly → AccUnLockComb
        //     never drains. The device DID apply them, so completing on delivery is correct.
        var ctext = command.CommandText?.TrimStart() ?? string.Empty;
        if (ctext.StartsWith("DATA QUERY", StringComparison.OrdinalIgnoreCase) ||
            ctext.Equals("CHECK", StringComparison.OrdinalIgnoreCase) ||
            ctext.Contains("AccTimeZone", StringComparison.OrdinalIgnoreCase) ||
            ctext.Contains("AccGroup", StringComparison.OrdinalIgnoreCase) ||
            ctext.Contains("AccUnLockComb", StringComparison.OrdinalIgnoreCase))
        {
            service.RecordCommandReply(command.Id, "OK (delivered; device responds via cdata upload)");
        }
    }

    // Keep LF separators for firmware compatibility when parsing multi-command responses.
    return string.Join("\n", lines);
}

static void QueueMetadataRefreshCommands(
    AttendanceService service,
    string serialNumber,
    ConcurrentDictionary<string, DateTime> probeMap)
{
    var now = DateTime.UtcNow;
    if (probeMap.TryGetValue(serialNumber, out var last) && now - last < TimeSpan.FromMinutes(2))
    {
        return;
    }

    probeMap[serialNumber] = now;

    // Reset commands that were delivered but never replied to (e.g. device rebooted mid-flight).
    // GetPendingCommands only returns SentUtc IS NULL rows, so without this they stay stuck forever.
    var resetCount = service.ResetStaleSentCommands(serialNumber, olderThanMinutes: 2);
    if (resetCount > 0)
    {
        Console.WriteLine($"[DeviceAction] Reset {resetCount} stale sent command(s) for re-delivery to {serialNumber}.");
    }

    var pending = service.GetPendingCommands(serialNumber, 50);
    if (pending.Count > 0)
    {
        return;
    }

    var recent = service.GetDeviceCommands(serialNumber, 100);
    var staleThreshold = now - TimeSpan.FromMinutes(10);
    var hasUnrepliedInfo = recent.Any(c =>
        c.CreatedUtc >= staleThreshold &&
        c.ReplyText is null &&
        string.Equals(c.CommandText, "INFO", StringComparison.OrdinalIgnoreCase));

    if (hasUnrepliedInfo)
    {
        return;
    }

    // Only queue INFO for routine metadata refresh. RELOAD OPTIONS is intentionally excluded:
    // it causes a device reboot which clears the command queue and interrupts any in-flight
    // DATA QUERY fetch commands.
    if (!pending.Any(c => string.Equals(c.CommandText, "INFO", StringComparison.OrdinalIgnoreCase)))
    {
        service.QueueDeviceCommand(serialNumber, "INFO");
    }
}

static bool IsUserDataTable(string? tableName)
{
    if (string.IsNullOrWhiteSpace(tableName))
    {
        return false;
    }

    var normalized = tableName.Trim().ToUpperInvariant();
    return normalized is "USERINFO" or "BIODATA" or "BIOPHOTO" or "FINGERTMP" or "FACE" or "USERPIC";
}

static bool IsRegistrationDevice(AttendanceService service, string? serialNumber)
{
    if (string.IsNullOrWhiteSpace(serialNumber))
    {
        return false;
    }

    var serial = serialNumber.Trim();
    var device = service.ListDevices().FirstOrDefault(d =>
        string.Equals(d.SerialNumber, serial, StringComparison.OrdinalIgnoreCase));
    return device?.IsRegistrationDevice ?? false;
}

static void MarkManualUserSyncWindow(
    ConcurrentDictionary<string, DateTime> map,
    string? serialNumber,
    TimeSpan duration)
{
    if (string.IsNullOrWhiteSpace(serialNumber))
    {
        return;
    }

    map[serialNumber.Trim()] = DateTime.UtcNow.Add(duration);
}

static bool IsManualUserSyncWindowActive(
    ConcurrentDictionary<string, DateTime> map,
    string? serialNumber)
{
    if (string.IsNullOrWhiteSpace(serialNumber))
    {
        return false;
    }

    var key = serialNumber.Trim();
    if (!map.TryGetValue(key, out var untilUtc))
    {
        return false;
    }

    if (untilUtc < DateTime.UtcNow)
    {
        map.TryRemove(key, out _);
        return false;
    }

    return true;
}

static int ResolveCommandBatchSize(
    ConcurrentDictionary<string, DateTime> manualUserSyncMap,
    string? serialNumber,
    int defaultCount = 10,
    int fastCount = 25)
{
    return IsManualUserSyncWindowActive(manualUserSyncMap, serialNumber) ? fastCount : defaultCount;
}

static bool ShouldImportUserData(
    AttendanceService service,
    string? serialNumber,
    string? commandText,
    ConcurrentDictionary<string, DateTime> manualUserSyncMap,
    ConcurrentDictionary<string, DateTime>? clearAllDataPendingMap = null)
{
    // While a clear-all-data is pending for this device, never import uploaded user data — doing
    // so would immediately undo the clear (the "it just fetches instead of clearing" symptom).
    if (clearAllDataPendingMap is not null && IsClearAllDataPending(clearAllDataPendingMap, serialNumber))
    {
        return false;
    }

    if (IsRegistrationDevice(service, serialNumber))
    {
        return true;
    }

    if (IsManualUserSyncWindowActive(manualUserSyncMap, serialNumber))
    {
        return true;
    }

    if (string.IsNullOrWhiteSpace(commandText))
    {
        return false;
    }

    var normalized = commandText.Trim().ToUpperInvariant();
    if (!normalized.Contains("DATA QUERY", StringComparison.Ordinal))
    {
        return false;
    }

    return normalized.Contains("USERINFO", StringComparison.Ordinal) ||
           normalized.Contains("BIODATA", StringComparison.Ordinal) ||
           normalized.Contains("BIOPHOTO", StringComparison.Ordinal) ||
           normalized.Contains("FINGERTMP", StringComparison.Ordinal) ||
           normalized.Contains("FACE", StringComparison.Ordinal) ||
           normalized.Contains("USERPIC", StringComparison.Ordinal);
}

static void MarkFullDataResetPending(ConcurrentDictionary<string, DateTime> pendingResetMap, string serialNumber)
{
    if (string.IsNullOrWhiteSpace(serialNumber))
    {
        return;
    }

    pendingResetMap[serialNumber.Trim()] = DateTime.UtcNow;
}

static bool ConsumeFullDataResetPending(ConcurrentDictionary<string, DateTime> pendingResetMap, string? serialNumber)
{
    if (string.IsNullOrWhiteSpace(serialNumber))
    {
        return false;
    }

    var normalized = serialNumber.Trim();
    if (!pendingResetMap.TryGetValue(normalized, out var requestedAt))
    {
        return false;
    }

    pendingResetMap.TryRemove(normalized, out _);
    var age = DateTime.UtcNow - requestedAt;
    return age <= TimeSpan.FromMinutes(10);
}

static void MarkClearAllDataPending(ConcurrentDictionary<string, DateTime> pendingClearMap, string? serialNumber)
{
    if (string.IsNullOrWhiteSpace(serialNumber))
    {
        return;
    }

    pendingClearMap[serialNumber.Trim()] = DateTime.UtcNow;
}

// True (once) if this device was added with "clear all data". Consumes the flag so the
// clear/no-fetch behaviour only applies to the first connect after add.
static bool ConsumeClearAllDataPending(ConcurrentDictionary<string, DateTime> pendingClearMap, string? serialNumber)
{
    if (string.IsNullOrWhiteSpace(serialNumber))
    {
        return false;
    }

    var normalized = serialNumber.Trim();
    if (!pendingClearMap.TryGetValue(normalized, out var requestedAt))
    {
        return false;
    }

    pendingClearMap.TryRemove(normalized, out _);
    // Keep the intent valid for a generous window in case the device connects later.
    return DateTime.UtcNow - requestedAt <= TimeSpan.FromHours(24);
}

static bool IsClearAllDataPending(ConcurrentDictionary<string, DateTime> pendingClearMap, string? serialNumber)
{
    if (string.IsNullOrWhiteSpace(serialNumber))
    {
        return false;
    }

    var normalized = serialNumber.Trim();
    if (!pendingClearMap.TryGetValue(normalized, out var requestedAt))
    {
        return false;
    }

    // Safety: never suppress fetching forever — expire a stuck flag after 24h.
    if (DateTime.UtcNow - requestedAt > TimeSpan.FromHours(24))
    {
        pendingClearMap.TryRemove(normalized, out _);
        return false;
    }

    return true;
}

static string? TryBuildCdataCommandPayload(
    AttendanceService service,
    string? serialNumber,
    string? ipAddress,
    int maxCount,
    ConcurrentDictionary<Guid, string> commandSerialById,
    Action<string, string, string, string?>? addLog = null,
    string source = "CDATA")
{
    // Never deliver commands via CDATA POST response bodies to prevent firmware hangs and retries.
    // The device must poll /iclock/getrequest to pull commands.
    return null;
}

static async Task TryWakeDeviceAsync(string deviceIpAddress, string serialNumber, int apiPort)
{
    if (string.IsNullOrWhiteSpace(deviceIpAddress)) return;

    // 1. Try UDP wake-up on port 4370 (standard ZK port)
    try
    {
        using var udpClient = new System.Net.Sockets.UdpClient();
        var udpData = System.Text.Encoding.ASCII.GetBytes("CHECK");
        _ = udpClient.SendAsync(udpData, udpData.Length, deviceIpAddress, 4370);
    }
    catch
    {
        // Ignore UDP failures
    }

    // 2. Try TCP wake-up/probe on port 80 (standard HTTP web server on device)
    try
    {
        using var tcpClient = new System.Net.Sockets.TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        _ = tcpClient.ConnectAsync(deviceIpAddress, 80).WaitAsync(cts.Token);
    }
    catch
    {
        // Ignore TCP port 80 failures
    }

    // Try TCP wake-up/probe on port 4370 (standard ZK SDK TCP port)
    try
    {
        using var tcpClient = new System.Net.Sockets.TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        _ = tcpClient.ConnectAsync(deviceIpAddress, 4370).WaitAsync(cts.Token);
    }
    catch
    {
        // Ignore TCP port 4370 failures
    }

    // 3. Try legacy HTTP request (may cover custom environments)
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var wakeUrl = $"http://{deviceIpAddress}:{apiPort}/iclock/ping?SN={Uri.EscapeDataString(serialNumber)}";
        _ = await client.GetAsync(wakeUrl, cts.Token);
    }
    catch
    {
        // Ignore wake failures; direct command may still work.
    }
}

static string SanitizeCommandValue(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    return value
        .Replace('\t', ' ')
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Trim();
}

static string RequireValue(string? value, string fieldName)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"{fieldName} is required for this action.");
    }

    return value.Trim();
}

int HandleAttendancePayload(AttendanceService service, string? deviceSerialNumber, string payload, bool allowUnknownEmployeeAutoCreate = false)
{
    var count = 0;
    var skippedUnknown = 0;
    HashSet<string>? knownEmployeeCodes = null;
    if (!allowUnknownEmployeeAutoCreate)
    {
        knownEmployeeCodes = service.ListEmployees()
            .Select(e => e.EmployeeCode?.Trim())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    var lines = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var line in lines)
    {
        if (TryParseAttendanceLine(line, out var employeeCode, out var timestamp, out var status, out var verify, out var workcode, out var rawEmployeeCode))
        {
            var normalizedCode = employeeCode.Trim();
            if (knownEmployeeCodes is not null && !knownEmployeeCodes.Contains(normalizedCode))
            {
                skippedUnknown++;
                continue;
            }

            service.AddAttendanceFromDevice(normalizedCode, timestamp, status, verify, workcode, deviceSerialNumber);
            count++;
        }
    }

    if (skippedUnknown > 0)
    {
        AddLog(
            "WARN",
            "Attendance",
            $"Skipped {skippedUnknown} attendance row(s) for unknown/deleted employee code(s).",
            $"serial={deviceSerialNumber ?? "unknown"}");
    }

    return count;
}

static string NormalizeDeviceEmployeeCode(string? rawCode)
{
    if (string.IsNullOrWhiteSpace(rawCode))
    {
        return string.Empty;
    }

    var code = rawCode.Trim();
    if (code.StartsWith("PIN=", StringComparison.OrdinalIgnoreCase))
    {
        code = code[4..].Trim();
    }
    else if (code.StartsWith("UserPIN=", StringComparison.OrdinalIgnoreCase))
    {
        code = code[8..].Trim();
    }
    else if (code.StartsWith("UserId=", StringComparison.OrdinalIgnoreCase))
    {
        code = code[7..].Trim();
    }
    else if (code.StartsWith("EnrollNumber=", StringComparison.OrdinalIgnoreCase))
    {
        code = code[13..].Trim();
    }

    while (code.StartsWith("=", StringComparison.Ordinal))
    {
        code = code[1..].Trim();
    }

    return code;
}

static bool TryParseDeviceTimestamp(string? value, out DateTime timestamp)
{
    timestamp = DateTime.MinValue;
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    var token = value.Trim();

    string[] formats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss.ffffff",
        "yyyy-MM-dd HH:mm:ss.fffffff",
        "yyyy/MM/dd HH:mm:ss",
        "yyyy/MM/dd HH:mm:ss.fff",
        "yyyy/MM/dd HH:mm:ss.ffffff",
        "yyyy/MM/dd HH:mm:ss.fffffff",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.fff",
        "yyyy-MM-ddTHH:mm:ss.ffffff",
        "yyyy-MM-ddTHH:mm:ss.fffffff",
        "yyyy/MM/ddTHH:mm:ss",
        "yyyy/MM/ddTHH:mm:ss.fff",
        "yyyy/MM/ddTHH:mm:ss.ffffff",
        "yyyy/MM/ddTHH:mm:ss.fffffff"
    ];

    if (DateTime.TryParseExact(
            token,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out timestamp))
    {
        return true;
    }

    return DateTime.TryParse(
        token,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AllowWhiteSpaces,
        out timestamp);
}

static bool TryParseAttendanceLine(string line, out string employeeCode, out DateTime timestamp, out int status, out int verify, out string? workcode, out string rawEmployeeCode)
{
    employeeCode = string.Empty;
    timestamp = DateTime.MinValue;
    status = 0;
    verify = 0;
    workcode = null;
    rawEmployeeCode = string.Empty;

    if (string.IsNullOrWhiteSpace(line))
    {
        return false;
    }

    var trimmed = line.Trim();

    // "OPLOG ..." lines are OPERATION logs, NOT attendance punches. Per the ZKTeco PUSH spec
    // (Attendance_PUSH_Communication_Protocol_20250429, p.86): an operation log is
    // "OPLOG ${OpType}\t${OpWho}\t${OpTime}\t..." — the FIRST field is the operation TYPE code, not a
    // user PIN. An attendance record is "${Pin}\t${Time}\t${Status}\t..." with NO prefix (via table=ATTLOG).
    // The old code stripped "OPLOG " and parsed the OpType as the employee code, so the transactions
    // dashboard showed opcodes (e.g. 30, 101) as the user ID even though the only real user is PIN=1.
    // Reject OPLOG lines here so only genuine attendance punches are recorded.
    if (trimmed.StartsWith("OPLOG", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (trimmed.Contains('=', StringComparison.Ordinal))
    {
        var pairs = trimmed.Split('\t', StringSplitOptions.TrimEntries);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in pairs)
            {
                if (string.IsNullOrWhiteSpace(pair) || !pair.Contains('=', StringComparison.Ordinal))
                {
                    continue;
                }

                var kvParts = pair.Split('=', 2);
                var key = kvParts[0].Trim();
                var value = kvParts.Length > 1 ? kvParts[1].Trim() : string.Empty;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    values[key] = value;
                }
            }

        static string? TryGetFirst(Dictionary<string, string> values, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        var pin = TryGetFirst(values, "PIN", "UserPIN", "UserId", "UserID", "EnrollNumber");
        if (!string.IsNullOrWhiteSpace(pin))
        {
            rawEmployeeCode = $"PIN={pin}";
            employeeCode = NormalizeDeviceEmployeeCode(rawEmployeeCode);
        }

        if (!string.IsNullOrWhiteSpace(employeeCode))
        {
            var dateTimeToken = TryGetFirst(values, "DateTime", "Time", "Timestamp");
            if (TryParseDeviceTimestamp(dateTimeToken, out timestamp))
            {
                _ = int.TryParse(TryGetFirst(values, "Status", "State") ?? string.Empty, out status);
                _ = int.TryParse(TryGetFirst(values, "Verify", "VerifyMode") ?? string.Empty, out verify);
                workcode = TryGetFirst(values, "WorkCode", "Workcode");
                if (string.IsNullOrWhiteSpace(workcode))
                {
                    workcode = null;
                }

                return true;
            }
        }
    }

    var tabParts = trimmed.Split('\t', StringSplitOptions.TrimEntries);
    if (tabParts.Length >= 3)
    {
        rawEmployeeCode = tabParts[0];
        employeeCode = NormalizeDeviceEmployeeCode(rawEmployeeCode);
        if (string.IsNullOrWhiteSpace(employeeCode))
        {
            return false;
        }

        var statusIndex = 2;
        if (!TryParseDeviceTimestamp(tabParts[1], out timestamp))
        {
            if (tabParts.Length >= 4 && TryParseDeviceTimestamp($"{tabParts[1]} {tabParts[2]}", out timestamp))
            {
                statusIndex = 3;
            }
            else if (tabParts.Length >= 4 && TryParseDeviceTimestamp(tabParts[2], out timestamp))
            {
                // OPLOG rows carry a reserved numeric field between the PIN and the timestamp
                // (PIN\t<reserved>\t<timestamp>\t...) rather than the timestamp starting at index 1.
                statusIndex = 3;
            }
            else
            {
                return false;
            }
        }

        if (tabParts.Length <= statusIndex)
        {
            return false;
        }

        _ = int.TryParse(tabParts[statusIndex], out status);
        if (tabParts.Length > statusIndex + 1)
        {
            _ = int.TryParse(tabParts[statusIndex + 1], out verify);
        }

        if (tabParts.Length > statusIndex + 2 && !string.IsNullOrWhiteSpace(tabParts[statusIndex + 2]))
        {
            workcode = tabParts[statusIndex + 2];
        }

        return true;
    }

    var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length < 4)
    {
        return false;
    }

    rawEmployeeCode = parts[0];
    employeeCode = NormalizeDeviceEmployeeCode(rawEmployeeCode);
    if (string.IsNullOrWhiteSpace(employeeCode))
    {
        return false;
    }

    if (!TryParseDeviceTimestamp($"{parts[1]} {parts[2]}", out timestamp))
    {
        return false;
    }

    _ = int.TryParse(parts[3], out status);
    if (parts.Length >= 5)
    {
        _ = int.TryParse(parts[4], out verify);
    }

    if (parts.Length >= 6)
    {
        workcode = parts[5];
    }

    return true;
}

static string? ExtractPinFromCommandText(string? commandText)
{
    if (string.IsNullOrWhiteSpace(commandText))
    {
        return null;
    }

    var match = System.Text.RegularExpressions.Regex.Match(commandText, @"\bPIN=([^\s\t]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    return match.Success ? match.Groups[1].Value.Trim() : null;
}

static void HandleCommandReplies(
    AttendanceService service,
    string payload,
    ConcurrentDictionary<Guid, string> commandSerialById,
    ConcurrentDictionary<string, DiscoveredDevice> discoveredDevices,
    Action<string, string, string, string?>? addLog = null,
    Action<Guid, string?, string?, string>? onDataReply = null)
{
    if (string.IsNullOrWhiteSpace(payload))
    {
        return;
    }

    var lines = payload.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    Guid? activeCommandId = null;
    var activeReply = new StringBuilder();

    void FlushActiveReply()
    {
        if (activeCommandId is not Guid id)
        {
            return;
        }

        var text = activeReply.ToString().Trim();
        if (text.Length == 0)
        {
            return;
        }

        service.RecordCommandReply(id, text);
        var code = ExtractReturnCode(text);
        var cmd = ExtractCommandName(text);
        commandSerialById.TryGetValue(id, out var serial);
        string? queuedCommandText = null;
        if (!string.IsNullOrWhiteSpace(serial))
        {
            queuedCommandText = service.GetDeviceCommands(serial, 2000).FirstOrDefault(c => c.Id == id)?.CommandText;
        }
        addLog?.Invoke(
            code == "0" ? "INFO" : "WARN",
            "DeviceAction",
            $"Device command reply: ID={id:N}, CMD={cmd}, Return={code}",
            text);
        TryUpdateSyncLogFromCommandReply(service, serial, queuedCommandText, code, text);

        if (string.Equals(cmd, "DATA", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(code, "0", StringComparison.OrdinalIgnoreCase) &&
            onDataReply is not null)
        {
            try
            {
                onDataReply(id, serial, queuedCommandText, text);
            }
            catch (Exception ex)
            {
                addLog?.Invoke(
                    "WARN",
                    "DeviceAction",
                    $"Failed to process DATA reply: ID={id:N}, serial={serial ?? "unknown"}",
                    ex.Message);
            }
        }

        if (string.Equals(cmd, "ENROLL_FP", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(code, "0", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(serial))
        {
            var pin = ExtractPinFromCommandText(queuedCommandText);
            if (!string.IsNullOrWhiteSpace(pin))
            {
                service.QueueDeviceCommand(serial, $"DATA QUERY FINGERTMP PIN={pin}");
                service.QueueDeviceCommand(serial, $"DATA QUERY BIODATA PIN={pin}");
                addLog?.Invoke(
                    "INFO",
                    "Enrollment",
                    $"ENROLL_FP succeeded for {serial} PIN={pin}; auto-queried fingerprint templates.",
                    queuedCommandText);
            }
        }

        if (string.Equals(cmd, "INFO", StringComparison.OrdinalIgnoreCase))
        {
            var deviceName = ExtractInfoReplyDeviceName(text);
            if (!string.IsNullOrWhiteSpace(deviceName) && !string.IsNullOrWhiteSpace(serial))
            {
                // INFO reply contains the device's display name, not its model number.
                // Model is populated separately from the OPTIONS push (DeviceType key).
                discoveredDevices.AddOrUpdate(
                    serial,
                    _ => new DiscoveredDevice(
                        serial,
                        deviceName,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        DateTime.UtcNow,
                        true,
                        0,
                        0,
                        0,
                        0,
                        -1),
                    (_, old) => old with
                    {
                        Name = deviceName,
                        // Do NOT overwrite Model here; model comes from OPTIONS DeviceType.
                        LastActivityUtc = DateTime.UtcNow,
                        IsOnline = true
                    });

                var existing = service.ListDevices().FirstOrDefault(d => string.Equals(d.SerialNumber, serial, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    var nameNeedsInitialization =
                        string.IsNullOrWhiteSpace(existing.Name) ||
                        string.Equals(existing.Name, existing.SerialNumber, StringComparison.OrdinalIgnoreCase);
                    var modelNeedsInitialization =
                        string.IsNullOrWhiteSpace(existing.Model) ||
                        string.Equals(existing.Model, "None", StringComparison.OrdinalIgnoreCase);

                    service.UpdateDeviceInfo(
                        existing.Id,
                        nameNeedsInitialization ? deviceName : null,
                        modelNeedsInitialization ? deviceName : null,
                        existing.IpAddress,
                        existing.Location);
                }
            }
        }

        commandSerialById.TryRemove(id, out _);
        CommandIdAlias.Map.TryRemove(CommandIdAlias.ShortIdFor(id), out _);
    }

    foreach (var line in lines)
    {
        if (TryExtractCommandIdToken(line, out var idToken) &&
            ResolveCommandGuid(idToken, commandSerialById) is Guid commandId)
        {
            FlushActiveReply();
            activeCommandId = commandId;
            activeReply.Clear();
            activeReply.Append(line.Trim());
            continue;
        }

        if (activeCommandId is null)
        {
            continue;
        }

        if (activeReply.Length > 0)
        {
            activeReply.Append('\n');
        }
        activeReply.Append(line.Trim());
    }

    FlushActiveReply();
}

static void TryUpdateSyncLogFromCommandReply(
    AttendanceService service,
    string? serial,
    string? queuedCommandText,
    string? returnCode,
    string replyText)
{
    if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(queuedCommandText))
    {
        return;
    }

    var normalizedCommand = NormalizeCommandForSyncMatch(queuedCommandText);
    if (normalizedCommand.Length == 0)
    {
        return;
    }

    var logs = service.GetSyncLogs(serial, 2000);
    var pending = logs.FirstOrDefault(log =>
        log.Status == SyncStatus.Pending &&
        string.Equals(
            NormalizeCommandForSyncMatch(log.CommandText),
            normalizedCommand,
            StringComparison.Ordinal));
    if (pending is null)
    {
        return;
    }

    var status = string.Equals(returnCode, "0", StringComparison.OrdinalIgnoreCase)
        ? SyncStatus.Completed
        : SyncStatus.Failed;
    service.UpdateSyncLogResult(pending.Id, status, replyText);
}

static string NormalizeCommandForSyncMatch(string? commandText)
{
    var normalized = NormalizeCommandForQueue(commandText ?? string.Empty);
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return string.Empty;
    }

    var parts = normalized
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return string.Join(' ', parts).ToUpperInvariant();
}

// Extracts the raw ID token from a device reply line. Device replies may use & or space
// separators (e.g. "CMD=DATA ID=fbdc7cb4448d4 Return=0"). Resolution to a Guid is done
// separately by ResolveCommandGuid so a truncated/variant echoed ID can still be matched.
static bool TryExtractCommandIdToken(string line, out string idToken)
{
    idToken = string.Empty;
    if (string.IsNullOrWhiteSpace(line))
    {
        return false;
    }

    foreach (var part in line.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (part.StartsWith("ID=", StringComparison.OrdinalIgnoreCase))
        {
            idToken = part.Substring(3).Trim();
            return idToken.Length > 0;
        }
    }

    var match = Regex.Match(line, @"(?:^|[\s&])ID=([0-9a-fA-F\-]{8,36})", RegexOptions.IgnoreCase);
    if (match.Success)
    {
        idToken = match.Groups[1].Value.Trim();
        return idToken.Length > 0;
    }

    return false;
}

// Resolves an echoed ID token back to the internal command Guid. The device echoes the
// 16-char hex prefix we sent, so a full Guid parse will usually fail; fall back to a prefix
// scan over the in-flight commands. This is the proven, restart-tolerant scheme from the
// pre-regression build (the retry path re-populates commandSerialById on re-delivery).
static Guid? ResolveCommandGuid(string idToken, ConcurrentDictionary<Guid, string> commandSerialById)
{
    if (string.IsNullOrWhiteSpace(idToken))
    {
        return null;
    }

    if (CommandIdAlias.Map.TryGetValue(idToken, out var guid))
    {
        return guid;
    }

    if (Guid.TryParseExact(idToken, "N", out var exact) || Guid.TryParse(idToken, out exact))
    {
        return exact;
    }

    var prefix = idToken.Replace("-", string.Empty).Trim();
    if (prefix.Length == 0)
    {
        return null;
    }

    foreach (var key in commandSerialById.Keys)
    {
        if (key.ToString("N").StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return key;
        }
    }

    return null;
}

static string ExtractReturnCode(string? replyText)
{
    if (string.IsNullOrWhiteSpace(replyText))
    {
        return string.Empty;
    }

    var match = Regex.Match(replyText, @"(?:^|[&\s])Return=([-\d]+)", RegexOptions.IgnoreCase);
    return match.Success ? match.Groups[1].Value : string.Empty;
}

static string DescribeEnrollmentCode(string? code)
{
    return (code ?? string.Empty).Trim() switch
    {
        "0" => "Success",
        "-1" => "Parameter error",
        "-3" => "Access error",
        "2" => "User already has face",
        "4" => "Enrollment failed (poor quality)",
        "5" => "Face already exists in device",
        "6" => "Enrollment cancelled",
        "7" => "Device busy",
        "-100" => "Face algorithm error",
        "-1002" => "Not supported",
        "-1003" => "Face enrollment timeout or poor capture quality",
        _ => "Enrollment failed"
    };
}

static void ApplyInfoUpdate(
    AttendanceService service,
    string? serialNumber,
    string infoPayload,
    ConcurrentDictionary<string, DateTime>? autoUserFetchUtcBySerial = null,
    ConcurrentDictionary<string, DateTime>? clearAllDataPendingBySerial = null)
{
    if (string.IsNullOrWhiteSpace(serialNumber) || string.IsNullOrWhiteSpace(infoPayload))
    {
        return;
    }

    var values = infoPayload.Split(',', StringSplitOptions.TrimEntries);
    if (values.Length == 0)
    {
        return;
    }

    var infoIp = values.Length > 4 ? values[4] : string.Empty;
    EnsureDeviceRegistered(service, serialNumber, infoIp);

    // INFO payload: firmware,userCount,fpCount,attCount,ip,...,faceCount
    var fpQty = values.Length >= 3 && int.TryParse(values[2], out var fp) ? fp : 0;
    var faceQty = values.Length >= 9 && int.TryParse(values[8], out var fa) ? fa : 0;
    var current = service.ListDevices().FirstOrDefault(d => string.Equals(d.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase));
    var visibleLight = current?.SupportsVisibleLightFace ?? false;
    var infrared = (current?.SupportsInfraredFace ?? false) || faceQty > 0;
    var fingerprint = (current?.SupportsFingerprint ?? false) || fpQty > 0;
    var firmware = values.Length > 0 ? values[0] : string.Empty;
    var fpAlgVersion = values.Length > 5 ? values[5] : string.Empty;
    var faceAlgVersion = values.Length > 6 ? values[6] : string.Empty;
    service.UpdateDeviceCapabilities(
        serialNumber,
        visibleLight,
        infrared,
        fingerprint,
        faceAlgorithmVersion: faceAlgVersion,
        fingerprintAlgorithmVersion: fpAlgVersion);
    service.UpdateDeviceProtocolInfo(serialNumber, firmwareVersion: firmware);

    if (autoUserFetchUtcBySerial is null)
    {
        return;
    }

    var device = service.ListDevices().FirstOrDefault(d => string.Equals(d.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase));
    if (device is null || !device.IsRegistrationDevice)
    {
        return;
    }

    // If a "clear all data" is pending for this device, do NOT auto-fetch its users — that is
    // exactly what would make cleared data reappear. Once the device reports it is empty
    // (userCount == 0) the clear is complete, so we drop the flag and resume normal behaviour.
    if (clearAllDataPendingBySerial is not null && IsClearAllDataPending(clearAllDataPendingBySerial, serialNumber))
    {
        var reportedUserCount = values.Length >= 2 && int.TryParse(values[1], out var uc) ? uc : -1;
        if (reportedUserCount == 0)
        {
            ConsumeClearAllDataPending(clearAllDataPendingBySerial, serialNumber);
        }
        return; // skip auto user-fetch while a clear is in progress
    }

    // Do not enqueue auto user-fetch while command queue is still busy.
    if (service.GetPendingCommands(serialNumber, 50).Count > 0)
    {
        return;
    }

    // Avoid re-queuing if recent user-fetch commands are still waiting for reply OR were
    // just auto-delivered (DATA QUERY commands are fire-and-forget: they get ReplyText =
    // "OK (delivered; device responds via cdata upload)" the moment they are sent, so
    // c.ReplyText is null is never true for them — we must also treat auto-delivered
    // commands within the same 10-minute window as "still in-flight").
    var recent = service.GetDeviceCommands(serialNumber, 200);
    var staleThreshold = DateTime.UtcNow - TimeSpan.FromMinutes(10);
    var hasRecentUserFetch = recent.Any(c =>
        c.CreatedUtc >= staleThreshold &&
        InferDataReplyTableFromCommand(c.CommandText).Length > 0 &&
        (c.ReplyText is null ||
         c.ReplyText.StartsWith("OK (delivered", StringComparison.OrdinalIgnoreCase)));
    if (hasRecentUserFetch)
    {
        return;
    }

    var now = DateTime.UtcNow;
    if (autoUserFetchUtcBySerial.TryGetValue(serialNumber, out var last) && now - last < TimeSpan.FromMinutes(5))
    {
        return;
    }

    autoUserFetchUtcBySerial[serialNumber] = now;
    QueueServerUserFetchCommands(service, serialNumber);
}

static void QueueServerUserFetchCommands(AttendanceService service, string serialNumber)
{
    service.QueueDeviceCommand(serialNumber, "CHECK");
    service.QueueDeviceCommand(serialNumber, "DATA QUERY USERINFO PIN=*");
    service.QueueDeviceCommand(serialNumber, "DATA QUERY USERINFO");
    service.QueueDeviceCommand(serialNumber, "DATA QUERY BIODATA");
    service.QueueDeviceCommand(serialNumber, "DATA QUERY FINGERTMP");
    service.QueueDeviceCommand(serialNumber, "DATA QUERY FACE");
    service.QueueDeviceCommand(serialNumber, "DATA QUERY BIOPHOTO");
    service.QueueDeviceCommand(serialNumber, "DATA QUERY USERPIC");
}

async Task<FetchAllQueueResult> QueueFetchAllCommandsAsync(
    AttendanceService service,
    Device device,
    bool includeAttendanceLogs,
    ConcurrentDictionary<string, DateTime> manualUserSyncUntilUtcBySerial,
    ConcurrentDictionary<string, DateTime> fullDataResetUtcBySerial,
    int apiPort)
{
    var normalizedSerial = device.SerialNumber.Trim();

    // Throttle: skip a redundant fetch-all if one ran for this device in the last 60s. The dashboard
    // re-POSTs /fetch-all on its poll loop; without this guard every call re-queues the whole DATA
    // QUERY batch (endless re-queue loop) and the ClearPendingCommands below wipes the pending
    // access-control command (AccUnLockComb) before the device can pull it. One fetch-all per 60s
    // already pulls everything. QueueDeviceCommand-level work is idempotent within that window.
    if (fetchAllUtcBySerial.TryGetValue(normalizedSerial, out var lastFetchAll) &&
        DateTime.UtcNow - lastFetchAll < TimeSpan.FromSeconds(60))
    {
        var existingPending = service.GetPendingCommands(normalizedSerial, 200).Count;
        AddLog("INFO", "Command", $"Skipped redundant fetch-all for {normalizedSerial} (one ran < 60s ago).");
        return new FetchAllQueueResult(0, existingPending, false, Guid.Empty, new List<string>());
    }
    fetchAllUtcBySerial[normalizedSerial] = DateTime.UtcNow;

    // Reset ALL in-flight commands immediately — after a restart CommandIdAlias.Map is empty
    // and in-flight commands from before restart would never get a reply match.
    var resetStale = service.ResetStaleSentCommands(normalizedSerial, olderThanMinutes: 2);
    if (resetStale > 0)
    {
        AddLog("INFO", "Command", $"Reset {resetStale} in-flight command(s) to pending before fetch-all for {normalizedSerial}.");
    }
    // NOTE: deliberately NOT calling ClearPendingCommands here. It deleted the pending access-control
    // command (AccUnLockComb, queued separately) mid-flight. The 60s throttle above plus the device's
    // own one-command-per-poll draining make clearing unnecessary; re-queuing the same DATA QUERY is
    // harmless because delivered DATA QUERY/CHECK are marked complete (fire-and-forget) on send.
    var clearedPending = 0;
    MarkManualUserSyncWindow(manualUserSyncUntilUtcBySerial, normalizedSerial, TimeSpan.FromMinutes(30));
    MarkFullDataResetPending(fullDataResetUtcBySerial, normalizedSerial);
    // RELOAD OPTIONS is intentionally NOT queued here — it causes a device reboot which
    // clears the remaining command queue before DATA QUERY commands can execute.
    var checkCommand = service.QueueDeviceCommand(normalizedSerial, "CHECK");
    QueueServerUserFetchCommands(service, normalizedSerial);
    // Auto time-sync on every connect. The SET OPTION MachineTZ/TimeZone/DateTime trio below is
    // kept (harmless if unused), but the dashboard's manual "Sync time" button — confirmed actually
    // working — proved the real mechanism is raw SHELL commands (device runs embedded Linux):
    // "SHELL date -s <local time>" + "SHELL hwclock -w" to persist it to the RTC. Queue that same
    // pair here too so every device gets a real clock sync automatically on connect, not only when
    // someone clicks the button. (Deliberately NOT queuing RELOAD OPTIONS like the manual button
    // does — it reboots the device and would wipe the DATA QUERY batch queued above.)
    service.QueueDeviceCommand(normalizedSerial, protocolService.CreateMachineTzCommand(device));
    service.QueueDeviceCommand(normalizedSerial, protocolService.CreateTimezoneCommand(device));
    service.QueueDeviceCommand(normalizedSerial, protocolService.CreateTimeSyncCommand(DateTime.Now));
    service.QueueDeviceCommand(normalizedSerial, $"SHELL date -s \"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\"");
    service.QueueDeviceCommand(normalizedSerial, "SHELL hwclock -w");
    // Auto-push each active employee's photo/fingerprint/face-template data on connect too, using
    // the device's own already-known capabilities (BioPhotoFun/FaceFunOn/SupportsInfraredFace/etc.,
    // read from its stored INFO) to pick BIOPHOTO Type=9 (visible-light) vs Type=2 (infrared) vs
    // USERPIC-only — the same capability-aware routing already used when an employee is edited.
    // Previously this only ran on employee save, so a device that connects after already having
    // employees on file never received their biometric data at all.
    var alreadyPushedToThisDevice = employeePushedOnceBySerial.ContainsKey(normalizedSerial);
    foreach (var employee in service.ListEmployees().Where(e => e.IsActive))
    {
        if (alreadyPushedToThisDevice && employee.SyncedAt.HasValue)
        {
            continue;
        }
        QueueEmployeePhotoCommandsForDevice(service, protocolService, device, employee);
    }
    employeePushedOnceBySerial[normalizedSerial] = true;
    var queuedSteps = new List<string>
    {
        "CHECK",
        "SET OPTION MachineTZ",
        "SET OPTION TimeZone",
        "SET OPTION DateTime",
        "DATA QUERY USERINFO PIN=*",
        "DATA QUERY USERINFO",
        "DATA QUERY BIODATA",
        "DATA QUERY FINGERTMP",
        "DATA QUERY FACE",
        "DATA QUERY BIOPHOTO",
        "DATA QUERY USERPIC",
        "RESET_STAMPS_ON_OPTIONS_ALL"
    };

    if (includeAttendanceLogs)
    {
        var existingPunchCounts = LoadDevicePunchCounts(postgresConnectionString, new[] { normalizedSerial });
        var hasPunches = existingPunchCounts.TryGetValue(normalizedSerial, out var dbCount) && dbCount > 0;
        var start = hasPunches && attLogRangeStartBySerial.TryGetValue(normalizedSerial, out var prevEnd) ? prevEnd : "2000-01-01 00:00:00";
        var end = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        service.QueueDeviceCommand(normalizedSerial, $"DATA QUERY ATTLOG StartTime={start}\tEndTime={end}");
        attLogRangeStartBySerial[normalizedSerial] = end;
        queuedSteps.Add("DATA QUERY ATTLOG");
    }

    var wakeAttempted = false;
    if (!string.IsNullOrWhiteSpace(device.IpAddress))
    {
        wakeAttempted = true;
        await TryWakeDeviceAsync(device.IpAddress, normalizedSerial, apiPort);
    }
    var pendingCount = service.GetPendingCommands(normalizedSerial, 200).Count;

    return new FetchAllQueueResult(
        clearedPending,
        pendingCount,
        wakeAttempted,
        checkCommand.Id,
        queuedSteps);
}

static string ExtractCommandName(string? replyText)
{
    if (string.IsNullOrWhiteSpace(replyText))
    {
        return string.Empty;
    }

    var match = Regex.Match(replyText, @"(?:^|[&\s])CMD=([A-Za-z0-9_]+)", RegexOptions.IgnoreCase);
    return match.Success ? match.Groups[1].Value : string.Empty;
}

static string InferDataReplyTableFromCommand(string? commandText)
{
    if (string.IsNullOrWhiteSpace(commandText))
    {
        return string.Empty;
    }

    var normalized = commandText.Trim().ToUpperInvariant();
    return normalized switch
    {
        var s when s.Contains("DATA QUERY USERINFO", StringComparison.Ordinal) => "USERINFO",
        var s when s.Contains("DATA QUERY BIODATA", StringComparison.Ordinal) => "BIODATA",
        var s when s.Contains("DATA QUERY BIOPHOTO", StringComparison.Ordinal) => "BIOPHOTO",
        var s when s.Contains("DATA QUERY FINGERTMP", StringComparison.Ordinal) => "FINGERTMP",
        var s when s.Contains("DATA QUERY FACE", StringComparison.Ordinal) => "FACE",
        var s when s.Contains("DATA QUERY USERPIC", StringComparison.Ordinal) => "USERPIC",
        var s when s.Contains("DATA QUERY ATTLOG", StringComparison.Ordinal) => "ATTLOG",
        var s when s.Contains("DATA QUERY OPERLOG", StringComparison.Ordinal) => "OPERLOG",
        _ => string.Empty
    };
}

static string ExtractDataPayloadFromDeviceCommandReply(string? replyText)
{
    if (string.IsNullOrWhiteSpace(replyText))
    {
        return string.Empty;
    }

    var lines = replyText
        .Replace("\r\n", "\n")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    if (lines.Length <= 1)
    {
        return string.Empty;
    }

    var payloadLines = lines
        .Skip(1)
        .Where(static line => !line.Equals("OK", StringComparison.OrdinalIgnoreCase))
        .Where(static line => !line.StartsWith("ID=", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    return payloadLines.Length == 0
        ? string.Empty
        : string.Join('\n', payloadLines);
}

static string ExtractInfoReplyDeviceName(string? replyText)
{
    if (string.IsNullOrWhiteSpace(replyText))
    {
        return string.Empty;
    }

    var match = Regex.Match(replyText, @"(?:^|[~&\s])DeviceName=([^,&\r\n]+)", RegexOptions.IgnoreCase);
    if (!match.Success)
    {
        match = Regex.Match(replyText, @"(?:^|[~&\s])DevName=([^,&\r\n]+)", RegexOptions.IgnoreCase);
    }

    if (!match.Success)
    {
        return string.Empty;
    }

    var raw = match.Groups[1].Value.Trim().Replace("+", " ");
    try
    {
        return Uri.UnescapeDataString(raw).Trim();
    }
    catch
    {
        return raw.Trim();
    }
}

static void ApplyOptionsUpdate(AttendanceService service, string serialNumber, string? remoteIpAddress, string body)
{
    var options = ParseOptionPairs(body);
    var name = FirstValue(options, "~DeviceName", "~DevName", "DevName", "DeviceName", "Alias", "TerminalName") ?? serialNumber;
    var model = NormalizeDeviceModelToken(FirstValue(options, "~DeviceModel", "DeviceModel", "~Model", "Model", "DeviceType", "ProductType", "DevType", "TerminalType"));
    var ipAddress = FirstValue(options, "~IPAddress", "~IP", "IPAddress", "IP", "NetIP") ?? remoteIpAddress ?? string.Empty;
    var location = FirstValue(options, "~Area", "Area", "Location", "Addr", "Address") ?? string.Empty;
    EnsureDeviceRegistered(service, serialNumber, ipAddress, name, location, model);

    // Protocol and capability fields from table=options payload.
    var firmwareVersion = FirstValue(options, "FirmwareVersion", "FWV", "FWVersion") ?? string.Empty;
    var pushVer = FirstValue(options, "pushver", "PushVer", "PushProtVer") ?? string.Empty;
    var deviceType = FirstValue(options, "DeviceType", "ProductType", "DevType", "TerminalType") ?? string.Empty;
    var modelToken = NormalizeDeviceModelToken(deviceType);
    var language = ParseIntOption(FirstValue(options, "language", "Language"));
    var multiBioDataSupport = FirstValue(options, "MultiBioDataSupport") ?? string.Empty;
    var multiBioPhotoSupport = FirstValue(options, "MultiBioPhotoSupport") ?? string.Empty;
    var multiBioVersion = FirstValue(options, "MultiBioVersion") ?? string.Empty;
    var maxMultiBioDataCount = FirstValue(options, "MaxMultiBioDataCount") ?? string.Empty;
    var maxMultiBioPhotoCount = FirstValue(options, "MaxMultiBioPhotoCount") ?? string.Empty;

    var fingerFunOn = ParseBoolOption(FirstValue(options, "FingerFunOn"));
    var faceFunOn = ParseBoolOption(FirstValue(options, "FaceFunOn"));
    var visiLightFunOn = ParseBoolOption(FirstValue(options, "VisilightFunOn", "VisilightFun", "VisiLightFunOn", "VisiLightFun"));
    var bioDataFun = ParseBoolOption(FirstValue(options, "BioDataFun"));
    var bioPhotoFun = ParseBoolOption(FirstValue(options, "BioPhotoFun"));

    var faceVersion = FirstValue(options, "FaceVersion", "FaceAlgVer", "FaceAlgoVersion", "FaceAlgorithmVersion") ?? string.Empty;
    var fpVersion = FirstValue(options, "FingerVersion", "FPVersion", "FingerAlgVer", "FingerprintAlgorithmVersion") ?? string.Empty;
    var versionsFromMultiBio = ParseMultiBioVersion(multiBioVersion);
    if (string.IsNullOrWhiteSpace(fpVersion) && versionsFromMultiBio.TryGetValue(1, out var fpFromMultiBio))
    {
        fpVersion = fpFromMultiBio.ToString(CultureInfo.InvariantCulture);
    }
    var supportsFace = (faceFunOn ?? false) ||
                       string.Equals(options.GetValueOrDefault("FaceEnrollThreshold"), "1") ||
                       !string.IsNullOrWhiteSpace(faceVersion);

    // ZKTeco protocol notes:
    // - Type=2 (near-infrared face) FaceVersion is commonly 5/7/8
    // - Type=9 (visible-light face) FaceVersion is commonly 38/58
    // Treat explicit visible-light markers as authoritative, and use FaceVersion only to detect
    // visible-light when it is clearly a visible-light algorithm (>= 30).
    bool isVisibleLight = (visiLightFunOn ?? false) ||
                          (bioDataFun ?? false) ||
                          body.Contains("VLFace", StringComparison.OrdinalIgnoreCase) ||
                          body.Contains("VisibleLight", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(options.GetValueOrDefault("IsSupportVisibleLightFace"), "1", StringComparison.OrdinalIgnoreCase);

    if (double.TryParse(faceVersion, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv))
    {
        if (fv >= 30.0)
        {
            isVisibleLight = true;
        }
    }

    var supportsFp = (fingerFunOn ?? false) ||
                     body.Contains("FPVersion", StringComparison.OrdinalIgnoreCase);
    var parsed = ParseMultiBioDataSupport(multiBioDataSupport);
    var supportsFingerprint = parsed.hasValue ? parsed.supportsFingerprint : supportsFp;
    var supportsFaceNir = parsed.hasValue ? parsed.supportsFaceNir : (supportsFace && !isVisibleLight);
    var supportsFaceVis = parsed.hasValue ? parsed.supportsFaceVis : isVisibleLight;
    var supportsPalm = parsed.hasValue ? parsed.supportsPalm : false;
    var supportsFingerVein = parsed.hasValue ? parsed.supportsFingerVein : false;
    if (string.IsNullOrWhiteSpace(faceVersion))
    {
        if (supportsFaceVis && versionsFromMultiBio.TryGetValue(9, out var faceVisVer))
        {
            faceVersion = faceVisVer.ToString(CultureInfo.InvariantCulture);
        }
        else if (supportsFaceNir && versionsFromMultiBio.TryGetValue(2, out var faceNirVer))
        {
            faceVersion = faceNirVer.ToString(CultureInfo.InvariantCulture);
        }
    }
    var maxUserCount = ParseOptionInt(options, "~MaxUserCount", "MaxUserCount");
    var maxAttLogCount = ParseOptionInt(options, "~MaxAttLogCount", "MaxAttLogCount", "~MaxTransactionCount", "MaxTransactionCount");
    var maxFingerCount = ParseOptionInt(options, "~MaxFingerCount", "MaxFingerCount");
    var maxFaceCount = ParseOptionInt(options, "~MaxFaceCount", "MaxFaceCount");
    var maxPalmCount = ParseOptionInt(options, "~MaxPalmCount", "MaxPalmCount");

    var capabilitiesJson = BuildCapabilitiesJson(
        multiBioDataSupport,
        multiBioPhotoSupport,
        multiBioVersion,
        maxMultiBioDataCount,
        maxMultiBioPhotoCount,
        maxUserCount,
        maxAttLogCount,
        maxFingerCount,
        maxFaceCount,
        maxPalmCount);

    var rawTz = FirstValue(options, "MachineTZ", "TimeZone");
    var timeZone = NormalizeTimeZoneToken(rawTz);

    service.UpdateDeviceProtocolInfo(
        serialNumber,
        firmwareVersion: firmwareVersion,
        deviceType: deviceType,
        pushVer: pushVer,
        language: language,
        multiBioDataSupport: multiBioDataSupport,
        multiBioPhotoSupport: multiBioPhotoSupport,
        multiBioVersion: multiBioVersion,
        maxMultiBioDataCount: maxMultiBioDataCount,
        maxMultiBioPhotoCount: maxMultiBioPhotoCount,
        fingerFunOn: fingerFunOn,
        faceFunOn: faceFunOn,
        visiLightFunOn: visiLightFunOn,
        bioDataFun: bioDataFun,
        bioPhotoFun: bioPhotoFun,
        capabilitiesJson: capabilitiesJson,
        supportsPalm: supportsPalm,
        supportsFingerVein: supportsFingerVein);

    service.UpdateDeviceCapabilities(
        serialNumber,
        supportsFaceVis,
        supportsFaceNir,
        supportsFingerprint,
        faceVersion,
        fpVersion,
        modelToken,
        timeZone);
}

static void ApplyInitOptionsAllUpdate(
    AttendanceService service,
    ConcurrentDictionary<string, DiscoveredDevice> discovered,
    string? serialNumber,
    IQueryCollection query,
    string? remoteIpAddress)
{
    if (string.IsNullOrWhiteSpace(serialNumber))
    {
        return;
    }

    var serial = serialNumber.Trim();
    var deviceType = query.TryGetValue("DeviceType", out var dt) ? dt.ToString().Trim() : string.Empty;
    var pushVer = query.TryGetValue("pushver", out var pv) ? pv.ToString().Trim() : string.Empty;
    var languageToken = query.TryGetValue("language", out var lg) ? lg.ToString().Trim() : string.Empty;
    var language = ParseIntOption(languageToken);
    var firmware = query.TryGetValue("firmware_version", out var fw) ? fw.ToString().Trim() : string.Empty;
    var name = query.TryGetValue("Alias", out var alias) ? alias.ToString().Trim() : serial;
    var location = query.TryGetValue("Area", out var area) ? area.ToString().Trim() : string.Empty;
    var ipAddress = remoteIpAddress ?? string.Empty;
    // DeviceType in the init handshake is the protocol type (att/pid/ins), not the device model.
    // Keep the discovered model empty here so it can be populated by the OPTIONS push.
    var discoveredModel = NormalizeDeviceModelToken(deviceType);

    discovered.AddOrUpdate(
        serial,
        _ => new DiscoveredDevice(serial, name, discoveredModel, ipAddress, location, DateTime.UtcNow, true, 0, 0, 0, 0, -1),
        (_, old) => old with
        {
            Name = string.IsNullOrWhiteSpace(name) ? old.Name : name,
            Model = string.IsNullOrWhiteSpace(discoveredModel) ? old.Model : discoveredModel,
            IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? old.IpAddress : ipAddress,
            Area = string.IsNullOrWhiteSpace(location) ? old.Area : location,
            LastActivityUtc = DateTime.UtcNow,
            IsOnline = true
        });

    EnsureDeviceRegistered(service, serial, remoteIpAddress, name, location);

    service.UpdateDeviceProtocolInfo(
        serial,
        firmwareVersion: firmware,
        deviceType: deviceType,
        pushVer: pushVer,
        language: language);
}

static Employee EnsureEmployeeRecord(PostgresAttendanceRepository repository, string employeeCode, string? fallbackDepartment)
{
    var existing = repository.FindEmployeeByCode(employeeCode);
    if (existing is not null)
    {
        return existing;
    }

    return repository.AddEmployee(new Employee
    {
        EmployeeCode = employeeCode,
        FullName = employeeCode,
        Department = fallbackDepartment?.Trim() ?? string.Empty,
        SyncedAt = DateTime.UtcNow,
        IsActive = true
    });
}

static string ToProfilePrivilegeLabel(UserPrivilege privilege)
{
    return privilege switch
    {
        UserPrivilege.Administrator or UserPrivilege.SuperAdministrator => "Admin",
        UserPrivilege.Registrar => "Enroller",
        _ => "User"
    };
}

static bool IsAdminPrivilegeLabel(string? privilege)
{
    var token = privilege?.Trim() ?? string.Empty;
    return !string.IsNullOrWhiteSpace(token) &&
           token.Contains("admin", StringComparison.OrdinalIgnoreCase);
}

static bool IsEnrollerPrivilegeLabel(string? privilege)
{
    var token = privilege?.Trim() ?? string.Empty;
    return !string.IsNullOrWhiteSpace(token) &&
           (token.Contains("enroll", StringComparison.OrdinalIgnoreCase) ||
            token.Contains("registrar", StringComparison.OrdinalIgnoreCase));
}

static UserPrivilege ResolveConfiguredEmployeePrivilege(string? privilege)
{
    if (IsAdminPrivilegeLabel(privilege))
    {
        return UserPrivilege.SuperAdministrator;
    }

    if (IsEnrollerPrivilegeLabel(privilege))
    {
        return UserPrivilege.Registrar;
    }

    return UserPrivilege.User;
}

static UserPrivilege ResolveDeviceSyncPrivilege(string? profilePrivilege, UserPrivilege employeePrivilege)
{
    if (IsAdminPrivilegeLabel(profilePrivilege))
    {
        return UserPrivilege.SuperAdministrator;
    }

    if (IsEnrollerPrivilegeLabel(profilePrivilege))
    {
        return UserPrivilege.Registrar;
    }

    return employeePrivilege switch
    {
        UserPrivilege.Administrator or UserPrivilege.SuperAdministrator => UserPrivilege.SuperAdministrator,
        UserPrivilege.Registrar => UserPrivilege.Registrar,
        _ => UserPrivilege.User
    };
}

static void UpsertEmployeeProfileMappingFromDevice(
    PersonnelStore store,
    Employee employee,
    string? mappedAreaCode,
    string? mappedDepartmentCode)
{
    if (string.IsNullOrWhiteSpace(employee.EmployeeCode))
    {
        return;
    }

    ProgramHelpers.UpsertEmployeeProfile(
        store,
        employee.EmployeeCode,
        ToProfilePrivilegeLabel(employee.Privilege),
        employee.CardNumber,
        employee.ProfilePhoto,
        employee.MobileNumber,
        employee.Gender,
        null,
        ResolveDevicePassword(null, employee.Password),
        employee.Position,
        mappedAreaCode,
        mappedDepartmentCode,
        verifyMode: employee.VerifyMode);
}

static void ApplyMappedDepartmentAndPosition(Employee employee, string? mappedDepartment, string? mappedPosition)
{
    if (string.IsNullOrWhiteSpace(mappedDepartment))
    {
        return;
    }

    employee.Department = mappedDepartment.Trim();
    if (!string.IsNullOrWhiteSpace(mappedPosition))
    {
        employee.Position = mappedPosition.Trim();
    }
}

static string DescribeDeviceOpLog(string line)
{
    var tokens = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (tokens.Length < 2)
    {
        tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    if (tokens.Length < 2)
    {
        return $"Device operation: {line}";
    }

    var opTypeCodeStr = tokens.Length > 1 ? tokens[1].Trim() : "";
    if (!int.TryParse(opTypeCodeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var opType))
    {
        return $"Device operation: {line}";
    }

    var opWho = tokens.Length > 2 ? tokens[2].Trim() : "";
    var opTime = tokens.Length > 3 ? tokens[3].Trim() : "";
    var val1 = tokens.Length > 4 ? tokens[4].Trim() : "";
    var val2 = tokens.Length > 5 ? tokens[5].Trim() : "";

    var whoText = string.IsNullOrWhiteSpace(opWho) || opWho == "0" ? "Admin" : $"User {opWho}";
    var timeText = string.IsNullOrWhiteSpace(opTime) ? "" : $" at {opTime}";

    return opType switch
    {
        0 => $"Device startup / power on{timeText}",
        1 => $"Device shutdown / power off{timeText}",
        2 => $"Device rebooted{timeText}",
        3 => $"{whoText} accessed device menu{timeText}",
        4 => $"{whoText} exited device menu{timeText}",
        5 => $"Device system settings changed by {whoText}{timeText}",
        6 => $"Device date/time changed by {whoText}{timeText}",
        7 => $"Device communication settings changed by {whoText}{timeText}",
        8 => $"Device access control settings changed by {whoText}{timeText}",
        9 => $"Device alarm/bell settings changed by {whoText}{timeText}",
        10 => $"Device record parameters changed by {whoText}{timeText}",
        11 => $"Device power management changed by {whoText}{timeText}",
        12 => $"Device Mifare card settings changed by {whoText}{timeText}",
        13 => $"Device lock/unlock state changed{timeText}",
        20 => $"User {(!string.IsNullOrWhiteSpace(val1) ? val1 : "enrolled")} registered on device by {whoText}{timeText}",
        21 => $"Fingerprint enrolled for user {(!string.IsNullOrWhiteSpace(val1) ? val1 : "")} by {whoText}{timeText}",
        22 => $"Password set for user {(!string.IsNullOrWhiteSpace(val1) ? val1 : "")} by {whoText}{timeText}",
        23 => $"Card enrolled for user {(!string.IsNullOrWhiteSpace(val1) ? val1 : "")} by {whoText}{timeText}",
        24 => $"User {(!string.IsNullOrWhiteSpace(val1) ? val1 : "")} info modified on device by {whoText}{timeText}",
        25 => $"Fingerprint modified for user {(!string.IsNullOrWhiteSpace(val1) ? val1 : "")} by {whoText}{timeText}",
        26 => $"Password modified for user {(!string.IsNullOrWhiteSpace(val1) ? val1 : "")} by {whoText}{timeText}",
        27 => $"Card modified for user {(!string.IsNullOrWhiteSpace(val1) ? val1 : "")} by {whoText}{timeText}",
        28 => $"User {(!string.IsNullOrWhiteSpace(val1) ? val1 : "")} deleted on device by {whoText}{timeText}",
        29 => $"Fingerprint deleted for user {(!string.IsNullOrWhiteSpace(val1) ? val1 : "")} by {whoText}{timeText}",
        30 => $"Password deleted for user {(!string.IsNullOrWhiteSpace(val1) ? val1 : "")} by {whoText}{timeText}",
        31 => $"Card deleted for user {(!string.IsNullOrWhiteSpace(val1) ? val1 : "")} by {whoText}{timeText}",
        32 => $"Device data cleared by {whoText}{timeText}",
        33 => $"Admin privileges cleared on device by {whoText}{timeText}",
        34 => $"Attendance log cleared on device by {whoText}{timeText}",
        35 => $"Photo records cleared on device by {whoText}{timeText}",
        36 => $"All data wiped on device by {whoText}{timeText}",
        _ => $"Device operation (code {opType}) by {whoText}{timeText}"
    };
}

(int Attendance, int UserInfo, int BioData, int BioPhoto, int FingerTmp, int Face, int UserPic) HandleOperLogPayload(
    AttendanceService service,
    PostgresAttendanceRepository repository,
    PersonnelStore personnelStore,
    string? deviceSerialNumber,
    string payload,
    string? fallbackDepartment,
    string? fallbackPosition,
    string? mappedAreaCode,
    string? mappedDepartmentCode,
    bool includeUserData,
    bool allowUnknownEmployeeAutoCreate)
{
    return repository.RunInTransaction(() =>
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return (0, 0, 0, 0, 0, 0, 0);
        }

        var attendanceLines = new StringBuilder();
        var userInfoLines = new StringBuilder();
        var bioDataLines = new StringBuilder();
        var bioPhotoLines = new StringBuilder();
        var fingerLines = new StringBuilder();
        var faceLines = new StringBuilder();
        var userPicLines = new StringBuilder();
        var opLogLinesCount = 0;

        foreach (var rawLine in payload.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawLine.Length == 0)
            {
                continue;
            }

            var line = rawLine.Trim();
            var upper = line.ToUpperInvariant();
            
            if (upper.StartsWith("OPLOG ", StringComparison.Ordinal) ||
                upper.StartsWith("OPLOG\t", StringComparison.Ordinal) ||
                upper.Equals("OPLOG", StringComparison.Ordinal))
            {
                opLogLinesCount++;
                try
                {
                    var desc = DescribeDeviceOpLog(line);
                    AddLog("INFO", "DeviceAction", $"[{deviceSerialNumber ?? "Device"}] {desc}", line);
                }
                catch
                {
                    AddLog("INFO", "DeviceAction", $"[{deviceSerialNumber ?? "Device"}] Operation log: {line}");
                }
                continue;
            }

            // "USER " = old ICLOCK format; "USERINFO\t" / "USERINFO " = SDK2/SpeedFace format.
            if (upper.StartsWith("USER ", StringComparison.Ordinal) ||
                upper.StartsWith("USERINFO\t", StringComparison.Ordinal) ||
                upper.StartsWith("USERINFO ", StringComparison.Ordinal))
            {
                if (includeUserData)
                {
                    userInfoLines.AppendLine(line);
                }
                continue;
            }

            if (upper.StartsWith("FP ", StringComparison.Ordinal) || upper.StartsWith("FINGERTMP ", StringComparison.Ordinal) || upper.StartsWith("FINGERTMP\t", StringComparison.Ordinal))
            {
                if (includeUserData)
                {
                    fingerLines.AppendLine(line);
                }
                continue;
            }

            if (upper.StartsWith("FACE ", StringComparison.Ordinal) || upper.StartsWith("FACE\t", StringComparison.Ordinal))
            {
                if (includeUserData)
                {
                    faceLines.AppendLine(line);
                }
                continue;
            }

            if (upper.StartsWith("BIODATA ", StringComparison.Ordinal) || upper.StartsWith("BIODATA\t", StringComparison.Ordinal))
            {
                if (includeUserData)
                {
                    bioDataLines.AppendLine(line);
                }
                continue;
            }

            if (upper.StartsWith("BIOPHOTO ", StringComparison.Ordinal) || upper.StartsWith("BIOPHOTO\t", StringComparison.Ordinal))
            {
                if (includeUserData)
                {
                    bioPhotoLines.AppendLine(line);
                }
                continue;
            }

            if (upper.StartsWith("USERPIC ", StringComparison.Ordinal) || upper.StartsWith("USERPIC\t", StringComparison.Ordinal))
            {
                if (includeUserData)
                {
                    userPicLines.AppendLine(line);
                }
                continue;
            }

            attendanceLines.AppendLine(line);
        }

        var attendance = HandleAttendancePayload(service, deviceSerialNumber, attendanceLines.ToString(), allowUnknownEmployeeAutoCreate: false);
        var userInfo = userInfoLines.Length > 0
            ? HandleUserInfoPayload(repository, personnelStore, userInfoLines.ToString(), fallbackDepartment, fallbackPosition, mappedAreaCode, mappedDepartmentCode, allowUnknownEmployeeAutoCreate)
            : 0;
        var bioData = includeUserData
            ? HandleBioDataPayload(repository, personnelStore, bioDataLines.ToString(), fallbackDepartment, fallbackPosition, mappedAreaCode, mappedDepartmentCode)
            : 0;
        var bioPhoto = includeUserData
            ? HandleBioPhotoPayload(repository, personnelStore, bioPhotoLines.ToString(), fallbackDepartment, fallbackPosition, mappedAreaCode, mappedDepartmentCode)
            : 0;
        var fingerTmp = includeUserData
            ? HandleFingerTemplatePayload(repository, personnelStore, fingerLines.ToString(), fallbackDepartment, fallbackPosition, mappedAreaCode, mappedDepartmentCode)
            : 0;
        var face = includeUserData
            ? HandleFaceTemplatePayload(repository, personnelStore, faceLines.ToString(), fallbackDepartment, fallbackPosition, mappedAreaCode, mappedDepartmentCode, service, deviceSerialNumber)
            : 0;
        var userPic = includeUserData
            ? HandleUserPicPayload(repository, personnelStore, userPicLines.ToString(), fallbackDepartment, fallbackPosition, mappedAreaCode, mappedDepartmentCode)
            : 0;

        return (attendance + opLogLinesCount, userInfo, bioData, bioPhoto, fingerTmp, face, userPic);
    });
}

static int HandleUserInfoPayload(
    PostgresAttendanceRepository repository,
    PersonnelStore personnelStore,
    string payload,
    string? fallbackDepartment,
    string? fallbackPosition,
    string? mappedAreaCode,
    string? mappedDepartmentCode,
    bool allowUnknownEmployeeAutoCreate = true)
{
    return repository.RunInTransaction(() =>
    {
        var rows = ParseDataRows(payload, "USERINFO");
        var count = 0;
        foreach (var row in rows)
        {
            var pin = FirstValue(row, "PIN", "Pin", "UserPIN", "UserId", "EnrollNumber", "ID", "Uid", "UID");
            if (string.IsNullOrWhiteSpace(pin))
            {
                continue;
            }

            var normalizedPin = NormalizeDeviceEmployeeCode(pin);
            if (string.IsNullOrWhiteSpace(normalizedPin))
            {
                continue;
            }
            var nameToken = FirstValue(row, "Name", "Username", "UserName", "UName", "FullName");
            var privilegeToken = ParseIntOption(FirstValue(row, "Privilege", "Pri", "Role", "Authority", "Authorize", "PriLevel"));
            var passwordToken = FirstValue(row, "Password", "Passwd", "PWD", "PIN2");
            var normalizedPasswordToken = NormalizePasswordHash(passwordToken);
            var cardToken = FirstValue(row, "Card", "CardNo", "CardNumber", "MainCard", "CardID", "RFID");
            var mobileToken = FirstValue(row, "Phone", "Mobile", "MobileNumber", "Tel");
            var secondaryCardToken = FirstValue(row, "ViceCard", "SecondaryCard", "SubCard");
            // Access-control defaults for fetched users: if the device does not specify an access
            // group, default to Group 1; if it does not specify a verify mode, default to -1 (Apply Group).
            // Personal time periods stay 0000000000000000 (i.e. inherit the group's time periods 1/2/3).
            var groupToken = ParseIntOption(FirstValue(row, "Group", "Grp", "GroupId")) ?? 1;
            var timezoneProfileToken = FirstValue(row, "TZ", "TimeZoneProfile", "TimezoneProfile") ?? "0000000000000000";
            if (string.IsNullOrWhiteSpace(timezoneProfileToken)) timezoneProfileToken = "0000000000000000";
            var verifyModeToken = ParseIntOption(FirstValue(row, "Verify", "VerifyMode")) ?? -1;
            var validFromToken = ParseDateTimeOption(FirstValue(row, "StartDatetime", "ValidFrom", "BeginTime", "BeginDateTime"));
            var validToToken = ParseDateTimeOption(FirstValue(row, "EndDatetime", "ValidTo", "EndTime", "EndDateTime"));
            var expiresEnabledToken = ParseBoolOption(FirstValue(row, "Expires", "ExpiresEnabled"));
            var genderToken = ParseIntOption(FirstValue(row, "Gender"));
            var nationToken = ParseIntOption(FirstValue(row, "Nation"));
            var idNumberToken = FirstValue(row, "IDNum", "IDNumber", "IdNumber");

            var employee = repository.FindEmployeeByCode(normalizedPin);
            if (employee is null)
            {
                if (!allowUnknownEmployeeAutoCreate)
                {
                    continue;
                }
                employee = EnsureEmployeeRecord(repository, normalizedPin, fallbackDepartment);
            }

            if (!string.IsNullOrWhiteSpace(nameToken))
            {
                employee.FullName = nameToken.Trim();
            }

            ApplyMappedDepartmentAndPosition(employee, fallbackDepartment, fallbackPosition);
            if (!string.IsNullOrWhiteSpace(mappedAreaCode))
            {
                employee.Area = mappedAreaCode.Trim();
            }

            if (!string.IsNullOrWhiteSpace(cardToken))
            {
                employee.CardNumber = cardToken.Trim();
            }

            if (!string.IsNullOrWhiteSpace(normalizedPasswordToken))
            {
                employee.Password = normalizedPasswordToken;
            }

            if (!string.IsNullOrWhiteSpace(mobileToken))
            {
                employee.MobileNumber = mobileToken.Trim();
            }

            if (privilegeToken.HasValue)
            {
                employee.Privilege = ResolveDeviceUserPrivilege(privilegeToken.Value);
            }
            employee.VerifyMode = ProgramHelpers.NormalizeEmployeeVerifyMode(verifyModeToken, employee.VerifyMode);

            repository.UpsertUnifiedUser(
                pin: normalizedPin,
                name: nameToken?.Trim(),
                privilege: privilegeToken,
                password: normalizedPasswordToken,
                primaryCard: cardToken?.Trim(),
                secondaryCard: secondaryCardToken?.Trim(),
                groupId: groupToken,
                timezoneProfile: timezoneProfileToken?.Trim(),
                verifyMode: verifyModeToken,
                validFrom: validFromToken,
                validTo: validToToken,
                expiresEnabled: expiresEnabledToken,
                phone: mobileToken?.Trim(),
                gender: genderToken,
                nation: nationToken,
                idNumber: idNumberToken?.Trim());

            employee.SyncedAt = DateTime.UtcNow;
            repository.UpdateEmployee(employee);
            ProgramHelpers.UpsertEmployeeProfile(
                personnelStore,
                employee.EmployeeCode,
                ToProfilePrivilegeLabel(employee.Privilege),
                employee.CardNumber,
                employee.ProfilePhoto,
                employee.MobileNumber,
                employee.Gender,
                null,
                ResolveDevicePassword(passwordToken, employee.Password),
                employee.Position,
                mappedAreaCode,
                mappedDepartmentCode,
                groupId: groupToken,
                timezoneProfile: timezoneProfileToken,
                verifyMode: employee.VerifyMode);
            count++;
        }

        return count;
    });
}

static int HandleBioDataPayload(
    PostgresAttendanceRepository repository,
    PersonnelStore personnelStore,
    string payload,
    string? fallbackDepartment,
    string? fallbackPosition,
    string? mappedAreaCode,
    string? mappedDepartmentCode)
{
    return repository.RunInTransaction(() =>
    {
        var rows = ParseDataRows(payload, "BIODATA");
        var count = 0;
        foreach (var row in rows)
        {
            var pin = FirstValue(row, "PIN", "Pin", "UserPIN", "UserId", "EnrollNumber");
            var type = ParseIntOption(FirstValue(row, "Type", "BioType", "TmpType"));
            var templateData = FirstValue(row, "TemplateData", "Tmp", "Template", "Data", "TmpData");
            if (string.IsNullOrWhiteSpace(pin) || !type.HasValue || string.IsNullOrWhiteSpace(templateData))
            {
                continue;
            }

            var normalizedPin = NormalizeDeviceEmployeeCode(pin);
            if (string.IsNullOrWhiteSpace(normalizedPin))
            {
                continue;
            }
            var normalizedTemplate = templateData.Trim();
            var templateNo = ParseIntOption(FirstValue(row, "No", "FingerId", "FID", "BioNo")) ?? 0;
            var templateIdx = ParseIntOption(FirstValue(row, "Index", "Idx", "TemplateIndex")) ?? 0;

            repository.UpsertBiometricTemplate(
                userPin: normalizedPin,
                type: type.Value,
                no: templateNo,
                idx: templateIdx,
                valid: ParseBoolOption(FirstValue(row, "Valid")) ?? true,
                duress: ParseBoolOption(FirstValue(row, "Duress")) ?? false,
                majorVer: ParseIntOption(FirstValue(row, "MajorVer", "MajorVersion")),
                minorVer: ParseIntOption(FirstValue(row, "MinorVer", "MinorVersion")),
                format: ParseIntOption(FirstValue(row, "Format")),
                templateData: normalizedTemplate);

            var employee = repository.FindEmployeeByCode(normalizedPin);
            if (employee is not null)
            {
                ApplyMappedDepartmentAndPosition(employee, fallbackDepartment, fallbackPosition);
                switch (type.Value)
                {
                    case 9:
                        employee.FaceTemplateVisible = normalizedTemplate;
                        break;
                    case 2:
                        employee.FaceTemplateInfrared = normalizedTemplate;
                        break;
                    default:
                        employee.FingerprintTemplates = MergeFingerprintTemplateJson(employee.FingerprintTemplates, normalizedTemplate, templateNo);
                        break;
                }

                employee.SyncedAt = DateTime.UtcNow;
                repository.UpdateEmployee(employee);
                UpsertEmployeeProfileMappingFromDevice(personnelStore, employee, mappedAreaCode, mappedDepartmentCode);
            }

            count++;
        }

        return count;
    });
}

static int HandleFingerTemplatePayload(
    PostgresAttendanceRepository repository,
    PersonnelStore personnelStore,
    string payload,
    string? fallbackDepartment,
    string? fallbackPosition,
    string? mappedAreaCode,
    string? mappedDepartmentCode)
{
    return repository.RunInTransaction(() =>
    {
        var rows = ParseDataRows(payload, "FINGERTMP");
        var count = 0;
        foreach (var row in rows)
        {
            var pin = FirstValue(row, "PIN", "Pin", "UserPIN", "UserId", "EnrollNumber");
            var templateData = FirstValue(row, "TemplateData", "Tmp", "Template", "Data", "TmpData");
            if (string.IsNullOrWhiteSpace(pin) || string.IsNullOrWhiteSpace(templateData))
            {
                continue;
            }

            var normalizedPin = NormalizeDeviceEmployeeCode(pin);
            if (string.IsNullOrWhiteSpace(normalizedPin))
            {
                continue;
            }

            var normalizedTemplate = templateData.Trim();
            var fingerNo = ParseIntOption(FirstValue(row, "No", "FingerId", "FID", "Index")) ?? 0;
            repository.UpsertBiometricTemplate(
                userPin: normalizedPin,
                type: 1,
                no: fingerNo,
                idx: ParseIntOption(FirstValue(row, "Index", "Idx")) ?? 0,
                valid: ParseBoolOption(FirstValue(row, "Valid")) ?? true,
                duress: ParseBoolOption(FirstValue(row, "Duress")) ?? false,
                majorVer: ParseIntOption(FirstValue(row, "MajorVer", "MajorVersion")),
                minorVer: ParseIntOption(FirstValue(row, "MinorVer", "MinorVersion")),
                format: ParseIntOption(FirstValue(row, "Format")),
                templateData: normalizedTemplate);

            var employee = repository.FindEmployeeByCode(normalizedPin);
            if (employee is not null)
            {
                ApplyMappedDepartmentAndPosition(employee, fallbackDepartment, fallbackPosition);
                employee.FingerprintTemplates = MergeFingerprintTemplateJson(employee.FingerprintTemplates, normalizedTemplate, fingerNo);
                employee.SyncedAt = DateTime.UtcNow;
                repository.UpdateEmployee(employee);
                UpsertEmployeeProfileMappingFromDevice(personnelStore, employee, mappedAreaCode, mappedDepartmentCode);
            }
            count++;
        }

        return count;
    });
}

static int HandleFaceTemplatePayload(
    PostgresAttendanceRepository repository,
    PersonnelStore personnelStore,
    string payload,
    string? fallbackDepartment,
    string? fallbackPosition,
    string? mappedAreaCode,
    string? mappedDepartmentCode,
    AttendanceService? service = null,
    string? serialNumber = null)
{
    var sawInfraredTemplate = false;
    var sawVisibleTemplate = false;
    var result = repository.RunInTransaction(() =>
    {
        var rows = ParseDataRows(payload, "FACE");
        var count = 0;
        foreach (var row in rows)
        {
            var pin = FirstValue(row, "PIN", "Pin", "UserPIN", "UserId", "EnrollNumber");
            var templateData = FirstValue(row, "TemplateData", "Tmp", "Template", "Data", "TmpData", "Content");
            if (string.IsNullOrWhiteSpace(pin) || string.IsNullOrWhiteSpace(templateData))
            {
                continue;
            }

            var normalizedPin = NormalizeDeviceEmployeeCode(pin);
            if (string.IsNullOrWhiteSpace(normalizedPin))
            {
                continue;
            }

            // Legacy FACE table does not include Type; treat it as near-infrared face (Type=2) by default.
            var faceType = ParseIntOption(FirstValue(row, "Type", "BioType")) ?? 2;
            var normalizedTemplate = templateData.Trim();

            repository.UpsertBiometricTemplate(
                userPin: normalizedPin,
                type: faceType,
                no: ParseIntOption(FirstValue(row, "No", "BioNo")) ?? 0,
                idx: ParseIntOption(FirstValue(row, "Index", "Idx")) ?? 0,
                valid: ParseBoolOption(FirstValue(row, "Valid")) ?? true,
                duress: ParseBoolOption(FirstValue(row, "Duress")) ?? false,
                majorVer: ParseIntOption(FirstValue(row, "MajorVer", "MajorVersion")),
                minorVer: ParseIntOption(FirstValue(row, "MinorVer", "MinorVersion")),
                format: ParseIntOption(FirstValue(row, "Format")),
                templateData: normalizedTemplate);

            var employee = repository.FindEmployeeByCode(normalizedPin);
            if (employee is not null)
            {
                ApplyMappedDepartmentAndPosition(employee, fallbackDepartment, fallbackPosition);
                if (faceType == 2)
                {
                    employee.FaceTemplateInfrared = normalizedTemplate;
                    sawInfraredTemplate = true;
                }
                else
                {
                    employee.FaceTemplateVisible = normalizedTemplate;
                    sawVisibleTemplate = true;
                }
                employee.SyncedAt = DateTime.UtcNow;
                repository.UpdateEmployee(employee);
                UpsertEmployeeProfileMappingFromDevice(personnelStore, employee, mappedAreaCode, mappedDepartmentCode);
            }
            count++;
        }

        return count;
    });

    // Uploading a real face template is direct proof this device has working face hardware —
    // stronger evidence than its INFO handshake, which for some low-end/older devices (e.g.
    // ZKTeco MB-series) never reliably reports FaceFunOn. Self-heal the capability flag here so
    // redistributing this employee's template to other devices isn't blocked by a flag that was
    // never set correctly in the first place. Runs after the transaction commits (not inside it)
    // to avoid nesting a second repository call within RunInTransaction.
    if (service is not null && !string.IsNullOrWhiteSpace(serialNumber) && (sawInfraredTemplate || sawVisibleTemplate))
    {
        var deviceForCaps = service.ListDevices().FirstOrDefault(d => string.Equals(d.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase));
        if (deviceForCaps is not null)
        {
            service.UpdateDeviceCapabilities(
                serialNumber,
                visibleLight: sawVisibleTemplate || deviceForCaps.SupportsVisibleLightFace,
                infrared: sawInfraredTemplate || deviceForCaps.SupportsInfraredFace,
                fingerprint: deviceForCaps.SupportsFingerprint);
        }
    }

    return result;
}

static int HandleUserPicPayload(
    PostgresAttendanceRepository repository,
    PersonnelStore personnelStore,
    string payload,
    string? fallbackDepartment,
    string? fallbackPosition,
    string? mappedAreaCode,
    string? mappedDepartmentCode)
{
    return repository.RunInTransaction(() =>
    {
        var rows = ParseDataRows(payload, "USERPIC");
        var count = 0;
        foreach (var row in rows)
        {
            var pin = FirstValue(row, "PIN", "Pin", "UserPIN", "UserId", "EnrollNumber");
            var content = FirstValue(row, "Content", "Photo", "Picture", "Pic", "Data", "Image", "UserPic");
            if (string.IsNullOrWhiteSpace(pin) || string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var normalizedPin = NormalizeDeviceEmployeeCode(pin);
            if (string.IsNullOrWhiteSpace(normalizedPin))
            {
                continue;
            }

            var normalizedContent = content.Trim();
            repository.UpsertBiometricPhoto(
                userPin: normalizedPin,
                type: ParseIntOption(FirstValue(row, "Type", "BioType")) ?? 9,
                no: ParseIntOption(FirstValue(row, "No", "BioNo")) ?? 0,
                idx: ParseIntOption(FirstValue(row, "Index", "Idx")) ?? 0,
                format: ParseIntOption(FirstValue(row, "Format")),
                content: normalizedContent);

            var employee = repository.FindEmployeeByCode(normalizedPin);
            if (employee is not null)
            {
                ApplyMappedDepartmentAndPosition(employee, fallbackDepartment, fallbackPosition);
                employee.ProfilePhoto = NormalizeBiometricPhotoContent(normalizedContent);
                employee.SyncedAt = DateTime.UtcNow;
                repository.UpdateEmployee(employee);
                UpsertEmployeeProfileMappingFromDevice(personnelStore, employee, mappedAreaCode, mappedDepartmentCode);
            }
            count++;
        }

        return count;
    });
}

static int HandleBioPhotoPayload(
    PostgresAttendanceRepository repository,
    PersonnelStore personnelStore,
    string payload,
    string? fallbackDepartment,
    string? fallbackPosition,
    string? mappedAreaCode,
    string? mappedDepartmentCode)
{
    return repository.RunInTransaction(() =>
    {
        var rows = ParseDataRows(payload, "BIOPHOTO");
        var count = 0;
        foreach (var row in rows)
        {
            var pin = FirstValue(row, "PIN", "Pin", "UserPIN", "UserId", "EnrollNumber");
            var type = ParseIntOption(FirstValue(row, "Type", "BioType"));
            var content = FirstValue(row, "Content", "Photo", "Picture", "Pic", "Data", "Image", "UserPic");
            if (string.IsNullOrWhiteSpace(pin) || !type.HasValue || string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var normalizedPin = NormalizeDeviceEmployeeCode(pin);
            if (string.IsNullOrWhiteSpace(normalizedPin))
            {
                continue;
            }
            var normalizedContent = content.Trim();

            repository.UpsertBiometricPhoto(
                userPin: normalizedPin,
                type: type.Value,
                no: ParseIntOption(FirstValue(row, "No", "BioNo")) ?? 0,
                idx: ParseIntOption(FirstValue(row, "Index", "Idx")) ?? 0,
                format: ParseIntOption(FirstValue(row, "Format")),
                content: normalizedContent);

            var employee = repository.FindEmployeeByCode(normalizedPin);
            if (employee is not null)
            {
                ApplyMappedDepartmentAndPosition(employee, fallbackDepartment, fallbackPosition);
                var asImageDataUrl = NormalizeBiometricPhotoContent(normalizedContent);
                if (type.Value == 9)
                {
                    employee.ProfilePhoto = asImageDataUrl;
                }

                employee.SyncedAt = DateTime.UtcNow;
                repository.UpdateEmployee(employee);
                UpsertEmployeeProfileMappingFromDevice(personnelStore, employee, mappedAreaCode, mappedDepartmentCode);
            }

            count++;
        }

        return count;
    });
}

static UserPrivilege ResolveDeviceUserPrivilege(int rawPrivilege)
{
    return rawPrivilege switch
    {
        >= (int)UserPrivilege.SuperAdministrator => UserPrivilege.SuperAdministrator,
        >= (int)UserPrivilege.Administrator => UserPrivilege.Administrator,
        >= (int)UserPrivilege.Registrar => UserPrivilege.Registrar,
        _ => UserPrivilege.User
    };
}

static string DescribePrivilegeLabel(int rawPrivilege)
{
    return rawPrivilege switch
    {
        >= (int)UserPrivilege.Administrator => "Admin",
        >= (int)UserPrivilege.Registrar => "Enroller",
        _ => "User"
    };
}

static string NormalizeGenderToken(int? rawGender, string? fallback)
{
    if (rawGender.HasValue)
    {
        return rawGender.Value switch
        {
            2 => "Female",
            0 => "Other",
            _ => "Male"
        };
    }

    if (!string.IsNullOrWhiteSpace(fallback))
    {
        var token = fallback.Trim();
        if (token.Equals("Female", StringComparison.OrdinalIgnoreCase))
        {
            return "Female";
        }

        if (token.Equals("Other", StringComparison.OrdinalIgnoreCase))
        {
            return "Other";
        }
    }

    return "Male";
}

static string BuildFingerprintTemplateJson(IEnumerable<BiometricTemplateSnapshot> rows)
{
    var payload = rows
        .Where(static row => !string.IsNullOrWhiteSpace(row.TemplateData))
        .GroupBy(row => row.No)
        .Select(group => new FingerprintTemplateEntry(group.Key, group.OrderByDescending(x => x.UpdatedAt).First().TemplateData))
        .OrderBy(static row => row.FingerIndex)
        .ToList();
    return payload.Count == 0 ? string.Empty : JsonSerializer.Serialize(payload);
}

static string MergeFingerprintTemplateJson(string? existingJson, string templateData, int fingerIndex)
{
    var payload = new List<FingerprintTemplateEntry>();
    if (!string.IsNullOrWhiteSpace(existingJson))
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<List<FingerprintTemplateEntry>>(existingJson);
            if (parsed is not null)
            {
                payload.AddRange(parsed.Where(static item => !string.IsNullOrWhiteSpace(item.Template)));
            }
        }
        catch
        {
            payload.Add(new FingerprintTemplateEntry(fingerIndex, existingJson.Trim()));
        }
    }

    payload.RemoveAll(entry => entry.FingerIndex == fingerIndex);
    payload.Add(new FingerprintTemplateEntry(fingerIndex, templateData));
    return JsonSerializer.Serialize(payload.OrderBy(static entry => entry.FingerIndex).ToList());
}

static string RemoveFingerprintFromTemplateJson(string? existingJson, int fingerIndex)
{
    if (string.IsNullOrWhiteSpace(existingJson))
    {
        return string.Empty;
    }

    try
    {
        var parsed = JsonSerializer.Deserialize<List<FingerprintTemplateEntry>>(existingJson);
        if (parsed is null)
        {
            return string.Empty;
        }

        parsed.RemoveAll(entry => entry.FingerIndex == fingerIndex);
        return parsed.Count == 0 ? string.Empty : JsonSerializer.Serialize(parsed.OrderBy(static entry => entry.FingerIndex).ToList());
    }
    catch
    {
        return string.Empty;
    }
}

static string NormalizeBiometricPhotoContent(string content)
{
    if (string.IsNullOrWhiteSpace(content))
    {
        return string.Empty;
    }

    var trimmed = content.Trim();
    if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
    {
        return trimmed;
    }

    return $"data:image/jpeg;base64,{trimmed}";
}

static string NormalizeEmployeeCode(string? value)
    => value?.Trim() ?? string.Empty;

static string NormalizeCardNumber(string? value)
    => value?.Trim() ?? string.Empty;

static string NormalizeEmployeePassword(string? value)
    => value?.Trim() ?? string.Empty;

static string NormalizePictureDataUrlForComparison(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    return NormalizeBiometricPhotoContent(value.Trim());
}

static string EscapeCsvCell(string? value)
{
    var s = value ?? string.Empty;
    if (s.Contains('"'))
    {
        s = s.Replace("\"", "\"\"");
    }

    return (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
        ? $"\"{s}\""
        : s;
}

static string SanitizeArchiveFileName(string? value)
{
    var token = string.IsNullOrWhiteSpace(value) ? "employee" : value.Trim();
    foreach (var c in Path.GetInvalidFileNameChars())
    {
        token = token.Replace(c, '_');
    }

    return token;
}

static bool TryDecodePictureDataUrl(string? value, out byte[] bytes, out string extension)
{
    bytes = [];
    extension = ".jpg";
    var token = value?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(token))
    {
        return false;
    }

    var commaIndex = token.IndexOf(',');
    var header = commaIndex > 0 ? token[..commaIndex] : string.Empty;
    var payload = commaIndex > 0 ? token[(commaIndex + 1)..] : token;
    if (header.Contains("png", StringComparison.OrdinalIgnoreCase))
    {
        extension = ".png";
    }
    else if (header.Contains("gif", StringComparison.OrdinalIgnoreCase))
    {
        extension = ".gif";
    }
    else if (header.Contains("webp", StringComparison.OrdinalIgnoreCase))
    {
        extension = ".webp";
    }
    else if (header.Contains("bmp", StringComparison.OrdinalIgnoreCase))
    {
        extension = ".bmp";
    }
    else
    {
        extension = ".jpg";
    }

    try
    {
        bytes = Convert.FromBase64String(payload);
        return bytes.Length > 0;
    }
    catch
    {
        return false;
    }
}

static string? ValidateEmployeePassword(string? value, bool required)
{
    var token = NormalizeEmployeePassword(value);
    if (string.IsNullOrWhiteSpace(token))
    {
        return required ? "Password is required." : null;
    }

    if (token.Length < 6)
    {
        return "Password must be at least 6 digits.";
    }

    if (!token.All(char.IsDigit))
    {
        return "Password must contain only numbers.";
    }

    return null;
}

static string? ValidateEmployeeUniqueness(
    IEnumerable<Employee> employees,
    PersonnelStore store,
    IReadOnlyList<UnifiedUserSnapshot> unifiedUsers,
    Guid? currentEmployeeId,
    string employeeCode,
    string cardNumber,
    string pictureDataUrl)
{
    var employeeList = employees as IReadOnlyList<Employee> ?? employees.ToList();
    var currentCode = currentEmployeeId.HasValue
        ? employeeList.FirstOrDefault(e => e.Id == currentEmployeeId.Value)?.EmployeeCode?.Trim()
        : null;

    static bool MatchesToken(string? left, string right)
        => string.Equals(left?.Trim(), right, StringComparison.OrdinalIgnoreCase);

    bool IsSameEmployeeCode(string? code)
        => !string.IsNullOrWhiteSpace(currentCode) &&
           string.Equals(code?.Trim(), currentCode, StringComparison.OrdinalIgnoreCase);

    if (!string.IsNullOrWhiteSpace(employeeCode) &&
        (employeeList.Any(e =>
             e.Id != currentEmployeeId &&
             MatchesToken(e.EmployeeCode, employeeCode)) ||
         store.Profiles.Any(p =>
             !IsSameEmployeeCode(p.EmployeeCode) &&
             MatchesToken(p.EmployeeCode, employeeCode)) ||
         unifiedUsers.Any(u =>
             !IsSameEmployeeCode(u.Pin) &&
             MatchesToken(u.Pin, employeeCode))))
    {
        return "Employee already exists with User ID.";
    }

    if (!string.IsNullOrWhiteSpace(cardNumber) &&
        (employeeList.Any(e =>
             e.Id != currentEmployeeId &&
             MatchesToken(e.CardNumber, cardNumber)) ||
         store.Profiles.Any(p =>
             !IsSameEmployeeCode(p.EmployeeCode) &&
             MatchesToken(p.CardNumber, cardNumber)) ||
         unifiedUsers.Any(u =>
             !IsSameEmployeeCode(u.Pin) &&
             (MatchesToken(u.PrimaryCard, cardNumber) || MatchesToken(u.SecondaryCard, cardNumber)))))
    {
        return "Card number already exists.";
    }

    if (!string.IsNullOrWhiteSpace(pictureDataUrl) &&
        (employeeList.Any(e =>
             e.Id != currentEmployeeId &&
             NormalizePictureDataUrlForComparison(e.ProfilePhoto) == pictureDataUrl) ||
         store.Profiles.Any(p =>
             !IsSameEmployeeCode(p.EmployeeCode) &&
             NormalizePictureDataUrlForComparison(p.PictureDataUrl) == pictureDataUrl)))
    {
        return "Employee photo already exists.";
    }

    return null;
}

static bool IsLikelyPhotoPayload(string? content)
{
    return !string.IsNullOrWhiteSpace(content) &&
           content.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase);
}

static List<Dictionary<string, string>> ParseDataRows(string payload, string tableName)
{
    static Dictionary<string, string> BuildPositionalMap(string[] parts, string[] keys, int offset)
    {
        var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < keys.Length; i++)
        {
            var index = i + offset;
            if (index < 0 || index >= parts.Length)
            {
                break;
            }

            mapped[keys[i]] = parts[index];
        }

        return mapped;
    }

    static bool LooksNumericToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return token.Trim().All(char.IsDigit);
    }

    static bool IsLikelyShiftedUserInfoRow(Dictionary<string, string> mapped)
    {
        var pin = mapped.TryGetValue("PIN", out var pinValue) ? pinValue : null;
        var name = mapped.TryGetValue("Name", out var nameValue) ? nameValue : null;
        var privilege = mapped.TryGetValue("Privilege", out var privilegeValue) ? privilegeValue : null;
        // Some firmware prepends an index before PIN:
        // index, pin, name, privilege, ...
        // Without offset correction this becomes PIN=index, Name=pin, Privilege=name.
        return LooksNumericToken(pin) &&
               LooksNumericToken(name) &&
               !int.TryParse(privilege, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }

    static bool LooksDateToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return DateTime.TryParse(token.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _);
    }

    static Dictionary<string, string> ParseKeyValueLine(string line, string normalizedTableName)
    {
        var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(line))
        {
            return mapped;
        }

        var normalizedLine = line.Trim();
        // Strip table-name prefix whether followed by space or tab (SDK2/SpeedFace uses tab).
        if (normalizedLine.StartsWith(normalizedTableName + " ", StringComparison.OrdinalIgnoreCase) ||
            normalizedLine.StartsWith(normalizedTableName + "\t", StringComparison.OrdinalIgnoreCase))
        {
            normalizedLine = normalizedLine[(normalizedTableName.Length + 1)..].TrimStart();
        }

        var matches = Regex.Matches(
            normalizedLine,
            @"(?<key>[A-Za-z][A-Za-z0-9_]*)=(?<value>.*?)(?=(?:\s+[A-Za-z][A-Za-z0-9_]*=)|$)",
            RegexOptions.CultureInvariant);

        foreach (Match match in matches)
        {
            var key = match.Groups["key"].Value.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var value = match.Groups["value"].Value.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            mapped[key] = value;
        }

        return mapped;
    }

    static int EvaluateUserInfoPositionalScore(Dictionary<string, string> mapped)
    {
        var score = 0;
        if (mapped.TryGetValue("PIN", out var pin) && !string.IsNullOrWhiteSpace(pin))
        {
            score += 2;
            if (LooksNumericToken(pin))
            {
                score += 1;
            }
        }

        if (mapped.TryGetValue("Name", out var name) && !string.IsNullOrWhiteSpace(name))
        {
            score += 2;
        }

        if (mapped.TryGetValue("Privilege", out var privilege) &&
            int.TryParse(privilege, NumberStyles.Integer, CultureInfo.InvariantCulture, out var priValue) &&
            priValue is >= 0 and <= 15)
        {
            score += 3;
        }

        if (mapped.TryGetValue("ValidFrom", out var validFrom) && LooksDateToken(validFrom))
        {
            score += 2;
        }

        if (mapped.TryGetValue("ValidTo", out var validTo) && LooksDateToken(validTo))
        {
            score += 2;
        }

        if (mapped.TryGetValue("Expires", out var expires) &&
            (expires == "0" || expires == "1" || bool.TryParse(expires, out _)))
        {
            score += 1;
        }

        return score;
    }

    var rows = new List<Dictionary<string, string>>();
    if (string.IsNullOrWhiteSpace(payload))
    {
        return rows;
    }

    var lines = payload
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(static line => !line.StartsWith("DATA ", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    var normalizedTableName = tableName.Trim().ToUpperInvariant();

    foreach (var line in lines)
    {
        if (line.Contains('='))
        {
            var mapped = ParseKeyValueLine(line, normalizedTableName);

            if (mapped.Count > 0)
            {
                rows.Add(mapped);
            }
            continue;
        }

        var parts = line.Split(new[] { '\t', ',' }, StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            continue;
        }

        var keys = tableName.ToUpperInvariant() switch
        {
            // Positional USERINFO is common and deterministic enough to map.
            "USERINFO" => new[] { "PIN", "Name", "Privilege", "Password", "Card", "Group", "TZ", "VerifyMode", "ValidFrom", "ValidTo", "Expires", "Phone", "Gender", "Nation", "IDNumber", "SecondaryCard" },
            // For BIODATA/BIOPHOTO/FACE/FINGERTMP/USERPIC, positional order varies by firmware.
            // Requiring key=value rows avoids false PIN parsing (for example Type=9 becoming fake user 9).
            _ => Array.Empty<string>()
        };

        var mappedPositional = BuildPositionalMap(parts, keys, offset: 0);
        if (tableName.Equals("USERINFO", StringComparison.OrdinalIgnoreCase) && parts.Length >= keys.Length + 1)
        {
            var shifted = BuildPositionalMap(parts, keys, offset: 1);
            var baseScore = EvaluateUserInfoPositionalScore(mappedPositional);
            var shiftedScore = EvaluateUserInfoPositionalScore(shifted);

            if (shiftedScore > baseScore || (shiftedScore == baseScore && IsLikelyShiftedUserInfoRow(mappedPositional)))
            {
                mappedPositional = shifted;
            }
        }

        if (mappedPositional.Count > 0)
        {
            rows.Add(mappedPositional);
        }
    }

    return rows;
}

static string BuildCapabilitiesJson(
    string multiBioDataSupport,
    string multiBioPhotoSupport,
    string multiBioVersion,
    string maxMultiBioDataCount,
    string maxMultiBioPhotoCount,
    int maxUserCount = -1,
    int maxAttLogCount = -1,
    int maxFingerCount = -1,
    int maxFaceCount = -1,
    int maxPalmCount = -1)
{
    var templateTypes = ParseSupportedTypeList(multiBioDataSupport);
    var photoTypes = ParseSupportedTypeList(multiBioPhotoSupport);
    var versions = ParseIndexedIntegerMap(multiBioVersion);
    var maxTemplates = ParseIndexedIntegerMap(maxMultiBioDataCount);
    var maxPhotos = ParseIndexedIntegerMap(maxMultiBioPhotoCount);

    var payload = new
    {
        raw = new
        {
            multi_bio_data_support = multiBioDataSupport,
            multi_bio_photo_support = multiBioPhotoSupport,
            multi_bio_version = multiBioVersion,
            max_multi_bio_data_count = maxMultiBioDataCount,
            max_multi_bio_photo_count = maxMultiBioPhotoCount
        },
        supported_templates = templateTypes,
        supported_photos = photoTypes,
        template_versions = versions,
        max_templates = maxTemplates,
        max_photos = maxPhotos,
        max_user_qty = maxUserCount > 0 ? maxUserCount : 10000,
        max_att_log_qty = maxAttLogCount > 0 ? maxAttLogCount : 200000,
        max_fp_qty = maxFingerCount > 0 ? maxFingerCount : (maxTemplates.TryGetValue("1", out var f) ? f : 10000),
        max_face_qty = maxFaceCount > 0 ? maxFaceCount : (maxTemplates.TryGetValue("9", out var fa) ? fa : (maxTemplates.TryGetValue("2", out var fa2) ? fa2 : 6000)),
        max_palm_qty = maxPalmCount > 0 ? maxPalmCount : (maxTemplates.TryGetValue("8", out var p) ? p : (maxTemplates.TryGetValue("10", out var p2) ? p2 : 3000))
    };

    return JsonSerializer.Serialize(payload);
}

static int[] ParseSupportedTypeList(string raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return Array.Empty<int>();
    }

    return raw
        .Split(':', StringSplitOptions.TrimEntries)
        .Select((token, index) => new { token, index })
        .Where(static item => string.Equals(item.token, "1", StringComparison.Ordinal))
        .Select(static item => item.index)
        .ToArray();
}

static SortedDictionary<string, int> ParseIndexedIntegerMap(string raw)
{
    var result = new SortedDictionary<string, int>(StringComparer.Ordinal);
    if (string.IsNullOrWhiteSpace(raw))
    {
        return result;
    }

    var parts = raw.Split(':', StringSplitOptions.TrimEntries);
    for (var i = 0; i < parts.Length; i++)
    {
        if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            continue;
        }

        if (value > 0)
        {
            result[i.ToString(CultureInfo.InvariantCulture)] = value;
        }
    }

    return result;
}

static Dictionary<string, string> ParseOptionPairs(string body)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (string.IsNullOrWhiteSpace(body))
    {
        return result;
    }

    foreach (var pair in body.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var idx = pair.IndexOf('=');
        if (idx <= 0 || idx == pair.Length - 1)
        {
            continue;
        }

        result[pair[..idx].Trim()] = pair[(idx + 1)..].Trim();
    }

    return result;
}

static string? FirstValue(IReadOnlyDictionary<string, string> source, params string[] keys)
{
    foreach (var key in keys)
    {
        if (source.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }
    }

    return null;
}

static string? NormalizeTimeZoneToken(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return null;
    }

    var token = raw.Trim();
    if (Regex.IsMatch(token, @"^[+-]\d{4}$"))
    {
        var hh = token[1..3];
        var mm = token[3..5];
        return $"{token[0]}{hh}:{mm}";
    }

    if (token.StartsWith("DATA QUERY USERINFO", StringComparison.OrdinalIgnoreCase) &&
        !token.Contains("PIN=", StringComparison.OrdinalIgnoreCase))
    {
        return "DATA QUERY USERINFO PIN=*";
    }

    return token;
}

static string NormalizeDeviceModelToken(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return string.Empty;
    }

    var token = raw.Trim();
    if (string.Equals(token, "att", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(token, "pid", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(token, "ins", StringComparison.OrdinalIgnoreCase))
    {
        return string.Empty;
    }

    return token;
}

static void EnsureDeviceRegistered(
    AttendanceService service,
    string serialNumber,
    string? ipAddress,
    string? name = null,
    string? location = null,
    string? model = null)
{
    if (string.IsNullOrWhiteSpace(serialNumber))
    {
        return;
    }

    var serial = serialNumber.Trim();
    var existing = service.ListDevices().FirstOrDefault(d =>
        string.Equals(d.SerialNumber, serial, StringComparison.OrdinalIgnoreCase));
    if (existing is null)
    {
        return;
    }

    // Only update name if current DB name is empty or just the serial number (i.e. not customized)
    var trimmedName = name?.Trim();
    var isRealNameFromPush = !string.IsNullOrWhiteSpace(trimmedName) &&
                             !string.Equals(trimmedName, serial, StringComparison.OrdinalIgnoreCase);
    
    bool nameNeedsInitialization = string.IsNullOrWhiteSpace(existing.Name) || 
                                   string.Equals(existing.Name, serial, StringComparison.OrdinalIgnoreCase);
    var normalizedName = (isRealNameFromPush && nameNeedsInitialization) ? trimmedName : null;

    var normalizedIp = string.IsNullOrWhiteSpace(ipAddress) ? null : ipAddress.Trim();
    
    // Only update model if current DB model is empty or "None"
    var trimmedModelFromPush = model?.Trim();
    bool modelNeedsInitialization = string.IsNullOrWhiteSpace(existing.Model) ||
                                    string.Equals(existing.Model, "None", StringComparison.OrdinalIgnoreCase);

    string? normalizedModel = null;
    if (modelNeedsInitialization && !string.IsNullOrWhiteSpace(trimmedModelFromPush))
    {
        normalizedModel = trimmedModelFromPush;
    }

    if (normalizedName is not null || normalizedIp is not null || normalizedModel is not null)
    {
        service.UpdateDeviceInfo(existing.Id, normalizedName, normalizedModel, normalizedIp, null);
    }

    service.TouchDevice(serial, normalizedIp);
}

static bool IsDefaultArea(string? areaCode, string defaultAreaCode)
{
    return !string.IsNullOrWhiteSpace(areaCode) &&
           string.Equals(areaCode.Trim(), defaultAreaCode, StringComparison.OrdinalIgnoreCase);
}

// Access-control verification modes are pushed straight to devices by
// ConfigSyncService.SyncAccessControlAsync (per-user USERINFO Verify). No
// employee-record mutation is required here, so nothing needs re-saving.
static bool ApplyAccessControlPrivilegesToEmployees(
    string rawJson,
    PostgresAttendanceRepository repository,
    PersonnelStore personnelStore)
{
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("privileges", out var privs) || privs.ValueKind != System.Text.Json.JsonValueKind.Array)
            return false;

        var changed = false;
        foreach (var priv in privs.EnumerateArray())
        {
            // Resolve group number from groupId
            var groupId = priv.TryGetProperty("groupId", out var gid) ? gid.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(groupId)) continue;

            // Parse groupNo from the groups array
            int groupNo = 1;
            if (root.TryGetProperty("groups", out var groups))
            {
                foreach (var g in groups.EnumerateArray())
                {
                    if (g.TryGetProperty("id", out var gIdProp) && gIdProp.GetString() == groupId)
                    {
                        if (g.TryGetProperty("groupNo", out var gnProp) && int.TryParse(gnProp.GetString(), out var gn))
                            groupNo = gn;
                        break;
                    }
                }
            }

            var verMode = priv.TryGetProperty("verificationMode", out var vm) ? vm.GetString() ?? "-1" : "-1";
            int verifyMode = int.TryParse(verMode, out var vi) ? vi : -1;

            if (!priv.TryGetProperty("employeeIds", out var empIds) || empIds.ValueKind != System.Text.Json.JsonValueKind.Array)
                continue;

            foreach (var empId in empIds.EnumerateArray())
            {
                var code = empId.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(code)) continue;

                // Update unified user table so next USERINFO sync uses correct group
                repository.UpsertUnifiedUser(
                    pin: code, name: null, privilege: null, password: null,
                    primaryCard: null, secondaryCard: null,
                    groupId: groupNo, timezoneProfile: null,
                    verifyMode: verifyMode,
                    validFrom: null, validTo: null, expiresEnabled: null,
                    phone: null, gender: null, nation: null, idNumber: null);

                // Also update personnel store profile
                var profile = personnelStore.Profiles.FirstOrDefault(p =>
                    string.Equals(p.EmployeeCode, code, StringComparison.OrdinalIgnoreCase));
                if (profile != null)
                {
                    personnelStore.Profiles.RemoveAll(p => string.Equals(p.EmployeeCode, code, StringComparison.OrdinalIgnoreCase));
                    personnelStore.Profiles.Add(profile with { GroupId = groupNo, VerifyMode = verifyMode });
                }
                changed = true;
            }
        }
        return changed;
    }
    catch { return false; }
}

static AreaCascadeDeleteResult DeleteAreaCascade(
    PersonnelStore store,
    PostgresAttendanceRepository repository,
    AttendanceService service,
    PersonnelArea target,
    string personnelPath,
    string connectionString,
    string defaultAreaCode,
    string defaultAreaName,
    string defaultDepartmentCode,
    string defaultDepartmentName)
{
    var result = new AreaCascadeDeleteResult();
    var targetAreaCode = target.Code?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(targetAreaCode) || IsDefaultArea(targetAreaCode, defaultAreaCode))
    {
        return result;
    }

    var departmentsInArea = store.Departments
        .Where(d => string.Equals(d.AreaCode, targetAreaCode, StringComparison.OrdinalIgnoreCase))
        .ToList();
    var departmentCodes = departmentsInArea
        .Select(d => d.Code?.Trim() ?? string.Empty)
        .Where(code => !string.IsNullOrWhiteSpace(code))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var departmentNames = departmentsInArea
        .Select(d => d.Name?.Trim() ?? string.Empty)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var employees = repository.GetEmployees().ToList();
    var employeeCodesToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var employeeIdsToDelete = new HashSet<Guid>();
    foreach (var employee in employees)
    {
        var profile = store.Profiles.FirstOrDefault(p =>
            string.Equals(p.EmployeeCode, employee.EmployeeCode, StringComparison.OrdinalIgnoreCase));
        var resolvedAssignments = GetResolvedEmployeeAssignments(
            store,
            employee,
            profile,
            defaultAreaCode,
            defaultAreaName,
            defaultDepartmentCode,
            defaultDepartmentName);
        var belongsToArea =
            resolvedAssignments.Any(x => string.Equals(x.Area.Code, targetAreaCode, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(employee.Area?.Trim() ?? string.Empty, target.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(employee.Area?.Trim() ?? string.Empty, targetAreaCode, StringComparison.OrdinalIgnoreCase) ||
            departmentNames.Contains(employee.Department?.Trim() ?? string.Empty);

        if (!belongsToArea)
        {
            continue;
        }

        employeeIdsToDelete.Add(employee.Id);
        var employeeCode = employee.EmployeeCode?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(employeeCode))
        {
            employeeCodesToDelete.Add(employeeCode);
        }
    }

    var allDeviceSerials = service.ListDevices()
        .Select(d => d.SerialNumber?.Trim())
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Cast<string>()
        .ToList();
    foreach (var serial in allDeviceSerials)
    {
        var queuedCheck = false;
        foreach (var code in employeeCodesToDelete)
        {
            var safeCode = SanitizeCommandValue(code);
            if (string.IsNullOrWhiteSpace(safeCode))
            {
                continue;
            }

            if (!queuedCheck)
            {
                service.QueueDeviceCommand(serial, "CHECK");
                queuedCheck = true;
            }

            service.QueueDeviceCommand(serial, $"DATA DELETE USERINFO PIN={safeCode}");
            result.QueuedDeviceDeletes++;
        }
    }

    foreach (var employeeId in employeeIdsToDelete)
    {
        if (repository.DeleteEmployee(employeeId))
        {
            result.EmployeesDeleted++;
        }
    }

    foreach (var employeeCode in employeeCodesToDelete)
    {
        result.DeletedArtifactRows += repository.DeleteEmployeeArtifactsByCode(employeeCode);
        result.DeletedArtifactRows += DeletePersonnelArtifactsInPostgres(connectionString, employeeCode);
    }

    if (employeeCodesToDelete.Count > 0)
    {
        store.Profiles.RemoveAll(p => employeeCodesToDelete.Contains(p.EmployeeCode));
    }

    var deviceSerialsToDelete = store.DeviceAssignments
        .Where(x =>
            string.Equals(x.AreaCode, targetAreaCode, StringComparison.OrdinalIgnoreCase) ||
            departmentCodes.Contains(x.DepartmentCode?.Trim() ?? string.Empty))
        .Select(x => x.DeviceSerial?.Trim() ?? string.Empty)
        .Where(serial => !string.IsNullOrWhiteSpace(serial))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    foreach (var device in service.ListDevices().Where(d => deviceSerialsToDelete.Contains(d.SerialNumber?.Trim() ?? string.Empty)).ToList())
    {
        if (service.DeleteDevice(device.Id))
        {
            result.DevicesDeleted++;
        }
    }

    store.DeviceAssignments.RemoveAll(x =>
        string.Equals(x.AreaCode, targetAreaCode, StringComparison.OrdinalIgnoreCase) ||
        departmentCodes.Contains(x.DepartmentCode?.Trim() ?? string.Empty));
    result.DepartmentsDeleted = store.Departments.RemoveAll(d =>
        string.Equals(d.AreaCode, targetAreaCode, StringComparison.OrdinalIgnoreCase));
    result.PositionsDeleted = store.Positions.RemoveAll(p => departmentNames.Contains(p.Department?.Trim() ?? string.Empty));
    result.AreasDeleted = store.Areas.RemoveAll(a => a.Id == target.Id);

    SavePersonnelStoreAndSyncDb(store, personnelPath, connectionString);
    return result;
}

static bool IsDefaultDepartment(PersonnelDepartment department, string defaultAreaCode, string defaultDepartmentCode)
{
    return string.Equals(department.Code, defaultDepartmentCode, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(department.AreaCode, defaultAreaCode, StringComparison.OrdinalIgnoreCase);
}

static void EnsurePersonnelSetupDefaults(
    PersonnelStore store,
    string defaultAreaCode,
    string defaultAreaName,
    string defaultDepartmentCode,
    string defaultDepartmentName)
{
    var changed = false;

    var defaultArea = store.Areas.FirstOrDefault(a =>
        string.Equals(a.Code, defaultAreaCode, StringComparison.OrdinalIgnoreCase));
    if (defaultArea is null)
    {
        defaultArea = new PersonnelArea(Guid.NewGuid(), defaultAreaCode, defaultAreaName, "System default area");
        store.Areas.Add(defaultArea);
        changed = true;
    }
    else if (!string.Equals(defaultArea.Name, defaultAreaName, StringComparison.OrdinalIgnoreCase))
    {
        var idx = store.Areas.FindIndex(a => a.Id == defaultArea.Id);
        if (idx >= 0)
        {
            store.Areas[idx] = defaultArea with { Name = defaultAreaName };
            defaultArea = store.Areas[idx];
            changed = true;
        }
    }

    for (var i = 0; i < store.Departments.Count; i++)
    {
        var department = store.Departments[i];
        if (string.IsNullOrWhiteSpace(department.AreaCode))
        {
            store.Departments[i] = department with { AreaCode = defaultAreaCode };
            changed = true;
        }
    }

    var defaultDepartment = store.Departments.FirstOrDefault(d =>
        string.Equals(d.Code, defaultDepartmentCode, StringComparison.OrdinalIgnoreCase));
    if (defaultDepartment is null)
    {
        store.Departments.Add(new PersonnelDepartment(
            Guid.NewGuid(),
            defaultDepartmentCode,
            defaultDepartmentName,
            string.Empty,
            string.Empty,
            defaultAreaCode));
        changed = true;
    }
    else if (!string.Equals(defaultDepartment.Name, defaultDepartmentName, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(defaultDepartment.AreaCode, defaultAreaCode, StringComparison.OrdinalIgnoreCase))
    {
        var idx = store.Departments.FindIndex(d => d.Id == defaultDepartment.Id);
        if (idx >= 0)
        {
            store.Departments[idx] = defaultDepartment with
            {
                Name = defaultDepartmentName,
                AreaCode = defaultAreaCode
            };
            changed = true;
        }
    }

    if (changed)
    {
        NormalizeEmployeeAssignments(store);
    }
}

static void EnsureDeviceAssignmentsForExistingDevices(
    AttendanceService service,
    PersonnelStore store,
    string defaultAreaCode,
    string defaultAreaName,
    string defaultDepartmentCode,
    string defaultDepartmentName)
{
    var changed = false;
    var devices = service.ListDevices().ToList();
    foreach (var device in devices)
    {
        var setup = ResolveDeviceSetupForSerial(
            store,
            device.SerialNumber,
            device.Location,
            defaultAreaCode,
            defaultAreaName,
            defaultDepartmentCode,
            defaultDepartmentName);

        var existing = store.DeviceAssignments.FirstOrDefault(x =>
            string.Equals(x.DeviceSerial, device.SerialNumber, StringComparison.OrdinalIgnoreCase));
        if (existing is null ||
            !string.Equals(existing.AreaCode, setup.Area.Code, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existing.DepartmentCode, setup.Department.Code, StringComparison.OrdinalIgnoreCase))
        {
            UpsertDeviceAssignment(store, device.SerialNumber, setup.Area.Code, setup.Department.Code);
            changed = true;
        }

        if (!string.Equals(device.Location, setup.Area.Name, StringComparison.OrdinalIgnoreCase))
        {
            service.UpdateDeviceInfo(device.Id, null, null, null, setup.Area.Name);
        }
    }

    if (changed)
    {
        NormalizeEmployeeAssignments(store);
    }
}


static (PersonnelArea Area, PersonnelDepartment Department) ResolveDeviceSetup(
    PersonnelStore store,
    string? areaCodeInput,
    string? areaNameInput,
    string? departmentCodeInput,
    string defaultAreaCode,
    string defaultAreaName,
    string defaultDepartmentCode,
    string defaultDepartmentName)
{
    var area = store.Areas.FirstOrDefault(a =>
                   !string.IsNullOrWhiteSpace(areaCodeInput) &&
                   string.Equals(a.Code, areaCodeInput.Trim(), StringComparison.OrdinalIgnoreCase)) ??
               store.Areas.FirstOrDefault(a =>
                   !string.IsNullOrWhiteSpace(areaNameInput) &&
                   string.Equals(a.Name, areaNameInput.Trim(), StringComparison.OrdinalIgnoreCase)) ??
               store.Areas.FirstOrDefault(a => string.Equals(a.Code, defaultAreaCode, StringComparison.OrdinalIgnoreCase)) ??
               new PersonnelArea(Guid.Empty, defaultAreaCode, defaultAreaName, string.Empty);

    var department = store.Departments.FirstOrDefault(d =>
                         !string.IsNullOrWhiteSpace(departmentCodeInput) &&
                         string.Equals(d.Code, departmentCodeInput.Trim(), StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(d.AreaCode, area.Code, StringComparison.OrdinalIgnoreCase)) ??
                     store.Departments
                         .Where(d => string.Equals(d.AreaCode, area.Code, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(d => IsDefaultDepartment(d, defaultAreaCode, defaultDepartmentCode) ? 0 : 1)
                         .ThenBy(d => d.Code, StringComparer.OrdinalIgnoreCase)
                         .FirstOrDefault() ??
                     store.Departments.FirstOrDefault(d => IsDefaultDepartment(d, defaultAreaCode, defaultDepartmentCode)) ??
                     new PersonnelDepartment(Guid.Empty, defaultDepartmentCode, defaultDepartmentName, string.Empty, string.Empty, defaultAreaCode);

    return (area, department);
}

static PersonnelArea ResolveAreaFromCode(
    PersonnelStore store,
    string? areaCode,
    string defaultAreaCode,
    string defaultAreaName)
{
    var token = areaCode?.Trim() ?? string.Empty;
    if (!string.IsNullOrWhiteSpace(token))
    {
        var byCode = store.Areas.FirstOrDefault(a => string.Equals(a.Code, token, StringComparison.OrdinalIgnoreCase));
        if (byCode is not null)
        {
            return byCode;
        }
    }

    return store.Areas.FirstOrDefault(a => string.Equals(a.Code, defaultAreaCode, StringComparison.OrdinalIgnoreCase))
        ?? new PersonnelArea(Guid.Empty, defaultAreaCode, defaultAreaName, string.Empty);
}


static PersonnelDepartment ResolveDepartmentFromSetup(

    PersonnelStore store,
    string? departmentInput,
    string defaultAreaCode,
    string defaultAreaName,
    string defaultDepartmentCode,
    string defaultDepartmentName)
{
    var token = departmentInput?.Trim() ?? string.Empty;
    if (!string.IsNullOrWhiteSpace(token))
    {
        var byCode = store.Departments.FirstOrDefault(d => string.Equals(d.Code, token, StringComparison.OrdinalIgnoreCase));
        if (byCode is not null)
        {
            return byCode;
        }

        var byName = store.Departments.FirstOrDefault(d => string.Equals(d.Name, token, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName;
        }
    }

    return ResolveDeviceSetup(
        store,
        null,
        null,
        null,
        defaultAreaCode,
        defaultAreaName,
        defaultDepartmentCode,
        defaultDepartmentName).Department;
}

static string ResolveDepartmentNameFromSetup(
    PersonnelStore store,
    string? departmentInput,
    string defaultAreaCode,
    string defaultAreaName,
    string defaultDepartmentCode,
    string defaultDepartmentName)
{
    return ResolveDepartmentFromSetup(
        store,
        departmentInput,
        defaultAreaCode,
        defaultAreaName,
        defaultDepartmentCode,
        defaultDepartmentName).Name;
}

static string ResolvePositionNameFromSetup(
    PersonnelStore store,
    string? positionInput,
    string? departmentName)
{
    var targetDepartment = departmentName?.Trim() ?? string.Empty;
    var token = positionInput?.Trim() ?? string.Empty;
    if (!string.IsNullOrWhiteSpace(token))
    {
        var byName = store.Positions.FirstOrDefault(p =>
            string.Equals(p.Name, token, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(targetDepartment) || string.Equals(p.Department, targetDepartment, StringComparison.OrdinalIgnoreCase)));
        if (byName is not null)
        {
            return byName.Name;
        }

        var byCode = store.Positions.FirstOrDefault(p =>
            string.Equals(p.Code, token, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(targetDepartment) || string.Equals(p.Department, targetDepartment, StringComparison.OrdinalIgnoreCase)));
        if (byCode is not null)
        {
            return byCode.Name;
        }
    }

    if (string.IsNullOrWhiteSpace(targetDepartment))
    {
        return string.Empty;
    }

    return store.Positions
        .Where(p => string.Equals(p.Department, targetDepartment, StringComparison.OrdinalIgnoreCase))
        .OrderBy(p => p.Code, StringComparer.OrdinalIgnoreCase)
        .Select(p => p.Name)
        .FirstOrDefault() ?? string.Empty;
}

static List<PersonnelEmployeeAssignment> NormalizeEmployeeAssignmentList(IEnumerable<PersonnelEmployeeAssignment> assignments)
{
    var result = new List<PersonnelEmployeeAssignment>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var assignment in assignments)
    {
        var departmentCode = assignment.DepartmentCode?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(departmentCode) || !seen.Add(departmentCode))
        {
            continue;
        }

        result.Add(new PersonnelEmployeeAssignment(
            assignment.AreaCode?.Trim() ?? string.Empty,
            departmentCode));
    }

    return result;
}

static void NormalizeEmployeeAssignments(PersonnelStore store)
{
    for (var i = 0; i < store.Profiles.Count; i++)
    {
        var profile = store.Profiles[i];
        var normalizedAssignments = NormalizeEmployeeAssignmentList((profile.Assignments ?? [])
            .Concat(!string.IsNullOrWhiteSpace(profile.DepartmentCode)
                ? [new PersonnelEmployeeAssignment(profile.AreaCode, profile.DepartmentCode)]
                : []));
        var primaryAssignment = normalizedAssignments.FirstOrDefault();
        store.Profiles[i] = profile with
        {
            AreaCode = primaryAssignment?.AreaCode ?? profile.AreaCode,
            DepartmentCode = primaryAssignment?.DepartmentCode ?? profile.DepartmentCode,
            Assignments = normalizedAssignments
        };
    }
}

static List<PersonnelEmployeeAssignment> ResolveEmployeeAssignmentsFromInputs(
    PersonnelStore store,
    IEnumerable<string?>? departmentInputs,
    string defaultAreaCode,
    string defaultAreaName,
    string defaultDepartmentCode,
    string defaultDepartmentName)
{
    var assignments = new List<PersonnelEmployeeAssignment>();
    if (departmentInputs is not null)
    {
        foreach (var input in departmentInputs)
        {
            var token = input?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var department = ResolveDepartmentFromSetup(
                store,
                token,
                defaultAreaCode,
                defaultAreaName,
                defaultDepartmentCode,
                defaultDepartmentName);
            assignments.Add(new PersonnelEmployeeAssignment(department.AreaCode, department.Code));
        }
    }

    assignments = NormalizeEmployeeAssignmentList(assignments);
    if (assignments.Count > 0)
    {
        return assignments;
    }

    var fallback = ResolveDepartmentFromSetup(
        store,
        null,
        defaultAreaCode,
        defaultAreaName,
        defaultDepartmentCode,
        defaultDepartmentName);
    return [new PersonnelEmployeeAssignment(fallback.AreaCode, fallback.Code)];
}

static List<(PersonnelEmployeeAssignment Assignment, PersonnelDepartment Department, PersonnelArea Area)> GetResolvedEmployeeAssignments(
    PersonnelStore store,
    Employee employee,
    PersonnelEmployeeProfile? profile,
    string defaultAreaCode,
    string defaultAreaName,
    string defaultDepartmentCode,
    string defaultDepartmentName)
{
    var rawAssignments = new List<PersonnelEmployeeAssignment>();
    if (profile?.Assignments is { Count: > 0 })
    {
        rawAssignments.AddRange(profile.Assignments);
    }

    if (!string.IsNullOrWhiteSpace(profile?.DepartmentCode))
    {
        rawAssignments.Add(new PersonnelEmployeeAssignment(
            profile.AreaCode?.Trim() ?? string.Empty,
            profile.DepartmentCode.Trim()));
    }

    if (rawAssignments.Count == 0 && !string.IsNullOrWhiteSpace(employee.Department))
    {
        var department = ResolveDepartmentFromSetup(
            store,
            employee.Department,
            defaultAreaCode,
            defaultAreaName,
            defaultDepartmentCode,
            defaultDepartmentName);
        rawAssignments.Add(new PersonnelEmployeeAssignment(department.AreaCode, department.Code));
    }

    var normalizedAssignments = NormalizeEmployeeAssignmentList(rawAssignments);
    if (normalizedAssignments.Count == 0)
    {
        normalizedAssignments = ResolveEmployeeAssignmentsFromInputs(
            store,
            null,
            defaultAreaCode,
            defaultAreaName,
            defaultDepartmentCode,
            defaultDepartmentName);
    }

    return normalizedAssignments
        .Select(assignment =>
        {
            var department = ResolveDepartmentFromSetup(
                store,
                assignment.DepartmentCode,
                defaultAreaCode,
                defaultAreaName,
                defaultDepartmentCode,
                defaultDepartmentName);
            var area = ResolveAreaFromCode(store, department.AreaCode, defaultAreaCode, defaultAreaName);
            return (new PersonnelEmployeeAssignment(department.AreaCode, department.Code), department, area);
        })
        .ToList();
}

static bool ApplyEmployeeAssignments(
    PostgresAttendanceRepository repository,
    PersonnelStore personnelStore,
    Employee employee,
    IReadOnlyList<PersonnelEmployeeAssignment> assignments,
    string defaultAreaCode,
    string defaultAreaName,
    string defaultDepartmentCode,
    string defaultDepartmentName)
{
    var normalizedAssignments = NormalizeEmployeeAssignmentList(assignments);
    if (normalizedAssignments.Count == 0)
    {
        normalizedAssignments = ResolveEmployeeAssignmentsFromInputs(
            personnelStore,
            null,
            defaultAreaCode,
            defaultAreaName,
            defaultDepartmentCode,
            defaultDepartmentName);
    }

    var primaryDepartment = ResolveDepartmentFromSetup(
        personnelStore,
        normalizedAssignments[0].DepartmentCode,
        defaultAreaCode,
        defaultAreaName,
        defaultDepartmentCode,
        defaultDepartmentName);
    employee.Department = primaryDepartment.Name;
    employee.Position = ResolvePositionNameFromSetup(personnelStore, employee.Position, employee.Department);
    employee.SyncedAt = null;
    if (!repository.UpdateEmployee(employee))
    {
        return false;
    }

    var profile = personnelStore.Profiles.FirstOrDefault(p =>
        string.Equals(p.EmployeeCode, employee.EmployeeCode, StringComparison.OrdinalIgnoreCase));
    ProgramHelpers.UpsertEmployeeProfile(
        personnelStore,
        employee.EmployeeCode,
        profile?.Privilege ?? ToProfilePrivilegeLabel(employee.Privilege),
        profile?.CardNumber ?? employee.CardNumber,
        profile?.PictureDataUrl ?? employee.ProfilePhoto,
        profile?.MobileNumber ?? employee.MobileNumber,
        profile?.Gender ?? (string.IsNullOrWhiteSpace(employee.Gender) ? "Male" : employee.Gender),
        profile?.HireDate ?? string.Empty,
        ResolveDevicePassword(profile?.Password, employee.Password),
        employee.Position,
        normalizedAssignments[0].AreaCode,
        normalizedAssignments[0].DepartmentCode,
        verifyMode: employee.VerifyMode,
        assignments: normalizedAssignments);
    return true;
}

static string? ResolveEmployeeDepartmentForDevice(
    PersonnelStore store,
    Employee employee,
    PersonnelEmployeeProfile? profile,
    PersonnelDepartment targetDepartment,
    string defaultAreaCode,
    string defaultAreaName,
    string defaultDepartmentCode,
    string defaultDepartmentName)
{
    var assignments = GetResolvedEmployeeAssignments(
        store,
        employee,
        profile,
        defaultAreaCode,
        defaultAreaName,
        defaultDepartmentCode,
        defaultDepartmentName);
    // AREA is the deciding factor for whether an employee is synced to a device. An employee
    // may belong to multiple departments, so the department is NOT required to match — only the
    // area must be the same. If the employee has no assignment in the device's area, they are
    // NOT synced to that device (returns null → caller skips).
    var deviceAreaCode = targetDepartment.AreaCode;

    // Prefer an assignment that matches BOTH the device's area and department (so the device
    // shows the most relevant department), then fall back to any assignment in the same area.
    var preferred = assignments.FirstOrDefault(x =>
        string.Equals(x.Area.Code, deviceAreaCode, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.Department.Code, targetDepartment.Code, StringComparison.OrdinalIgnoreCase));
    if (preferred.Department is not null)
    {
        return preferred.Department.Name;
    }

    var sameArea = assignments.FirstOrDefault(x =>
        string.Equals(x.Area.Code, deviceAreaCode, StringComparison.OrdinalIgnoreCase));
    if (sameArea.Department is not null)
    {
        return sameArea.Department.Name;
    }

    // Employee is not in this device's area → do not sync them to this device.
    return null;
}

static void NormalizeEmployeeDepartmentsToSetup(
    PostgresAttendanceRepository repository,
    PersonnelStore store,
    string defaultAreaCode,
    string defaultAreaName,
    string defaultDepartmentCode,
    string defaultDepartmentName)
{
    foreach (var employee in repository.GetEmployees())
    {
        var profile = store.Profiles.FirstOrDefault(p =>
            string.Equals(p.EmployeeCode, employee.EmployeeCode, StringComparison.OrdinalIgnoreCase));
        var resolvedAssignments = GetResolvedEmployeeAssignments(
            store,
            employee,
            profile,
            defaultAreaCode,
            defaultAreaName,
            defaultDepartmentCode,
            defaultDepartmentName);
        var primaryDepartment = resolvedAssignments[0].Department.Name;
        if (string.Equals(employee.Department ?? string.Empty, primaryDepartment, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        employee.Department = primaryDepartment;
        repository.UpdateEmployee(employee);
    }
}

static void NormalizeStoredPasswordsToMd5(
    PostgresAttendanceRepository repository,
    PersonnelStore store,
    string personnelPath,
    string connectionString)
{
    var profileChanged = false;
    foreach (var employee in repository.GetEmployees())
    {
        var normalized = string.IsNullOrWhiteSpace(employee.Password)
            ? NormalizePasswordHash(employee.EmployeeCode)
            : NormalizePasswordHash(employee.Password);
        if (string.Equals(employee.Password ?? string.Empty, normalized, StringComparison.Ordinal))
        {
            continue;
        }

        employee.Password = normalized;
        repository.UpdateEmployee(employee);
    }

    for (var i = 0; i < store.Profiles.Count; i++)
    {
        var profile = store.Profiles[i];
        // Keep software/profile password in plain token for device sync.
        var normalized = profile.Password?.Trim() ?? string.Empty;
        if (string.Equals(profile.Password ?? string.Empty, normalized, StringComparison.Ordinal))
        {
            continue;
        }

        store.Profiles[i] = profile with { Password = normalized };
        profileChanged = true;
    }

    if (profileChanged)
    {
        SavePersonnelStoreAndSyncDb(store, personnelPath, connectionString);
    }
}

static void NormalizeEmployeePositionsToSetup(
    PostgresAttendanceRepository repository,
    PersonnelStore store)
{
    var employees = repository.GetEmployees();
    foreach (var employee in employees)
    {
        var normalizedPosition = ResolvePositionNameFromSetup(store, employee.Position, employee.Department);
        if (string.Equals(employee.Position ?? string.Empty, normalizedPosition, StringComparison.Ordinal))
        {
            continue;
        }

        employee.Position = normalizedPosition;
        repository.UpdateEmployee(employee);
    }
}

static void NormalizePositionsToSetup(
    PersonnelStore store,
    string defaultAreaCode,
    string defaultAreaName,
    string defaultDepartmentCode,
    string defaultDepartmentName)
{
    for (var i = 0; i < store.Positions.Count; i++)
    {
        var current = store.Positions[i];
        var normalizedDepartment = ResolveDepartmentNameFromSetup(
            store,
            current.Department,
            defaultAreaCode,
            defaultAreaName,
            defaultDepartmentCode,
            defaultDepartmentName);
        if (!string.Equals(current.Department, normalizedDepartment, StringComparison.OrdinalIgnoreCase))
        {
            store.Positions[i] = current with { Department = normalizedDepartment };
        }
    }
}

static (int AreaId, int DepartmentId) SyncSqlAreaAndDepartment(PostgresAttendanceRepository repository, string areaName, string departmentName)
{
    var name = string.IsNullOrWhiteSpace(areaName) ? "Default Area" : areaName.Trim();
    var deptName = string.IsNullOrWhiteSpace(departmentName) ? "Default Department" : departmentName.Trim();

    var dbArea = repository.GetAreas().FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    if (dbArea is null)
    {
        dbArea = repository.AddArea(new Area { Name = name, IsDefault = false });
    }

    var dbDept = repository.GetDepartments().FirstOrDefault(d => d.AreaId == dbArea.Id && string.Equals(d.Name, deptName, StringComparison.OrdinalIgnoreCase));
    if (dbDept is null)
    {
        dbDept = repository.AddDepartment(new Department { Name = deptName, AreaId = dbArea.Id, IsDefault = false });
    }

    return (dbArea.Id, dbDept.Id);
}

static (PersonnelArea Area, PersonnelDepartment Department) ResolveDeviceSetupForSerial(
    PersonnelStore store,
    string serialNumber,
    string? fallbackAreaName,
    string defaultAreaCode,
    string defaultAreaName,
    string defaultDepartmentCode,
    string defaultDepartmentName)
{
    var assignment = store.DeviceAssignments.FirstOrDefault(x =>
        string.Equals(x.DeviceSerial, serialNumber, StringComparison.OrdinalIgnoreCase));
    return ResolveDeviceSetup(
        store,
        assignment?.AreaCode,
        fallbackAreaName,
        assignment?.DepartmentCode,
        defaultAreaCode,
        defaultAreaName,
        defaultDepartmentCode,
        defaultDepartmentName);
}

static void UpsertDeviceAssignment(PersonnelStore store, string serialNumber, string areaCode, string departmentCode)
{
    if (string.IsNullOrWhiteSpace(serialNumber))
    {
        return;
    }

    store.DeviceAssignments.RemoveAll(x =>
        string.Equals(x.DeviceSerial, serialNumber.Trim(), StringComparison.OrdinalIgnoreCase));
    store.DeviceAssignments.Add(new PersonnelDeviceAssignment(
        serialNumber.Trim(),
        areaCode?.Trim() ?? string.Empty,
        departmentCode?.Trim() ?? string.Empty));
}

static bool IsPersonnelSetupMutationPath(string path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return false;
    }

    return path.StartsWith("/api/personnel", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith("/api/devices", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith("/api/device/setup", StringComparison.OrdinalIgnoreCase);
}

static bool IsPersonnelStoreEmpty(PersonnelStore store)
{
    return store.Departments.Count == 0 &&
           store.Positions.Count == 0 &&
           store.Areas.Count == 0 &&
           store.DeviceAssignments.Count == 0 &&
           store.Profiles.Count == 0;
}

static void ReplacePersonnelStoreContents(PersonnelStore target, PersonnelStore source)
{
    target.Departments.Clear();
    target.Departments.AddRange(source.Departments);
    target.Positions.Clear();
    target.Positions.AddRange(source.Positions);
    target.Areas.Clear();
    target.Areas.AddRange(source.Areas);
    target.DeviceAssignments.Clear();
    target.DeviceAssignments.AddRange(source.DeviceAssignments);
    target.Profiles.Clear();
    target.Profiles.AddRange(source.Profiles);
}

static void HydratePersonnelStoreFromDb(PersonnelStore target, string connectionString)
{
    ReplacePersonnelStoreContents(target, LoadPersonnelStoreFromDb(connectionString));
}

static PersonnelStore LoadPersonnelStoreFromDb(string connectionString)
{
    var store = new PersonnelStore();
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return store;
    }

    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();

        static string ReadString(NpgsqlDataReader reader, int ordinal)
            => reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);

        static Guid ParseStoredGuid(string? raw, string scope, string key)
        {
            if (Guid.TryParse(raw, out var parsed))
            {
                return parsed;
            }

            var seed = $"{scope}:{key}".Trim();
            var bytes = MD5.HashData(Encoding.UTF8.GetBytes(seed));
            return new Guid(bytes);
        }

        static List<PersonnelEmployeeAssignment> ReadAssignments(string? rawJson, string areaCode, string departmentCode)
        {
            var assignments = new List<PersonnelEmployeeAssignment>();
            if (!string.IsNullOrWhiteSpace(rawJson))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<PersonnelEmployeeAssignment>>(rawJson);
                    if (parsed is not null)
                    {
                        assignments.AddRange(parsed);
                    }
                }
                catch
                {
                    // Ignore malformed historic payloads and fall back to primary codes.
                }
            }

            if (!string.IsNullOrWhiteSpace(departmentCode))
            {
                assignments.Add(new PersonnelEmployeeAssignment(areaCode, departmentCode));
            }

            return NormalizeEmployeeAssignmentList(assignments);
        }

        using (var cmd = new NpgsqlCommand("""
            SELECT "Id","Code","Name","Remark"
            FROM "E_Attendace"."PersonnelAreas"
            ORDER BY "Code";
            """, conn))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var code = ReadString(reader, 1);
                store.Areas.Add(new PersonnelArea(
                    ParseStoredGuid(ReadString(reader, 0), "area", code),
                    code,
                    ReadString(reader, 2),
                    ReadString(reader, 3)));
            }
        }

        using (var cmd = new NpgsqlCommand("""
            SELECT "Id","Code","Name","Superior","Manager","AreaCode"
            FROM "E_Attendace"."PersonnelDepartments"
            ORDER BY "Code";
            """, conn))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var code = ReadString(reader, 1);
                store.Departments.Add(new PersonnelDepartment(
                    ParseStoredGuid(ReadString(reader, 0), "department", code),
                    code,
                    ReadString(reader, 2),
                    ReadString(reader, 3),
                    ReadString(reader, 4),
                    ReadString(reader, 5)));
            }
        }

        using (var cmd = new NpgsqlCommand("""
            SELECT "Id","Code","Name","Department"
            FROM "E_Attendace"."PersonnelPositions"
            ORDER BY "Code";
            """, conn))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var code = ReadString(reader, 1);
                store.Positions.Add(new PersonnelPosition(
                    ParseStoredGuid(ReadString(reader, 0), "position", code),
                    code,
                    ReadString(reader, 2),
                    ReadString(reader, 3)));
            }
        }

        using (var cmd = new NpgsqlCommand("""
            SELECT "DeviceSerial","AreaCode","DepartmentCode"
            FROM "E_Attendace"."PersonnelDeviceAssignments"
            ORDER BY "DeviceSerial";
            """, conn))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                store.DeviceAssignments.Add(new PersonnelDeviceAssignment(
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadString(reader, 2)));
            }
        }

        using (var cmd = new NpgsqlCommand("""
            SELECT "EmployeeCode","Privilege","CardNumber","PictureDataUrl","MobileNumber","Gender","HireDate","Password","Position","AreaCode","DepartmentCode","AssignmentsJson","VerifyMode"
            FROM "E_Attendace"."PersonnelEmployeeProfiles"
            ORDER BY "EmployeeCode";
            """, conn))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var employeeCode = ReadString(reader, 0);
                var areaCode = ReadString(reader, 9);
                var departmentCode = ReadString(reader, 10);
                var assignments = ReadAssignments(ReadString(reader, 11), areaCode, departmentCode);
                var primaryAssignment = assignments.FirstOrDefault();
                store.Profiles.Add(new PersonnelEmployeeProfile(
                    employeeCode,
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadString(reader, 3),
                    ReadString(reader, 4),
                    ReadString(reader, 5),
                    ReadString(reader, 6),
                    ReadString(reader, 7),
                    ReadString(reader, 8),
                    primaryAssignment?.AreaCode ?? areaCode,
                    primaryAssignment?.DepartmentCode ?? departmentCode,
                    VerifyMode: reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                    Assignments: assignments));
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[SetupLoad] Failed to load setup/profile data from DB: {ex.Message}");
    }

    NormalizeEmployeeAssignments(store);
    return store;
}

static void TryArchiveLegacyPersonnelFile(string personnelPath)
{
    if (string.IsNullOrWhiteSpace(personnelPath) || !File.Exists(personnelPath))
    {
        return;
    }

    try
    {
        var archivePath = $"{personnelPath}.{DateTime.UtcNow:yyyyMMddHHmmss}.migrated-to-db.bak";
        File.Move(personnelPath, archivePath, overwrite: true);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Startup] Warning: unable to archive legacy personnel file '{personnelPath}'. {ex.Message}");
    }
}

static void SavePersonnelStoreAndSyncDb(PersonnelStore store, string personnelPath, string connectionString)
{
    PersistPersonnelSetupToDb(connectionString, store);
    HydratePersonnelStoreFromDb(store, connectionString);
    TryArchiveLegacyPersonnelFile(personnelPath);
}

static void PersistDeviceAssignmentToDb(string connectionString, string serialNumber, string areaCode, string departmentCode)
{
    if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(serialNumber))
    {
        return;
    }

    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            """
            INSERT INTO "E_Attendace"."PersonnelDeviceAssignments" ("DeviceSerial","AreaCode","DepartmentCode","UpdatedAtUtc")
            VALUES (@serial,@area,@dept,NOW())
            ON CONFLICT ("DeviceSerial") DO UPDATE SET
                "AreaCode" = EXCLUDED."AreaCode",
                "DepartmentCode" = EXCLUDED."DepartmentCode",
                "UpdatedAtUtc" = NOW();
            """,
            conn);
        cmd.Parameters.AddWithValue("serial", serialNumber.Trim());
        cmd.Parameters.AddWithValue("area", areaCode?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("dept", departmentCode?.Trim() ?? string.Empty);
        cmd.ExecuteNonQuery();
    }
    catch
    {
        // Best-effort update for device assignment; avoid blocking device add.
    }
}

static int DeletePersonnelArtifactsInPostgres(string connectionString, string employeeCode)
{
    if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(employeeCode))
    {
        return 0;
    }

    var affected = 0;
    var code = employeeCode.Trim();
    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();

        var sql = """
            DELETE FROM "E_Attendace"."PersonnelEmployeeProfiles" WHERE "EmployeeCode" = @code;
            DELETE FROM "E_Attendace"."TransactionDetails" WHERE "EmployeeCode" = @code;
            """;

        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("code", code);
        affected += cmd.ExecuteNonQuery();
    }
    catch
    {
        // best-effort cleanup; primary delete path already ran
    }

    return affected;
}

static void PersistPersonnelSetupToDb(string connectionString, PersonnelStore store)
{
    try
    {
        NormalizeEmployeeAssignments(store);
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();

        static NpgsqlCommand Cmd(NpgsqlConnection c, NpgsqlTransaction t, string sql)
        {
            var cmd = new NpgsqlCommand(sql, c, t);
            return cmd;
        }

        using (var clear = Cmd(conn, tx, """
            DELETE FROM "E_Attendace"."PersonnelEmployeeProfiles";
            DELETE FROM "E_Attendace"."PersonnelDeviceAssignments";
            DELETE FROM "E_Attendace"."PersonnelPositions";
            DELETE FROM "E_Attendace"."PersonnelDepartments";
            DELETE FROM "E_Attendace"."PersonnelAreas";
            """))
        {
            clear.ExecuteNonQuery();
        }

        const string areaSql = """
            INSERT INTO "E_Attendace"."PersonnelAreas" ("Id","Code","Name","Remark","UpdatedAtUtc")
            VALUES (@id,@code,@name,@remark,NOW())
            ON CONFLICT ("Code") DO UPDATE SET
                "Id" = EXCLUDED."Id",
                "Name" = EXCLUDED."Name",
                "Remark" = EXCLUDED."Remark",
                "UpdatedAtUtc" = NOW();
            """;
        foreach (var area in store.Areas)
        {
            using var cmd = Cmd(conn, tx, areaSql);
            cmd.Parameters.AddWithValue("id", area.Id.ToString());
            cmd.Parameters.AddWithValue("code", area.Code?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("name", area.Name?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("remark", area.Remark?.Trim() ?? string.Empty);
            cmd.ExecuteNonQuery();
        }

        const string deptSql = """
            INSERT INTO "E_Attendace"."PersonnelDepartments" ("Id","Code","Name","Superior","Manager","AreaCode","UpdatedAtUtc")
            VALUES (@id,@code,@name,@superior,@manager,@areaCode,NOW())
            ON CONFLICT ("Code") DO UPDATE SET
                "Id" = EXCLUDED."Id",
                "Name" = EXCLUDED."Name",
                "Superior" = EXCLUDED."Superior",
                "Manager" = EXCLUDED."Manager",
                "AreaCode" = EXCLUDED."AreaCode",
                "UpdatedAtUtc" = NOW();
            """;
        foreach (var department in store.Departments)
        {
            using var cmd = Cmd(conn, tx, deptSql);
            cmd.Parameters.AddWithValue("id", department.Id.ToString());
            cmd.Parameters.AddWithValue("code", department.Code?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("name", department.Name?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("superior", department.Superior?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("manager", department.Manager?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("areaCode", department.AreaCode?.Trim() ?? string.Empty);
            cmd.ExecuteNonQuery();
        }

        const string posSql = """
            INSERT INTO "E_Attendace"."PersonnelPositions" ("Id","Code","Name","Department","UpdatedAtUtc")
            VALUES (@id,@code,@name,@department,NOW())
            ON CONFLICT ("Code") DO UPDATE SET
                "Id" = EXCLUDED."Id",
                "Name" = EXCLUDED."Name",
                "Department" = EXCLUDED."Department",
                "UpdatedAtUtc" = NOW();
            """;
        foreach (var position in store.Positions)
        {
            using var cmd = Cmd(conn, tx, posSql);
            cmd.Parameters.AddWithValue("id", position.Id.ToString());
            cmd.Parameters.AddWithValue("code", position.Code?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("name", position.Name?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("department", position.Department?.Trim() ?? string.Empty);
            cmd.ExecuteNonQuery();
        }

        const string assignmentSql = """
            INSERT INTO "E_Attendace"."PersonnelDeviceAssignments" ("DeviceSerial","AreaCode","DepartmentCode","UpdatedAtUtc")
            VALUES (@serial,@areaCode,@departmentCode,NOW())
            ON CONFLICT ("DeviceSerial") DO UPDATE SET
                "AreaCode" = EXCLUDED."AreaCode",
                "DepartmentCode" = EXCLUDED."DepartmentCode",
                "UpdatedAtUtc" = NOW();
            """;
        foreach (var assignment in store.DeviceAssignments)
        {
            using var cmd = Cmd(conn, tx, assignmentSql);
            cmd.Parameters.AddWithValue("serial", assignment.DeviceSerial?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("areaCode", assignment.AreaCode?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("departmentCode", assignment.DepartmentCode?.Trim() ?? string.Empty);
            cmd.ExecuteNonQuery();
        }

        const string profileSql = """
            INSERT INTO "E_Attendace"."PersonnelEmployeeProfiles" ("EmployeeCode","Privilege","CardNumber","PictureDataUrl","MobileNumber","Gender","HireDate","Password","Position","AreaCode","DepartmentCode","AssignmentsJson","VerifyMode","UpdatedAtUtc")
            VALUES (@employeeCode,@privilege,@cardNumber,@pictureDataUrl,@mobileNumber,@gender,@hireDate,@password,@position,@areaCode,@departmentCode,@assignmentsJson,@verifyMode,NOW())
            ON CONFLICT ("EmployeeCode") DO UPDATE SET
                "Privilege" = EXCLUDED."Privilege",
                "CardNumber" = EXCLUDED."CardNumber",
                "PictureDataUrl" = EXCLUDED."PictureDataUrl",
                "MobileNumber" = EXCLUDED."MobileNumber",
                "Gender" = EXCLUDED."Gender",
                "HireDate" = EXCLUDED."HireDate",
                "Password" = EXCLUDED."Password",
                "Position" = EXCLUDED."Position",
                "AreaCode" = EXCLUDED."AreaCode",
                "DepartmentCode" = EXCLUDED."DepartmentCode",
                "AssignmentsJson" = EXCLUDED."AssignmentsJson",
                "VerifyMode" = EXCLUDED."VerifyMode",
                "UpdatedAtUtc" = NOW();
            """;
        foreach (var profile in store.Profiles)
        {
            using var cmd = Cmd(conn, tx, profileSql);
            cmd.Parameters.AddWithValue("employeeCode", profile.EmployeeCode?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("privilege", profile.Privilege?.Trim() ?? "User");
            cmd.Parameters.AddWithValue("cardNumber", profile.CardNumber?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("pictureDataUrl", profile.PictureDataUrl?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("mobileNumber", profile.MobileNumber?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("gender", profile.Gender?.Trim() ?? "Male");
            cmd.Parameters.AddWithValue("hireDate", profile.HireDate?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("password", profile.Password?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("position", profile.Position?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("areaCode", profile.AreaCode?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("departmentCode", profile.DepartmentCode?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("assignmentsJson", JsonSerializer.Serialize(profile.Assignments ?? []));
            cmd.Parameters.AddWithValue("verifyMode", profile.VerifyMode);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[SetupSync] Failed to persist setup/profile data to DB: {ex.Message}");
    }
}

static bool? ParseBoolOption(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    var token = value.Trim();
    if (token == "1") return true;
    if (token == "0") return false;
    if (bool.TryParse(token, out var b)) return b;
    return null;
}

static int? ParseIntOption(string? value)
{
    if (int.TryParse((value ?? string.Empty).Trim(), out var number))
    {
        return number;
    }

    return null;
}

static DateTime? ParseDateTimeOption(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    var token = value.Trim();
    if (DateTime.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed) ||
        DateTime.TryParse(token, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
    {
        return parsed;
    }

    return null;
}

static Dictionary<int, int> ParseMultiBioVersion(string? value)
{
    var result = new Dictionary<int, int>();
    if (string.IsNullOrWhiteSpace(value))
    {
        return result;
    }

    var tokens = value.Split(':', StringSplitOptions.TrimEntries);
    for (var i = 0; i < tokens.Length; i++)
    {
        if (int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            result[i] = parsed;
        }
    }

    return result;
}

static (bool hasValue, bool supportsFingerprint, bool supportsFaceNir, bool supportsFaceVis, bool supportsPalm, bool supportsFingerVein)
    ParseMultiBioDataSupport(string? support)
{
    if (string.IsNullOrWhiteSpace(support))
    {
        return (false, false, false, false, false, false);
    }

    var parts = support.Split(':', StringSplitOptions.TrimEntries);
    bool IsOn(int index) => index >= 0 && index < parts.Length && string.Equals(parts[index], "1", StringComparison.OrdinalIgnoreCase);

    return (
        true,
        supportsFingerprint: IsOn(1),
        supportsFaceNir: IsOn(2),
        supportsFaceVis: IsOn(9),
        supportsPalm: IsOn(6) || IsOn(10),
        supportsFingerVein: IsOn(7)
    );
}

static void TouchDiscoveredDevice(
    ConcurrentDictionary<string, DiscoveredDevice> discovered,
    string? serial,
    string? ipAddress)
{
    if (string.IsNullOrWhiteSpace(serial))
    {
        return;
    }

    var trimmed = serial.Trim();
    var ip = ipAddress?.Trim() ?? string.Empty;
    discovered.AddOrUpdate(
        trimmed,
        _ => new DiscoveredDevice(trimmed, trimmed, string.Empty, ip, string.Empty, DateTime.UtcNow, true, 0, 0, 0, 0, -1),
        (_, old) => old with
        {
            IpAddress = string.IsNullOrWhiteSpace(ip) ? old.IpAddress : ip,
            LastActivityUtc = DateTime.UtcNow,
            IsOnline = true
        });
}

static void UpdateDiscoveredFromInfo(ConcurrentDictionary<string, DiscoveredDevice> discovered, string? serial, string infoPayload, string? fallbackIp)
{
    if (string.IsNullOrWhiteSpace(serial))
    {
        return;
    }

    var parts = (infoPayload ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
    var ip = parts.Length >= 5 ? parts[4] : fallbackIp ?? string.Empty;
    var userQty = parts.Length >= 2 && int.TryParse(parts[1], out var u) ? u : 0;
    var fpQty = parts.Length >= 3 && int.TryParse(parts[2], out var f) ? f : 0;
    var transactionQty = parts.Length >= 4 && int.TryParse(parts[3], out var t) ? t : -1;
    var faceQty = parts.Length >= 9 && int.TryParse(parts[8], out var fa) ? fa : 0;
    var model = parts.Length > 9 ? string.Join(",", parts.Skip(9).Where(p => !string.IsNullOrWhiteSpace(p) && !int.TryParse(p, out _))) : string.Empty;

    discovered.AddOrUpdate(
        serial.Trim(),
        _ => new DiscoveredDevice(serial.Trim(), serial.Trim(), model, ip ?? string.Empty, string.Empty, DateTime.UtcNow, true, userQty, fpQty, faceQty, 0, transactionQty),
        (_, old) => old with
        {
            IpAddress = string.IsNullOrWhiteSpace(ip) ? old.IpAddress : ip,
            LastActivityUtc = DateTime.UtcNow,
            IsOnline = true,
            UserQty = userQty,
            FpQty = fpQty,
            FaceQty = faceQty,
            TransactionQty = transactionQty >= 0 ? transactionQty : old.TransactionQty,
            Model = string.IsNullOrWhiteSpace(model) ? old.Model : model
        });
}

static void UpdateDiscoveredFromOptions(ConcurrentDictionary<string, DiscoveredDevice> discovered, string serial, string body, string? fallbackIp)
{
    var map = ParseOptionPairs(body);
    var name = FirstValue(map, "~DeviceName", "~DevName", "DevName", "DeviceName", "Alias", "TerminalName") ?? serial;
    var model = NormalizeDeviceModelToken(FirstValue(map, "~DeviceModel", "DeviceModel", "~Model", "Model", "DeviceType", "ProductType", "DevType", "TerminalType"));
    var ip = FirstValue(map, "~IPAddress", "~IP", "IPAddress", "IP", "NetIP") ?? fallbackIp ?? string.Empty;
    var area = FirstValue(map, "~Area", "~Location", "Area", "Location", "Addr", "Address") ?? string.Empty;
    var palm = map.TryGetValue("~PalmCount", out var p) && int.TryParse(p, out var pv) ? pv : 0;
    if (palm == 0 && map.TryGetValue("PalmCount", out var p2) && int.TryParse(p2, out var pv2)) { palm = pv2; }
    var transactionQty = ParseOptionInt(map, "~AttLogCount", "~AttlogCount", "~ATTLOGCount", "~TransactionCount", "AttLogCount", "AttlogCount", "ATTLOGCount", "TransactionCount");

    discovered.AddOrUpdate(
        serial.Trim(),
        _ => new DiscoveredDevice(serial.Trim(), name, model, ip, area, DateTime.UtcNow, true, 0, 0, 0, palm, transactionQty),
        (_, old) => old with
        {
            Name = string.IsNullOrWhiteSpace(name) ? old.Name : name,
            Model = string.IsNullOrWhiteSpace(model) ? old.Model : model,
            IpAddress = string.IsNullOrWhiteSpace(ip) ? old.IpAddress : ip,
            Area = string.IsNullOrWhiteSpace(area) ? old.Area : area,
            LastActivityUtc = DateTime.UtcNow,
            IsOnline = true,
            PalmQty = palm > 0 ? palm : old.PalmQty,
            TransactionQty = transactionQty >= 0 ? transactionQty : old.TransactionQty
        });
}

static int ParseOptionInt(IReadOnlyDictionary<string, string> map, params string[] keys)
{
    foreach (var key in keys)
    {
        if (map.TryGetValue(key, out var value) && int.TryParse(value, out var parsed))
        {
            return parsed;
        }
    }

    return -1;
}

static string ResolveContentRootPath()
{
    if (File.Exists(Path.Combine(AppContext.BaseDirectory, "appsettings.json")) ||
        Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")))
    {
        return AppContext.BaseDirectory;
    }

    var candidates = new List<string>();

    void AddCandidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var full = Path.GetFullPath(path);
            if (!candidates.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(full);
            }
        }
        catch
        {
            // Ignore invalid paths and continue with other candidates.
        }
    }

    try
    {
        AddCandidate(Path.GetDirectoryName(Environment.ProcessPath));
    }
    catch
    {
        // Best-effort only.
    }

    try
    {
        AddCandidate(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName));
    }
    catch
    {
        // Best-effort only.
    }

    AddCandidate(AppContext.BaseDirectory);
    AddCandidate(Environment.CurrentDirectory);

    foreach (var candidate in candidates)
    {
        if (Directory.Exists(candidate))
        {
            return candidate;
        }
    }

    return AppContext.BaseDirectory;
}

static string ResolveWebRootPath(string contentRootPath)
{
    var candidates = new List<string>();

    void AddCandidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var full = Path.GetFullPath(path);
            if (!candidates.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(full);
            }
        }
        catch
        {
            // Ignore invalid paths and continue with other candidates.
        }
    }

    AddCandidate(Path.Combine(contentRootPath, "wwwroot"));
    AddCandidate(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
    AddCandidate(Path.Combine(Environment.CurrentDirectory, "wwwroot"));
    try
    {
        AddCandidate(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty, "wwwroot"));
    }
    catch
    {
        // Best-effort only.
    }

    string? firstExisting = null;
    foreach (var candidate in candidates)
    {
        if (!Directory.Exists(candidate))
        {
            continue;
        }

        if (Directory.Exists(Path.Combine(candidate, "_next")))
        {
            return candidate;
        }

        firstExisting ??= candidate;
    }

    if (!string.IsNullOrWhiteSpace(firstExisting))
    {
        return firstExisting;
    }

    // Dev fallback when running from bin without copied static files.
    var projectWebRoot = Path.GetFullPath(Path.Combine(contentRootPath, "..", "..", "..", "..", "wwwroot"));
    if (Directory.Exists(projectWebRoot))
    {
        return projectWebRoot;
    }

    return Path.Combine(contentRootPath, "wwwroot");
}

static void EnsureBundledWebUiFiles(string? webRootPath)
{
    if (string.IsNullOrWhiteSpace(webRootPath))
    {
        return;
    }

    try
    {
        Directory.CreateDirectory(webRootPath);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Startup] Warning: unable to ensure webroot '{webRootPath}'. {ex.Message}");
        return;
    }

    var indexPath = Path.Combine(webRootPath, "index.html");
    if (File.Exists(indexPath))
    {
        // Bundled frontend is already present (for example Next export).
        // Avoid writing fallback assets in locked install directories.
        return;
    }

    EnsureBundledWebUiFile(indexPath, """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>E-Attendance - Home</title>
  <link rel="stylesheet" href="/styles.css" />
</head>
<body>
  <header class="topbar">
    <div class="brand">E-Attendance</div>
    <div class="status-group">
      <span id="chipPort" class="chip">Port: -</span>
      <span id="health" class="chip">STOPPED</span>
    </div>
  </header>

  <main class="page">
    <section class="grid">
      <article class="card">
        <h2>Service Overview</h2>
        <div class="kpi-row">
          <div class="kpi">
            <div class="kpi-label">Push Hits</div>
            <div id="requestCount" class="kpi-value">0</div>
          </div>
          <div class="kpi">
            <div class="kpi-label">Server</div>
            <div id="serverText" class="kpi-value">Stopped</div>
          </div>
        </div>
      </article>

      <article class="card">
        <h2>Recent Activity</h2>
        <pre id="recentRequests" class="log">No recent requests</pre>
      </article>
    </section>
  </main>

  <div id="toast" class="toast"></div>
  <script src="/app.js"></script>
</body>
</html>
""");

    EnsureBundledWebUiFile(Path.Combine(webRootPath, "app.js"), """
const el = (id) => document.getElementById(id);

const refs = {
  chipPort: el("chipPort"),
  health: el("health"),
  requestCount: el("requestCount"),
  serverText: el("serverText"),
  recentRequests: el("recentRequests"),
  toast: el("toast")
};

function showToast(message) {
  refs.toast.textContent = message;
  refs.toast.classList.add("show");
  setTimeout(() => refs.toast.classList.remove("show"), 1700);
}

async function api(url) {
  const resp = await fetch(url, { headers: { "Content-Type": "application/json" } });
  if (!resp.ok) {
    throw new Error(`Request failed (${resp.status})`);
  }

  const type = resp.headers.get("content-type") || "";
  if (type.includes("application/json")) {
    return await resp.json();
  }

  return await resp.text();
}

function renderStats(stats) {
  const running = !!stats.isRunning;
  refs.health.textContent = running ? "RUNNING" : "STOPPED";
  refs.health.classList.toggle("ok", running);
  refs.serverText.textContent = running ? "Running" : "Stopped";
  refs.requestCount.textContent = String(stats.requestCount ?? 0);
  refs.chipPort.textContent = `Port: ${stats.configuredPort ?? "-"}`;

  const recent = Array.isArray(stats.recent) ? stats.recent : [];
  refs.recentRequests.textContent = recent.length > 0 ? recent.join("\n") : "No recent requests";
}

async function refreshStats() {
  try {
    const stats = await api("/api/server/stats");
    renderStats(stats);
  } catch (err) {
    refs.serverText.textContent = "Unavailable";
    refs.health.textContent = "ERROR";
    refs.health.classList.remove("ok");
  }
}

refreshStats().catch(() => showToast("Unable to load dashboard."));
setInterval(() => {
  refreshStats().catch(() => {});
}, 4000);
""");

    EnsureBundledWebUiFile(Path.Combine(webRootPath, "styles.css"), """
:root {
  --bg: #eef3fb;
  --card: #ffffff;
  --line: #d9e2ef;
  --ink: #1d2a3c;
  --muted: #64748b;
  --primary: #1f4b8f;
  --ok: #198754;
  --warn: #c0392b;
}

* { box-sizing: border-box; }

body {
  margin: 0;
  font-family: "Segoe UI", Arial, sans-serif;
  color: var(--ink);
  background: var(--bg);
}

.topbar {
  height: 58px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 16px;
  background: linear-gradient(90deg, #173c73, #2459a2);
  color: #fff;
}

.brand {
  font-size: 20px;
  font-weight: 700;
}

.status-group {
  display: flex;
  gap: 8px;
}

.chip {
  font-size: 12px;
  padding: 5px 10px;
  border-radius: 999px;
  border: 1px solid rgba(255, 255, 255, 0.4);
  background: rgba(255, 255, 255, 0.14);
}

.chip.ok {
  background: rgba(25, 135, 84, 0.22);
}

.page {
  padding: 16px;
}

.grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
}

.card {
  background: var(--card);
  border: 1px solid var(--line);
  border-radius: 12px;
  padding: 14px;
  box-shadow: 0 4px 16px rgba(15, 35, 65, 0.08);
}

.card h2 {
  margin: 0 0 12px;
  color: var(--primary);
  font-size: 20px;
}

.kpi-row {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
}

.kpi {
  border: 1px solid var(--line);
  border-radius: 10px;
  padding: 12px;
  background: #f8fbff;
}

.kpi-label {
  font-size: 12px;
  color: var(--muted);
}

.kpi-value {
  margin-top: 4px;
  font-size: 24px;
  font-weight: 700;
}

.log {
  margin: 0;
  min-height: 260px;
  max-height: 420px;
  overflow: auto;
  padding: 10px;
  border: 1px solid var(--line);
  border-radius: 10px;
  background: #fafcff;
  color: #334155;
  font-size: 12px;
}

.toast {
  position: fixed;
  right: 14px;
  bottom: 14px;
  padding: 8px 11px;
  border-radius: 8px;
  background: #253b5f;
  color: #fff;
  opacity: 0;
  transition: opacity .2s ease;
  pointer-events: none;
}

.toast.show {
  opacity: 1;
}

@media (max-width: 900px) {
  .grid { grid-template-columns: 1fr; }
}
""");
}

static void EnsureBundledWebUiFile(string path, string content)
{
    if (File.Exists(path))
    {
        return;
    }

    try
    {
        File.WriteAllText(path, content, Encoding.UTF8);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Startup] Warning: unable to create fallback web asset '{path}'. {ex.Message}");
    }
}

static string ResolveServiceRole(string[] args)
{
    foreach (var arg in args)
    {
        if (!arg.StartsWith("--service-role=", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var role = arg["--service-role=".Length..].Trim().ToLowerInvariant();
        if (role is "api" or "web")
        {
            return role;
        }
    }

    return "all";
}

static string[] BuildListenUrls(int apiPort, int frontendPort, string serviceRole = "all")
{
    var urls = new List<string>();

    static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    var apiOnly = string.Equals(serviceRole, "api", StringComparison.OrdinalIgnoreCase);
    var webOnly = string.Equals(serviceRole, "web", StringComparison.OrdinalIgnoreCase);
    var listenHost = string.Equals(
        Environment.GetEnvironmentVariable("E_ATTENDANCE_LOOPBACK_ONLY"),
        "true",
        StringComparison.OrdinalIgnoreCase)
        ? "127.0.0.1"
        : "0.0.0.0";

    if (!webOnly && IsValidPort(apiPort))
    {
        urls.Add($"http://{listenHost}:{apiPort}");
    }

    if (!apiOnly && IsValidPort(frontendPort) && (webOnly || frontendPort != apiPort))
    {
        urls.Add($"http://{listenHost}:{frontendPort}");
    }

    return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

static string[] BuildPublicListenUrls(int apiPort, int frontendPort)
{
    var urls = new List<string>();

    static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    if (IsValidPort(apiPort))
    {
        urls.Add($"http://localhost:{apiPort}");
        urls.Add($"http://<your-ip>:{apiPort}");
    }

    if (IsValidPort(frontendPort) && frontendPort != apiPort)
    {
        urls.Add($"http://localhost:{frontendPort}");
        urls.Add($"http://<your-ip>:{frontendPort}");
    }

    return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

static string ResolvePreferredServerIp()
{
    try
    {
        var candidates = new List<(string Ip, int Priority)>();

        foreach (var networkInterface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
            {
                continue;
            }

            // Skip loopback and tunnel interfaces
            if (networkInterface.NetworkInterfaceType is
                System.Net.NetworkInformation.NetworkInterfaceType.Loopback or
                System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            // Determine priority based on interface type
            // Lower number = higher priority
            int priority = networkInterface.NetworkInterfaceType switch
            {
                System.Net.NetworkInformation.NetworkInterfaceType.Ethernet => 1,      // LAN (wired) - highest priority
                System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 => 2,   // WiFi
                System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet => 1, // Gigabit Ethernet
                System.Net.NetworkInformation.NetworkInterfaceType.FastEthernetT => 1,   // Fast Ethernet
                System.Net.NetworkInformation.NetworkInterfaceType.FastEthernetFx => 1,  // Fast Ethernet FX
                _ => 99 // Other types (VPN, etc.) - lowest priority
            };

            var properties = networkInterface.GetIPProperties();
            foreach (var addressInfo in properties.UnicastAddresses)
            {
                var address = addressInfo.Address;
                if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                    System.Net.IPAddress.IsLoopback(address))
                {
                    continue;
                }

                // Skip link-local (169.254.x.x) and APIPA addresses
                var bytes = address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                {
                    continue;
                }

                var addressText = address.ToString();
                if (!string.IsNullOrWhiteSpace(addressText))
                {
                    candidates.Add((addressText, priority));
                }
            }
        }

        // Sort by priority (lower = better), then return first
        if (candidates.Count > 0)
        {
            candidates.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            return candidates[0].Ip;
        }
    }
    catch
    {
    }

    return "127.0.0.1";
}

static WebAppSettings EnsureBindableRuntimePorts(WebAppSettings settings, string settingsPath)
{
    var apiPort = settings.Port;
    var frontendPort = settings.FrontendPort;

    // Same port is a configuration error — auto-fix by shifting frontend port by 1
    if (frontendPort == apiPort)
    {
        frontendPort = apiPort == 65535 ? apiPort - 1 : apiPort + 1;
        Console.Error.WriteLine(
            $"[Startup] API port and Web UI port were both {apiPort}. Auto-corrected Web UI port to {frontendPort}.");
        settings = settings with { FrontendPort = frontendPort };
        try { File.WriteAllText(settingsPath, System.Text.Json.JsonSerializer.Serialize(settings)); } catch { }
    }

    // Log warnings for ports in use but let ASP.NET Core raise the real bind error at app.Run()
    // Throwing here would crash the process before UseWindowsService() notifies the SCM → Error 1053
    if (!CanBindPort(apiPort))
        Console.Error.WriteLine($"[Startup] WARNING: API port {apiPort} may already be in use. Will attempt to bind.");
    if (!CanBindPort(frontendPort))
        Console.Error.WriteLine($"[Startup] WARNING: Web UI port {frontendPort} may already be in use. Will attempt to bind.");

    return settings;
}

static bool CanBindPort(int port)
{
    if (port is < 1 or > 65535)
    {
        return false;
    }

    System.Net.Sockets.TcpListener? listener = null;
    try
    {
        listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        return true;
    }
    catch
    {
        return false;
    }
    finally
    {
        try
        {
            listener?.Stop();
        }
        catch
        {
            // Ignore close errors.
        }
    }
}

static bool IsAllowedFrontendOrigin(string origin)
{
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
    {
        return false;
    }

    if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var host = uri.Host;
    if (string.IsNullOrWhiteSpace(host))
    {
        return false;
    }

    // Allow localhost variants
    if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    // Allow private IPs
    if (System.Net.IPAddress.TryParse(host, out var address))
    {
        return IsPrivateOrLoopbackAddress(address);
    }

    // Allow hostnames without dots (local network names)
    if (!host.Contains('.'))
    {
        return true;
    }

    return false;
}

static bool IsPrivateOrLoopbackAddress(System.Net.IPAddress address)
{
    if (System.Net.IPAddress.IsLoopback(address))
    {
        return true;
    }

    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 169 && bytes[1] == 254);
    }

    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
    {
        var bytes = address.GetAddressBytes();
        var first = bytes[0];
        return first == 0xfc || first == 0xfd || // Unique local
               (first == 0xfe && (bytes[1] & 0xc0) == 0x80); // Link-local
    }

    return false;
}

sealed record FetchAllQueueResult(
    int ClearedPending,
    int PendingCount,
    bool WakeAttempted,
    Guid CheckCommandId,
    List<string> QueuedSteps);

internal sealed record EmployeeCreateRequest(
    string? EmployeeCode,
    string? FullName,
    string? Department,
    List<string>? DepartmentCodes,
    string? Position,
    string? Privilege,
    string? CardNumber,
    string? PictureDataUrl,
    string? MobileNumber,
    string? Gender,
    string? HireDate,
    string? Password,
    int? VerifyMode = null,
    int? GroupId = 1,
    string? TimezoneProfile = null,
    bool? IsActive = true);
internal sealed record EmployeeUpdateRequest(
    string? EmployeeCode,
    string? FullName,
    string? Department,
    List<string>? DepartmentCodes,
    string? Position,
    bool IsActive,
    string? Privilege,
    string? CardNumber,
    string? PictureDataUrl,
    string? MobileNumber,
    string? Gender,
    string? HireDate,
    string? Password,
    int? VerifyMode = null,
    int? GroupId = null,
    string? TimezoneProfile = null);
internal sealed record EmployeeDeleteRequest(List<Guid> Ids);
internal sealed record EmployeeCredentialDeleteRequest(string? EmployeeId, string? EmployeeCode, string CredentialType, List<string>? EmployeeIds = null);
internal sealed record EmployeeStatusRequest(List<Guid> Ids, bool IsActive);
internal sealed record EmployeeVerifyModeRequest(List<Guid> Ids, int VerifyMode);
internal sealed record EmployeeTransferRequest(List<Guid> Ids, List<string>? DepartmentCodes, string? Department = null);
internal sealed record EmployeeImportRequest(List<EmployeeImportRowRequest> Items, string? Mode = null);
internal sealed record EmployeeImportRowRequest(
    string? UserId,
    string? FullName,
    string? DepartmentName,
    bool IsActive,
    string? EmployeeCode = null,
    string? Department = null,
    string? AreaName = null,
    string? AreaCode = null,
    string? Area = null,
    string? DepartmentCode = null,
    string? CardNumber = null,
    string? Password = null);
internal sealed record EmployeePhotoImportRequest(List<EmployeePhotoImportRowRequest> Items, string? Mode = null);
internal sealed record EmployeePhotoImportRowRequest(string? EmployeeCode, string? PictureDataUrl);
internal sealed record FingerprintTemplateEntry(int FingerIndex, string Template);
internal sealed record DeviceCreateRequest(
    string? SerialNumber,
    string? Name,
    string? Model,
    string? IpAddress,
    string? Location,
    string? AreaCode,
    string? DepartmentCode,
    bool? RegistrationDevice,
    bool? ClearAllDataOnAdd = null);
internal sealed record DeviceUpdateRequest(
    string? Name,
    string? Model,
    string? IpAddress,
    string? Location,
    string? AreaCode,
    string? DepartmentCode,
    bool? RegistrationDevice,
    bool? ClearAllDataOnAdd = null);
internal sealed record DeviceCommandRequest(string? Command);
internal sealed record DeviceSyncSelectedRequest(List<Guid> EmployeeIds);
internal sealed record FetchAllDataRequest(bool? IncludeAttendanceLogs);
internal sealed record UniversalFaceEnrollmentRequest(
    string? DeviceSerial,
    string? UserId,
    string? UserName,
    string? CardNo,
    string? FaceType,
    int? Retry,
    bool? Overwrite,
    bool? ExistingUserOnly,
    int? VerifyMode = null);
internal sealed record FingerprintEnrollmentRequest(
    string? DeviceSerial,
    string? UserId,
    string? UserName,
    string? CardNo,
    int? FingerIndex,
    int? Retry,
    bool? Overwrite,
    int? VerifyMode = null);
internal sealed record CardEnrollmentRequest(
    string? DeviceSerial,
    string? UserId,
    string? UserName,
    string? CardNo,
    int? Retry,
    bool? Overwrite,
    int? VerifyMode = null);
internal sealed record DevicePresetCommandRequest(
    string Action,
    string? Pin,
    int? Fid,
    int? Retry,
    bool? Overwrite,
    string? OptionKey,
    string? OptionValue,
    string? StartTime,
    string? EndTime,
    string? TimeZone);
internal sealed record ClockRequest(Guid EmployeeId, Guid? DeviceId, string? Action);
internal sealed record AttendanceManualRequest(Guid EmployeeId, string? EmployeeCode, string? Status, DateTime? Timestamp, int? StatusCode, int? VerifyMode, string? Workcode, Guid? DeviceId, string? DeviceSerialNumber, string? Remarks);
internal sealed record AttendanceDeleteRequest(List<Guid> Ids);
internal sealed record AttendanceImportRequest(List<AttendanceImportRowRequest> Items);
internal sealed record AttendanceImportRowRequest(string? EmployeeCode, string? Status, DateTime? Timestamp, int? StatusCode, int? VerifyMode, string? Workcode, string? DeviceSerialNumber);
internal sealed record UpdatePortRequest(int Port, int? FrontendPort = null);
internal sealed record AuthLoginRequest(string? Username, string? Password);
internal sealed record AuthUserSaveRequest(
    string? Id,
    string? Username,
    string? FullName,
    string? Password,
    string? Role,
    bool? IsActive,
    Dictionary<string, bool>? FeatureAccess);
internal sealed record AuthUserResponse(
    string Id,
    string Username,
    string FullName,
    string Password,
    string Role,
    bool IsActive,
    Dictionary<string, bool> FeatureAccess,
    string CreatedAt,
    string UpdatedAt,
    string? Token = null);
internal sealed record EmployeeDepartmentRequest(string Department);
internal sealed record DepartmentRequest(string Code, string Name, string? Superior, string? Manager, string? AreaCode);
internal sealed record DepartmentDeleteRequest(List<Guid> Ids);
internal sealed record DepartmentImportRequest(List<DepartmentRequest> Items);
internal sealed record DepartmentTransferRequest(string FromDepartment, string ToDepartment);
internal sealed record PositionRequest(string Code, string Name, string? Department);
internal sealed record PositionDeleteRequest(List<Guid> Ids);
internal sealed record AreaRequest(string Code, string Name, string? Remark);
internal sealed record AreaDeleteRequest(List<Guid> Ids);
internal sealed class AreaCascadeDeleteResult
{
    public int AreasDeleted { get; set; }
    public int DepartmentsDeleted { get; set; }
    public int DevicesDeleted { get; set; }
    public int EmployeesDeleted { get; set; }
    public int PositionsDeleted { get; set; }
    public int QueuedDeviceDeletes { get; set; }
    public int DeletedArtifactRows { get; set; }

    public void Add(AreaCascadeDeleteResult other)
    {
        AreasDeleted += other.AreasDeleted;
        DepartmentsDeleted += other.DepartmentsDeleted;
        DevicesDeleted += other.DevicesDeleted;
        EmployeesDeleted += other.EmployeesDeleted;
        PositionsDeleted += other.PositionsDeleted;
        QueuedDeviceDeletes += other.QueuedDeviceDeletes;
        DeletedArtifactRows += other.DeletedArtifactRows;
    }
}
internal sealed record HolidayRequest(string? HolidayDate, string? HolidayName, string? Description, bool IsActive = true, bool IsRecurring = false);
internal sealed record HolidayListRow(int Id, string HolidayDate, string HolidayName, string Description, bool IsRecurring, bool IsActive, string Department);
internal sealed record LicenseTrialSnapshot(DateTimeOffset StartedUtc, DateTimeOffset ExpiryUtc, int TotalDays);
internal sealed record LicenseTrialFileModel(string StartedUtc, string ExpiryUtc, int TotalDays, string? LastCheckedUtc = null, string? Signature = null);
internal sealed record LicenseAccessSnapshot(
    LicenseValidationResult Validation,
    bool IsAccessGranted,
    bool IsTrialActive,
    int TrialDaysTotal,
    int TrialDaysRemaining,
    DateTimeOffset? TrialStartedUtc,
    DateTimeOffset? TrialExpiryUtc,
    string ActivationMode,
    string ActivationLabel,
    string ActivationMessage,
    DateTimeOffset CheckedAtUtc,
    DateTimeOffset? EffectiveExpiryUtc,
    string LicenseDirectory);

internal sealed record PersonnelDepartment(Guid Id, string Code, string Name, string Superior, string Manager, string AreaCode);
internal sealed record PersonnelPosition(Guid Id, string Code, string Name, string Department);
internal sealed record PersonnelArea(Guid Id, string Code, string Name, string Remark);
internal sealed record PersonnelDeviceAssignment(string DeviceSerial, string AreaCode, string DepartmentCode);
internal sealed record PersonnelEmployeeAssignment(string AreaCode, string DepartmentCode);
internal sealed record PersonnelEmployeeProfile(
    string EmployeeCode,
    string Privilege,
    string CardNumber,
    string PictureDataUrl,
    string MobileNumber,
    string Gender,
    string HireDate,
    string Password,
    string Position = "",
    string AreaCode = "",
    string DepartmentCode = "",
    int? GroupId = null,
    string TimezoneProfile = "",
    int VerifyMode = 0,
    List<PersonnelEmployeeAssignment>? Assignments = null);
internal sealed record AppLogEntry(DateTime TimestampUtc, string Level, string Source, string Message, string? Detail);

record BackupScheduleResponse(bool Enabled, string Frequency, string RunTime, string BackupLocation, int RetentionCount, string? LastRunUtc, string? NextRunUtc);
record BackupHistoryResponse(string Id, string FileName, long? FileSizeBytes, string Status, string Trigger, string? ErrorMessage, string StartedAtUtc, string? FinishedAtUtc);
record BackupScheduleSaveRequest(bool Enabled, string Frequency, string RunTime, string BackupLocation, int RetentionCount);
record EAttServicesSnapshot(DateTime CheckedAtUtc, string OverallStatus, List<EAttServiceStatus> Services);
record EAttServiceStatus(string Name, string Kind, string Status, string Message, DateTime CheckedAtUtc, Dictionary<string, object?> Metrics);
internal sealed record TransactionDetailPersistRequest(string? SerialNumber, string SourceTable, string Payload);
internal sealed record TransactionDetailRow(
    string Id,
    DateTime ReceivedUtc,
    string DeviceSerialNumber,
    string SourceTable,
    string RawLine,
    string? EmployeeCode,
    DateTime? PunchTime,
    int? StatusCode,
    int? VerifyMode,
    string? Workcode,
    int ParsedOk);

// Maps a short (<=16 char) wire command ID echoed by ZKTeco devices back to the full
// command Guid used internally for reply matching. ZKTeco firmware silently ignores any
// command whose ID exceeds 16 characters, so we cannot put a 32-char Guid on the wire.
// Bounded like commandSerialById: entries are removed when the device replies (the short
// ID is deterministically the Guid's first 13 hex chars, so removal needs no extra state).
internal static class CommandIdAlias
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Guid> Map = new();

    public static string ShortIdFor(Guid id) => id.ToString("N")[..13];
}

internal sealed class PersonnelStore
{
    public List<PersonnelDepartment> Departments { get; init; } = new();
    public List<PersonnelPosition> Positions { get; init; } = new();
    public List<PersonnelArea> Areas { get; init; } = new();
    public List<PersonnelDeviceAssignment> DeviceAssignments { get; init; } = new();
    public List<PersonnelEmployeeProfile> Profiles { get; init; } = new();

    public static PersonnelStore Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<PersonnelStore>(json);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
        }

        return new PersonnelStore();
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
internal sealed record DiscoveredDevice(
    string SerialNumber,
    string Name,
    string Model,
    string IpAddress,
    string Area,
    DateTime LastActivityUtc,
    bool IsOnline,
    int UserQty,
    int FpQty,
    int FaceQty,
    int PalmQty,
    int TransactionQty);

internal sealed record WebAppSettings(int Port, int FrontendPort)
{
    public static WebAppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<WebAppSettings>(json);
                if (loaded is not null)
                {
                    // Strictly use what is in the file. If ports are invalid (0 or out of range), 
                    // the bind check later will catch it.
                    return loaded;
                }
            }
        }
        catch { }

        // If no config file exists, use the application defaults (8081/8001)
        // until the Service Controller updates them.
        return new WebAppSettings(8081, 8001);
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}

internal static class ProgramHelpers
{
    private static bool LooksLikeMd5Hash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Regex.IsMatch(value.Trim(), "^[a-fA-F0-9]{32}$");
    }

    private static string ToMd5Hash(string plainText)
    {
        var input = plainText ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(input);
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static string NormalizePasswordHash(string? value)
    {
        var token = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        return LooksLikeMd5Hash(token) ? token.ToLowerInvariant() : ToMd5Hash(token);
    }

    public static bool IsSupportedVerifyMode(int verifyMode)
    {
        return verifyMode is >= -1 and <= 29 or 200;
    }

    public static int NormalizeEmployeeVerifyMode(int? verifyMode, int fallbackVerifyMode = -1)
    {
        if (verifyMode.HasValue && IsSupportedVerifyMode(verifyMode.Value))
        {
            return verifyMode.Value;
        }

        return IsSupportedVerifyMode(fallbackVerifyMode) ? fallbackVerifyMode : -1;
    }

    public static void UpsertEmployeeProfile(
        PersonnelStore store,
        string employeeCode,
        string? privilege,
        string? cardNumber,
        string? pictureDataUrl,
        string? mobileNumber,
        string? gender,
        string? hireDate,
        string? password,
        string? position = null,
        string? areaCode = null,
        string? departmentCode = null,
        int? groupId = null,
        string? timezoneProfile = null,
        int? verifyMode = null,
        IReadOnlyList<PersonnelEmployeeAssignment>? assignments = null)
    {
        static List<PersonnelEmployeeAssignment> NormalizeAssignments(IEnumerable<PersonnelEmployeeAssignment> items)
        {
            var result = new List<PersonnelEmployeeAssignment>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var normalizedDepartmentCode = item.DepartmentCode?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(normalizedDepartmentCode) || !seen.Add(normalizedDepartmentCode))
                {
                    continue;
                }

                result.Add(new PersonnelEmployeeAssignment(
                    item.AreaCode?.Trim() ?? string.Empty,
                    normalizedDepartmentCode));
            }

            return result;
        }

        var code = employeeCode.Trim();
        var existing = store.Profiles.FirstOrDefault(p => string.Equals(p.EmployeeCode, code, StringComparison.OrdinalIgnoreCase));
        var incomingPassword = password?.Trim() ?? string.Empty;
        var existingPassword = existing?.Password?.Trim() ?? string.Empty;
        var passwordForProfile = !string.IsNullOrWhiteSpace(incomingPassword) && !LooksLikeMd5Hash(incomingPassword)
            ? incomingPassword
            : (!string.IsNullOrWhiteSpace(existingPassword) && !LooksLikeMd5Hash(existingPassword)
                ? existingPassword
                : string.Empty);
        var verifyModeForProfile = NormalizeEmployeeVerifyMode(verifyMode, existing?.VerifyMode ?? -1);
        var groupIdForProfile = groupId ?? existing?.GroupId ?? 1;
        var timezoneProfileForProfile = timezoneProfile?.Trim() ?? existing?.TimezoneProfile ?? "0000000000000000";
        if (string.IsNullOrWhiteSpace(timezoneProfileForProfile)) timezoneProfileForProfile = "0000000000000000";
        var existingAssignments = NormalizeAssignments((existing?.Assignments ?? [])
            .Concat(!string.IsNullOrWhiteSpace(existing?.DepartmentCode)
                ? [new PersonnelEmployeeAssignment(existing.AreaCode ?? string.Empty, existing.DepartmentCode)]
                : []));
        var nextAssignments = assignments is not null
            ? NormalizeAssignments(assignments)
            : existingAssignments;
        if (assignments is null &&
            (!string.IsNullOrWhiteSpace(areaCode) || !string.IsNullOrWhiteSpace(departmentCode)))
        {
            nextAssignments = NormalizeAssignments(nextAssignments.Concat([
                new PersonnelEmployeeAssignment(areaCode?.Trim() ?? string.Empty, departmentCode?.Trim() ?? string.Empty)
            ]));
        }

        var primaryAssignment = nextAssignments.FirstOrDefault();
        var primaryAreaCode = primaryAssignment is not null && !string.IsNullOrWhiteSpace(primaryAssignment.DepartmentCode)
            ? primaryAssignment.AreaCode
            : areaCode?.Trim() ?? existing?.AreaCode ?? string.Empty;
        var primaryDepartmentCode = primaryAssignment is not null && !string.IsNullOrWhiteSpace(primaryAssignment.DepartmentCode)
            ? primaryAssignment.DepartmentCode
            : departmentCode?.Trim() ?? existing?.DepartmentCode ?? string.Empty;
        store.Profiles.RemoveAll(p => string.Equals(p.EmployeeCode, code, StringComparison.OrdinalIgnoreCase));
        store.Profiles.Add(new PersonnelEmployeeProfile(
            code,
            string.IsNullOrWhiteSpace(privilege) ? (existing?.Privilege ?? "User") : privilege.Trim(),
            cardNumber?.Trim() ?? existing?.CardNumber ?? string.Empty,
            pictureDataUrl?.Trim() ?? existing?.PictureDataUrl ?? string.Empty,
            mobileNumber?.Trim() ?? existing?.MobileNumber ?? string.Empty,
            gender?.Trim() ?? existing?.Gender ?? "Male",
            hireDate?.Trim() ?? existing?.HireDate ?? string.Empty,
            passwordForProfile,
            position?.Trim() ?? existing?.Position ?? string.Empty,
            primaryAreaCode,
            primaryDepartmentCode,
            groupIdForProfile,
            timezoneProfileForProfile,
            verifyModeForProfile,
            nextAssignments));
    }
}
