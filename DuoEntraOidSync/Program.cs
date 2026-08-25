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
using Microsoft.Extensions.Options;
using Microsoft.Graph;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

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
