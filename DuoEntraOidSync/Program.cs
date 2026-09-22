using Azure.Identity;
using DuoEntraOidSync.Configuration;
using DuoEntraOidSync.Duo;
using DuoEntraOidSync.Graph;
using DuoEntraOidSync.Sync;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Adaptive sampling is on by default in the worker and is configured separately from
// the host's samplingSettings in host.json. One run an hour is nowhere near the sampling
// threshold, but the first live run can be, and that is the run whose record matters most.
builder.Services
    .AddApplicationInsightsTelemetryWorkerService(options => options.EnableAdaptiveSampling = false)
    .ConfigureFunctionsApplicationInsights();

// The App Insights logger provider registers a default filter rule that drops
// anything below Warning, and ConfigureFunctionsApplicationInsights takes worker
// logs off the host relay so host.json can't re-enable them. Without removing the
// rule, every Information-level line below (including the per-user change log) is
// discarded in the worker and never reaches App Insights.
builder.Services.Configure<LoggerFilterOptions>(options =>
{
    var defaultRule = options.Rules.FirstOrDefault(rule =>
        rule.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");

    if (defaultRule is not null)
    {
        options.Rules.Remove(defaultRule);
    }
});

// Options.
builder.Services.AddOptions<DuoOptions>()
    .Bind(builder.Configuration.GetSection(DuoOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<SyncOptions>()
    .Bind(builder.Configuration.GetSection(SyncOptions.SectionName));

// Microsoft Graph via managed identity (DefaultAzureCredential also covers local dev
// with az login / Visual Studio). Set Sync:ManagedIdentityClientId for a user-assigned MI.
builder.Services.AddSingleton(sp =>
{
    var sync = sp.GetRequiredService<IOptions<SyncOptions>>().Value;

    // Empty string != unset: a blank client id would push the credential down a
    // user-assigned path and break system-assigned MI after deploy. Treat it as null.
    var userAssignedClientId = string.IsNullOrWhiteSpace(sync.ManagedIdentityClientId)
        ? null
        : sync.ManagedIdentityClientId;

    var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ManagedIdentityClientId = userAssignedClientId,
    });

    return new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
});

builder.Services.AddSingleton<EntraUserService>();
// RemoveAllLoggers strips the IHttpClientFactory default per-request logging
// (Start/Sending/Received) for this client — otherwise every Duo call is logged.
builder.Services.AddHttpClient<DuoAdminClient>().RemoveAllLoggers();
builder.Services.AddSingleton<SyncService>();

builder.Build().Run();
