using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, configuration) =>
        configuration.ReadFrom.Configuration(context.Configuration));

    // One env var per service, so a backend can be pointed at a process running from source
    // without touching the route table.
    builder.Configuration.AddInMemoryCollection(
        GatewayRoutes.Clusters
            .Select(name => (name, url: builder.Configuration[$"SVC_{name.ToUpperInvariant()}_URL"]))
            .Where(x => !string.IsNullOrWhiteSpace(x.url))
            .ToDictionary(
                x => $"ReverseProxy:Clusters:{x.name}:Destinations:primary:Address",
                x => x.url));

    // Binding lives here rather than in Kestrel config so that an absent Tls path means no HTTPS
    // endpoint at all. Kestrel loads a configured certificate eagerly and fails to start if the
    // file is missing, which is not what an environment without certs wants.
    var httpPort = builder.Configuration.GetValue<int?>("Gateway:HttpPort") ?? 8080;
    var internalPort = builder.Configuration.GetValue<int?>("Gateway:InternalPort");
    var certPath = builder.Configuration["Tls:CertPath"];
    var keyPath = builder.Configuration["Tls:KeyPath"];

    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(httpPort);

        if (internalPort is int p && p != httpPort)
            options.ListenAnyIP(p);

        if (!string.IsNullOrWhiteSpace(certPath) && !string.IsNullOrWhiteSpace(keyPath))
        {
            var certificate = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            options.ListenAnyIP(443, listen => listen.UseHttps(certificate));
        }
    });

    builder.Services
        .AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

    builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
    builder.Services.AddHealthChecks();

    // Partitioned by client IP. The services' own limiter policies have no partition key, so
    // theirs are global buckets shared by every caller.
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.AddPolicy(GatewayRoutes.AuthLimiter, PartitionByClientIp(
            builder.Configuration.GetValue<int?>("RateLimiting:auth") ?? 10, internalPort));

        options.AddPolicy(GatewayRoutes.ApiLimiter, PartitionByClientIp(
            builder.Configuration.GetValue<int?>("RateLimiting:api") ?? 200, internalPort));
    });

    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    });

    var app = builder.Build();

    app.UseForwardedHeaders();
    app.UseSerilogRequestLogging();

    // certbot renews on a host cron and writes the challenge here.
    var acmeWebroot = app.Configuration["Acme:WebRoot"];
    if (!string.IsNullOrWhiteSpace(acmeWebroot) && Directory.Exists(acmeWebroot))
    {
        app.UseStaticFiles(new StaticFileOptions
        {
            RequestPath = "/.well-known/acme-challenge",
            FileProvider = new PhysicalFileProvider(acmeWebroot),
            ServeUnknownFileTypes = true,
            DefaultContentType = "text/plain"
        });
    }

    var canonicalHost = app.Configuration["CanonicalHost"];
    // Traffic arriving here is in-cluster — the frontend's server-side fetches — and must never be
    // redirected out to the public origin and back. Everything public arrives on :80 or :443.
    var internalPortForRedirects = internalPort ?? httpPort;

    if (!string.IsNullOrWhiteSpace(canonicalHost))
    {
        app.Use(async (context, next) =>
        {
            var isInternal = context.Connection.LocalPort == internalPortForRedirects;
            var isAcme = context.Request.Path.StartsWithSegments("/.well-known/acme-challenge");
            var wrongHost = !string.Equals(context.Request.Host.Host, canonicalHost,
                StringComparison.OrdinalIgnoreCase);

            if (!isInternal && !isAcme && (!context.Request.IsHttps || wrongHost))
            {
                context.Response.Redirect(
                    $"https://{canonicalHost}{context.Request.Path}{context.Request.QueryString}",
                    permanent: true);
                return;
            }

            await next();
        });
    }

    app.Use(async (context, next) =>
    {
        var headers = context.Response.Headers;
        // Inline scripts are Next's hydration; google.com/gstatic are reCAPTCHA v3; img-src is open
        // because avatars are user-supplied URLs and the map tiles come from OSM.
        headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self' 'unsafe-inline' https://www.google.com https://www.gstatic.com; " +
            "style-src 'self' 'unsafe-inline'; img-src * data: blob:; connect-src 'self' https://www.google.com; " +
            "frame-src https://www.google.com; font-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; " +
            "form-action 'self'; object-src 'none';";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        if (context.Request.IsHttps)
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains; preload";

        await next();
    });

    app.UseResponseCompression();

    // Public by construction — there is no identity at this layer, which is why upload keys are
    // unguessable rather than access-controlled.
    foreach (var (requestPath, configKey) in GatewayRoutes.UploadMounts)
    {
        var root = app.Configuration[configKey];
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Log.Information("Uploads: {Path} not served — {Key} is {Root}", requestPath, configKey,
                string.IsNullOrWhiteSpace(root) ? "unset" : $"'{root}' (missing)");
            continue;
        }

        Log.Information("Uploads: serving {Path} from {Root}", requestPath, root);

        app.UseStaticFiles(new StaticFileOptions
        {
            RequestPath = requestPath,
            FileProvider = new PhysicalFileProvider(root),
            OnPrepareResponse = ctx =>
            {
                var headers = ctx.Context.Response.Headers;
                headers.CacheControl = "public, max-age=3600";
                headers.ContentDisposition = "inline";
                headers["X-Frame-Options"] = "SAMEORIGIN";
            }
        });
    }

    // Must stay after the static files. StaticFileMiddleware stands aside once an endpoint is
    // selected, and the frontend catch-all matches every path — routing first means every avatar
    // is proxied to Next rather than served off disk.
    app.UseRouting();

    app.UseRateLimiter();

    // Routing only. Each service validates the token against identity's key set and owns the
    // permissions only it can answer.
    app.MapReverseProxy();
    app.MapHealthChecks("/healthz");

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Gateway terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>
/// Limits public traffic per client address. Requests arriving on the internal port are exempt:
/// they come from the frontend's server-side rendering, which shares one container address, so a
/// per-address bucket would be one budget for the entire user base — and /api/identity/me renders
/// on every page.
/// </summary>
static Func<HttpContext, RateLimitPartition<string>> PartitionByClientIp(
    int permitPerMinute, int? internalPort) =>
    context => internalPort is int port && context.Connection.LocalPort == port
        ? RateLimitPartition.GetNoLimiter("internal")
        : RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });

internal static class GatewayRoutes
{
    public const string AuthLimiter = "auth";
    public const string ApiLimiter = "api";

    /// <summary>Cluster ids, which double as the <c>SVC_&lt;NAME&gt;_URL</c> suffixes.</summary>
    public static readonly string[] Clusters =
        ["identity", "forum", "finance", "notifications", "household", "math", "geography", "media", "frontend"];

    /// <summary>Public path → the config key holding the directory behind it.</summary>
    public static readonly (string RequestPath, string ConfigKey)[] UploadMounts =
    [
        ("/uploads/avatars", "Uploads:Avatars"),
        ("/uploads/forum", "Uploads:Forum")
    ];
}
