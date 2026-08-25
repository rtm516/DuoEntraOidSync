# Duo-Entra oid alias sync

Azure Function that stamps each Duo user's Entra ID object id (`oid`) into a Duo
username alias slot, so Duo can match users on their `oid` - needed for Entra
External Authentication Method logins, including guest / cross-tenant.

## How it works

A single timer-triggered function, `Sync`, runs on a configurable schedule
(default `0 30 * * * *` - half past every hour). Each run:

1. Pulls the full Entra user index (`id`, `userPrincipalName`, `mail`) via Microsoft Graph.
2. Pulls all Duo users via the Admin API.
3. Joins in memory: the UPN in Duo `alias1` is looked up in the Entra index.
4. Writes the Entra `oid` into the target Duo alias slot **only when it differs** from
   the current value (idempotent). Only that one slot is written, so `alias1` and the
   others are never clobbered.

The Entra index is keyed by **both** UPN and mail (UPN wins on collision), so guest
users whose Duo `alias1` holds their real email - rather than the synthetic
`#EXT#` UPN - still resolve.

No webhooks/queues/subscriptions: Duo has no user-lifecycle webhooks and Duo
directory sync is twice daily, so a periodic full pass is the model.

## Deploy to Azure

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Frtm516%2FDuoEntraOidSync%2Fmaster%2Fdeploy%2Fazuredeploy.json)

The ARM template is at [`deploy/azuredeploy.json`](deploy/azuredeploy.json) with an
example parameters file at [`deploy/azuredeploy.parameters.json`](deploy/azuredeploy.parameters.json).

### What the template provisions (permissions included)

- **Function App** (.NET 10 isolated) on a **Consumption (Y1)** plan, with a
  **system-assigned managed identity**.
- **Storage account** (required by Functions) and **Application Insights** backed by a
  **Log Analytics** workspace (30-day retention).
- **Key Vault** (RBAC mode) holding the Duo `ikey` / `skey` as secrets, referenced from
  app settings as `@Microsoft.KeyVault(...)` - the app never sees raw secret values.
- **Azure RBAC set up out of the box:** the Function's identity is granted
  **Key Vault Secrets User** on the vault automatically, so the secret references
  resolve with no manual step.
- **Function code** is pulled from `packageUri` (a published release zip) via
  run-from-package, so the deploy includes the app - no separate code push.

All app settings (alias slots, schedule, dry-run, Duo host) are template parameters.

> **Deployer needs** `Owner` or `User Access Administrator` on the target resource
> group - the template creates a role assignment, which requires that right.

### Parameters

| Parameter | Default | Notes |
|-----------|---------|-------|
| `functionAppName` | `duo-oid-sync-<unique>` | Globally-unique app name. |
| `duoIntegrationKey` | (required, secure) | Duo Admin API ikey -> Key Vault secret. |
| `duoSecretKey` | (required, secure) | Duo Admin API skey -> Key Vault secret. |
| `duoApiHost` | (required) | `api-XXXXXXXX.duosecurity.com` (no scheme). |
| `duoUpnAliasSlot` | `alias1` | Slot holding the UPN join key. |
| `duoOidAliasSlot` | `alias2` | Slot the oid is written to. Must differ from the UPN slot. |
| `timerSchedule` | `0 30 * * * *` | NCRONTAB schedule for the run. |
| `packageUri` | latest GitHub release zip | Built function package the app runs from. Override to pin a version or use your own build. |
| `dryRun` | `true` | Logs intended writes without calling Duo. Leave on for the first run. |

## Post-deploy steps

Deploying with the button (or the template) provisions everything and pulls the function
code from `packageUri`, so only two steps remain.

### 1. Grant the one Microsoft Graph permission (admin, one-time)

ARM handles all *Azure* RBAC, but Graph `User.Read.All` is a **directory-plane** app
permission that only a directory admin can grant - it can't be self-bootstrapped in a
template. Run this once (needs the **Microsoft.Graph** PowerShell module and a
**Global Administrator** or **Privileged Role Administrator**). Use the
`functionAppPrincipalId` from the deployment outputs:

