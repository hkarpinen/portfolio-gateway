using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, configuration) =>
        configuration.ReadFrom.Configuration(context.Configuration));

    // Destinations come from one env var per service so a single backend can be pointed somewhere
    // else — at a container, at a process running from source on the host — without touching the
    // route table. The nested `ReverseProxy__Clusters__x__Destinations__primary__Address` form
    // works too, but nobody wants to type it into a compose file.
    builder.Configuration.AddInMemoryCollection(
        GatewayRoutes.Services
            .Select(name => (name, url: builder.Configuration[$"SVC_{name.ToUpperInvariant()}_URL"]))
            .Where(x => !string.IsNullOrWhiteSpace(x.url))
            .ToDictionary(
                x => $"ReverseProxy:Clusters:{x.name}:Destinations:primary:Address",
                x => x.url));

    builder.Services
        .AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

    builder.Services.AddHealthChecks();

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    // The gateway routes; it does not authorise. Deciding here which paths tolerate an anonymous
    // caller would mean holding a copy of every service's endpoint list, which is the duplication
    // this project exists to remove. Each service already validates the token itself against
    // identity's key set, and each owns the permissions only it can answer.
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

internal static class GatewayRoutes
{
    /// <summary>Cluster ids, which double as the <c>SVC_&lt;NAME&gt;_URL</c> suffixes.</summary>
    public static readonly string[] Services =
        ["identity", "forum", "finance", "notifications", "household", "math", "geography"];
}