```powershell
$principalId = "<functionAppPrincipalId from deployment outputs>"

Connect-MgGraph -Scopes "AppRoleAssignment.ReadWrite.All","Application.Read.All"
$graph = Get-MgServicePrincipal -Filter "appId eq '00000003-0000-0000-c000-000000000000'"
$role  = $graph.AppRoles | Where-Object { $_.Value -eq "User.Read.All" -and $_.AllowedMemberTypes -contains "Application" }
New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $principalId -PrincipalId $principalId -ResourceId $graph.Id -AppRoleId $role.Id
```

### 2. Verify with a dry run, then go live

The app deploys with `dryRun=true`. Trigger a run (or wait for the schedule) and check
App Insights -> Logs for the summary:

```kusto
traces | where message startswith "Sync complete" | order by timestamp desc
```

Confirm the `matched` / `unmatched` counts look right, and that the target alias slot
isn't managed by **Duo directory sync** (see the warning below). Then set the
`Sync__DryRun` app setting to `false` for live writes.

> **Warning: confirm before first live run.** If the target alias slot is managed by Duo
> directory sync, API writes are reverted on the next sync. Confirm the slot is free, or
> map the oid via the sync attribute config instead.

## Building the release package

The default `packageUri` points at the latest GitHub release asset
(`DuoEntraOidSync.zip`), so a release must exist for one-click deploy to include code.
Build and package it with:

```bash
dotnet publish DuoEntraOidSync -c Release -o publish
cd publish && zip -r ../DuoEntraOidSync.zip . && cd ..
```

Attach `DuoEntraOidSync.zip` to a GitHub release (or host it anywhere the Function App
can reach and set `packageUri` to that URL / SAS). To push a build to an
already-deployed app instead of using run-from-package, publish from Visual Studio
(right-click -> **Publish**) or with Core Tools:

```bash
func azure functionapp publish <functionAppName>
```

## Configuration reference

App settings (double-underscore = config-section nesting). Set by the template; editable
in the portal afterwards.

| Setting | Default | Notes |
|---------|---------|-------|
| `Duo__IntegrationKey` | (required) | Duo ikey. Key Vault reference. |
| `Duo__SecretKey` | (required) | Duo skey (HMAC key). Key Vault reference. |
| `Duo__ApiHost` | (required) | `api-XXXXXXXX.duosecurity.com` (no scheme). |
| `Sync__DuoUpnAliasSlot` | `alias1` | Slot holding the UPN join key. |
| `Sync__DuoOidAliasSlot` | `alias2` | Slot the oid is written to. Must differ from the UPN slot. |
| `Sync__Schedule` | `0 30 * * * *` | NCRONTAB schedule for the `Sync` timer. |
| `Sync__DryRun` | `false` | When `true`, logs intended writes without calling Duo. |
| `Sync__ManagedIdentityClientId` | (optional) | Set only for a *user-assigned* MI; omit for system-assigned. |

## Local development

`local.settings.json` (git-ignored) holds local values, including `Sync__Schedule`.
Graph auth locally uses `DefaultAzureCredential` - sign in via **Tools -> Options ->
Azure Service Authentication** in Visual Studio (or `az login`) with an account that can
read the directory. In Debug builds the timer has `RunOnStartup` enabled, so F5 fires a
run immediately; leave `Sync__DryRun=true` unless you mean to write to the real Duo tenant.

```bash
func start
```

## Project layout

| Path | Purpose |
|------|---------|
| `SyncFunction.cs` | Timer trigger (`%Sync__Schedule%`). |
| `Sync/SyncService.cs` | Join + idempotent-write logic, run stats. |
| `Graph/EntraUserService.cs` | Builds the UPN/mail -> oid index via Graph. |
| `Duo/DuoAdminClient.cs` | Hand-rolled HMAC-SHA1 signed Duo Admin API client. |
| `Configuration/` | `DuoOptions` (credentials), `SyncOptions` (slots, schedule, dry-run, MI). |
| `deploy/` | ARM template + example parameters. |

## Attribution

This project was written by Claude (Anthropic's Claude Code).
