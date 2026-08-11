# Intune Session Host Plan

Status: Planned, not implemented.

## Purpose

Extend WindowsClientCenter with optional Microsoft Intune data while preserving its existing direct-client and offline workflows. Authentication must be comfortable during an interactive support session, but tokens must not be persisted by WindowsClientCenter or exposed to the host application and plugins.

The design uses a per-user, per-Windows-session launcher and tray application. It is explicitly not a Windows service and does not run as `SYSTEM`.

## Decisions

- Keep all existing direct-client features usable without the launcher, Internet access, or an Intune sign-in.
- Add three device access modes: `ClientOnly`, `Hybrid`, and `IntuneOnly`.
- Load Intune data only after an explicit user action. Navigation, timers, and background refreshes must not trigger an authentication prompt.
- Run one normal-user session host per user SID and Terminal Services session ID.
- Keep MSAL access, refresh, and ID tokens only in the session host's memory.
- Do not serialize the MSAL cache to a file, registry, DPAPI-protected blob, or another persistent store.
- Use the external system browser with authorization code flow and PKCE. Do not use WAM in the strict in-memory profile.
- Never return tokens, authorization headers, or unrestricted Graph access to WindowsClientCenter or its plugins.
- Prefer Microsoft Graph beta for the richer Intune reports selected for this feature, visibly label beta-backed data, and use v1.0 fallbacks for core device information where possible.
- Keep local AppX installation and removal tied to an accessible client. Initially expose only the existing explicit Intune device synchronization as a cloud action.

## Process Model

### Session Host

Add a normal desktop executable, tentatively named `WindowsClientCenter.SessionHost.exe`.

- It provides the launcher and notification-area UI.
- Its single-instance identity consists of the current user SID and Windows session ID.
- It owns the only live `IPublicClientApplication` instance and the only in-memory MSAL token cache.
- It performs authenticated Microsoft Graph requests and maps responses to bounded application DTOs.
- Closing its window minimizes it to the notification area.
- `Exit and clear Intune session` disposes the authentication state, clears cloud-result caches, disconnects clients, and terminates the process.
- Process exit, crash, or Windows logoff ends the ICC authentication session. A later start requires authentication again, although browser cookies outside ICC can still provide browser SSO.

The session host is not installed through the Windows Service Control Manager, does not use a service account, and does not require elevation for normal operation.

### Terminal Services Isolation

- Different users on one server receive separate processes, memory, pipes, authentication sessions, and result caches.
- Two concurrent sessions belonging to the same user also receive separate session hosts and must authenticate separately.
- Pipe and mutex names include a non-reversible SID-derived identifier plus the numeric Windows session ID.
- A disconnected or locked RDP session keeps its session host by default.
- `ClearSessionOnLockOrDisconnect` is an optional hardening setting and defaults to `false`.
- Windows logoff always clears the in-memory session by terminating the process.

### Host Startup

- Starting WindowsClientCenter without the session host continues in local/client-only operation.
- The first explicit Intune command detects a missing session host and offers `Start Intune session host`.
- Starting the session host does not automatically open a sign-in page. Sign-in remains a separate user action.
- The launcher can open additional WindowsClientCenter windows that share the same session while the session host remains alive.

## Authentication Lifecycle

### Initial Sign-In

1. The user selects `Sign in` in the tray or host UI.
2. The session host starts MSAL interactive authentication in the external system browser.
3. The request uses authorization code flow, PKCE, validated state and nonce values, and a loopback redirect on a free local port.
4. Credentials and MFA are entered only on Microsoft Entra pages. ICC never receives a password.
5. MSAL stores returned tokens only in its process-local memory cache.
6. Connected ICC clients receive a sanitized session state containing the UPN, tenant, status, and relevant timestamps, but no claims or tokens.

### Silent Use

- Every Graph operation first attempts silent token acquisition in the session host.
- Valid access tokens and refresh tokens can be reused while the tray process is alive.
- `Refresh session` performs a silent refresh only and never opens a browser.
- There is one token acquisition operation at a time to prevent concurrent refresh or sign-in races.

### Four-Hour Reauthentication

The deployment currently uses an approximate four-hour Conditional Access sign-in frequency. This is not treated as an exact local token-expiration timer. Entra remains authoritative and can require interaction earlier or later when a token is refreshed or a protected resource is accessed.

Configure `ExpectedInteractiveSignInFrequencyMinutes` to `240` for presentation only:

- Show `Reauthentication may be required soon` 15 minutes before the expected interval.
- Show `Reauthentication expected` after the interval.
- Do not open a browser and do not discard a token that Entra still accepts solely because the local display timer elapsed.

When MSAL returns a UI-required result or Graph returns a Conditional Access claims challenge:

1. The session host retains the challenge only in memory and returns `ReauthenticationRequired`.
2. ICC displays `Your Intune session requires reauthentication.` with `Reauthenticate and retry` and `Cancel`.
3. No browser opens automatically, including for a request the user just started.
4. Only `Reauthenticate and retry` starts interactive authentication.
5. The session host passes the retained challenge to MSAL with `WithClaims(...)`.
6. After successful authentication, the original operation is replayed exactly once.
7. Cancellation or another failure returns control without a prompt loop and leaves local features available.
8. The challenge is removed after success, cancellation, expiry, sign-out, or target change.

Build the MSAL client with the `cp1` client capability so supported resources can return actionable claims challenges.

### Sign-Out and Account Change

- `Sign out` removes accounts and tokens from the in-memory MSAL cache and clears all Intune result caches.
- All connected clients receive a session-state notification and remove displayed cloud data.
- `Switch account` is an explicit interactive operation and clears data associated with the previous tenant and account.
- Removing tokens locally does not revoke an already stolen access token at Entra. Server-side revocation remains an administrative incident-response action.

## IPC Security Boundary

The session host is a narrow Intune gateway, not a general token broker.

### Transport

- Use a versioned local named-pipe protocol.
- Restrict the pipe ACL to the current user in the current Windows session; deny network access.
- Include request IDs, protocol version, operation timeout, cancellation ID, and message-size limits.
- Reject malformed, oversized, unknown, or out-of-sequence messages before any Graph call.

### Client Verification

For every connection, resolve and verify:

- client process ID
- user SID
- Windows session ID
- absolute executable path
- Authenticode signature
- expected publisher

Production clients must be signed and installed in a directory that standard users cannot modify. A clearly marked development configuration may allow unsigned local builds; that relaxation must not be enabled in production packages.

### Allowed Operations

Expose typed commands only, for example:

- `GetSessionStatus`
- `SignIn`
- `RefreshSession`
- `Reauthenticate`
- `SignOut`
- `SearchDevices`
- `LoadDeviceOverview`
- `LoadUsers`
- `LoadCompliance`
- `LoadConfigurations`
- `LoadManagedApplications`
- `LoadDiscoveredApplications`
- `SyncDevice`

Do not expose commands for:

- retrieving a token
- selecting arbitrary scopes
- sending an arbitrary URL
- selecting an arbitrary HTTP method
- supplying arbitrary headers or request bodies
- returning raw authentication results

The session host normalizes and bounds Graph responses before sending them to a client.

## Public Contracts

Add a small protocol/contract assembly shared by the session host and host application.

### Authentication and Gateway

```csharp
public interface IIntuneSessionHostClient
{
    Task<IntuneSessionSnapshot> GetSessionAsync(CancellationToken cancellationToken);
    Task<IntuneOperationResult> SignInAsync(CancellationToken cancellationToken);
    Task<IntuneOperationResult> RefreshSessionAsync(CancellationToken cancellationToken);
    Task<IntuneOperationResult> ReauthenticateAsync(string challengeId, CancellationToken cancellationToken);
    Task<IntuneOperationResult> SignOutAsync(CancellationToken cancellationToken);
    event EventHandler<IntuneSessionChangedEventArgs>? SessionChanged;
}

public interface IIntuneGateway
{
    Task<IntuneOperationResult<IReadOnlyList<IntuneDeviceSearchResult>>> SearchDevicesAsync(
        string searchText,
        CancellationToken cancellationToken);

    Task<IntuneOperationResult<IntuneDeviceOverview>> GetDeviceOverviewAsync(
        string managedDeviceId,
        CancellationToken cancellationToken);

    Task<IntuneOperationResult<IReadOnlyList<IntuneUserAssignment>>> GetUsersAsync(
        string managedDeviceId,
        CancellationToken cancellationToken);

    Task<IntuneOperationResult<IReadOnlyList<IntuneComplianceResult>>> GetComplianceAsync(
        string managedDeviceId,
        CancellationToken cancellationToken);

    Task<IntuneOperationResult<IReadOnlyList<IntuneConfigurationResult>>> GetConfigurationsAsync(
        string managedDeviceId,
        CancellationToken cancellationToken);

    Task<IntuneOperationResult<IReadOnlyList<IntuneApplicationResult>>> GetManagedApplicationsAsync(
        string managedDeviceId,
        string? userId,
        CancellationToken cancellationToken);
}
```

`IntuneOperationResult<T>` distinguishes at least:

- `Success`
- `NotSignedIn`
- `ReauthenticationRequired`
- `PermissionMissing`
- `Forbidden`
- `NotFound`
- `Throttled`
- `Offline`
- `SchemaChanged`
- `Cancelled`
- `Error`

A claims challenge remains inside the session host and is represented to ICC only by a short-lived opaque challenge ID.

### Device Target Context

Add `IDeviceTargetContextService` so a selected device no longer depends on a successful WinRM connection.

The context contains:

- entered device name
- `DeviceAccessMode` (`ClientOnly`, `Hybrid`, `IntuneOnly`)
- reachable direct host, when available
- Intune managed-device ID, when resolved
- Entra device ID, when available
- display name, serial number, and platform
- target-context version

A target change cancels outstanding operations and causes late results from an older context version to be ignored.

Existing authentication and token-provider interfaces receive compatibility adapters during migration. Live Graph services move behind `IIntuneGateway`; mock and demo implementations can remain in process.

## Intune Data Model and UI

### Device Selection

- Preserve the entered hostname when direct connectivity fails.
- Offer `Retry client connection` and `Find in Intune` as separate actions.
- Search Intune by device name, serial number, or user UPN.
- Require an explicit selection when several devices match.
- Use the stable managed-device ID for subsequent requests.

### Navigation

Expose stateful Intune Agent navigation entries through the existing plugin navigation tree:

- Intune Overview
- Managed Applications
- Discovered Applications
- Configurations
- Compliance
- Users

### Information Placement

- Device Overview: primary user, compliance, last Intune check-in, platform, and source timestamp.
- Installed Software and AppX Applications: separate, manually loaded `Intune reported` sections.
- Intune Agent: full application, configuration, compliance, and user detail.
- Device Actions: cloud actions visibly separated from direct-client actions.

Do not silently merge local and cloud rows. Display `Local`, `Intune`, or `Cached`, the collection/report time, and any relevant user/device context. Show conflicting local and cloud states side by side. Only mark an application match as definite when stable identifiers support it; name-based matches must be labeled as inferred.

### Data Categories

- Overview: Intune and Entra IDs, serial number, model, OS/build, ownership, enrollment, management agent, compliance, and last check-in.
- Users: primary user, enrollment user, and other reported users as distinct roles.
- Compliance: overall state, grace period, policy states, affected settings, last report, and errors.
- Configurations: Settings Catalog, classic device configurations, administrative templates, and endpoint security profiles normalized to common states.
- Managed Applications: assignment intent, device/user context, version, installation status, error details, and last report.
- Discovered Applications: separate inventory data that does not imply an Intune assignment.

Use normalized states such as `Succeeded`, `Error`, `Conflict`, `Pending`, `NotApplicable`, and `Unknown`, while retaining the original Graph value for diagnostics.

## Caching and Privacy

- Token cache: session-host memory only.
- Intune result cache: memory only, keyed by tenant, account, managed-device ID, user context, and data section.
- Default result TTL: five minutes.
- Explicit refresh bypasses the result cache.
- After a network failure, already loaded data can remain visible as `Cached` until sign-out or process exit.
- Do not persist Intune IDs, UPNs, policy results, application states, or Graph payloads in recent-device lists.
- Clear cloud data on sign-out, account change, target change where applicable, or session-host exit.
- Never log tokens, authorization headers, claims challenges, or complete Graph payloads.

## Configuration

Add configuration equivalent to:

```json
{
  "Intune": {
    "Cloud": {
      "Enabled": true,
      "PreferredApiVersion": "beta",
      "AllowV1Fallback": true,
      "AutoLoad": false,
      "ResultCacheMinutes": 5,
      "ExpectedInteractiveSignInFrequencyMinutes": 240,
      "ReauthenticationWarningMinutes": 15,
      "ClearSessionOnLockOrDisconnect": false,
      "AllowUnsignedDevelopmentClients": false
    }
  }
}
```

Keep the existing runtime mode (`Mock`, `Demo`, or `Live`) separate from the selected device access mode.

## Entra and Intune Prerequisites

- Configure the Entra application as a public client with the required loopback redirect behavior.
- Grant delegated read permissions separately from privileged remote-action permissions.
- Expected read permissions include:
  - `DeviceManagementManagedDevices.Read.All`
  - `DeviceManagementConfiguration.Read.All`
  - `DeviceManagementApps.Read.All`
- Request `DeviceManagementManagedDevices.PrivilegedOperations.All` only for supported cloud actions.
- Confirm tenant admin consent, Conditional Access behavior, and Intune RBAC roles in a test tenant.
- Pilot beta-backed report adapters because Microsoft can change beta response schemas.

## Delivery Sequence

1. Add the shared protocol assembly and session-host executable without changing existing local workflows.
2. Implement strict in-memory MSAL authentication, tray commands, and reauthentication state handling.
3. Implement secured named-pipe transport, client verification, and typed gateway commands.
4. Introduce the device target context and the three access modes.
5. Add Intune device search, overview, and user data.
6. Add compliance, configuration, managed-application, and discovered-application adapters.
7. Add Intune navigation entries and optional cloud sections to existing views.
8. Add the explicitly allowed cloud actions and incremental privileged consent.
9. Add production signing checks, package integration, administrator documentation, and test-tenant validation.

Each phase must keep the direct-client path working without the session host.

## Acceptance and Security Tests

### Authentication

- Starting ICC without the session host causes no authentication or Graph traffic.
- Multiple ICC windows in one Windows session share the in-memory authentication state.
- Restarting only ICC preserves the session while the session host remains alive.
- Restarting or crashing the session host requires authentication again.
- No ICC token-cache files or registry values exist after sign-in, sign-out, crash, or exit.
- `Refresh session` never opens a browser.

### Reauthentication

- Silent requests succeed while Entra accepts the session.
- A simulated UI-required result or claims challenge displays only the reauthentication notice.
- No request, timer, navigation event, or background refresh opens an authentication window automatically.
- `Reauthenticate and retry` performs one interactive flow and replays the original operation once.
- Cancellation and failure do not create a prompt loop.
- Concurrent requests produce one shared reauthentication-required state.

### IPC and Terminal Services

- Different users cannot connect to each other's pipes.
- Two sessions for the same user remain isolated.
- A client with the wrong session ID, path, signature, or publisher is rejected.
- Unknown, malformed, or oversized messages are rejected without a Graph call.
- No protocol operation can retrieve a token or send an arbitrary Graph request.

### Device and UI Behavior

- Client-only mode retains all existing direct functionality.
- An unreachable client can be located and opened in Intune-only mode.
- Hybrid mode does not load cloud data until explicitly requested.
- A target change cancels or ignores stale responses.
- Local AppX actions are disabled with a clear reason in Intune-only mode.
- Source, report time, user/device context, cached state, and beta usage are visible.
- `401`, `403`, `404`, `429`, network errors, and beta schema changes affect only the relevant section.

### Validation Boundary

Run focused unit and ViewModel tests first, then build and test the affected Windows projects serially through `./scripts/dotnet-win.sh`. Mocked authentication and Graph responses are necessary but not sufficient: MFA, Conditional Access, Intune RBAC, concurrent terminal sessions, signing checks, and real beta payloads require validation in a dedicated test tenant and supported Windows Server environment.

## Residual Risk

This design materially reduces token persistence, token forwarding, and cross-session exposure, but it cannot make token theft impossible. A local administrator, malware running as the same user, process injection, or a sensitive system crash dump can still expose process memory or misuse an authenticated process.

Production deployment therefore also depends on:

- signed binaries in a user-nonwritable installation directory
- no local administrator rights for normal operators
- Defender for Endpoint or equivalent EDR
- least-privilege Intune RBAC and delegated scopes
- separate authorization for read and remote-action capabilities
- risk-based Conditional Access
- monitoring of unusual Entra sign-ins and Graph activity
- documented session-revocation and incident-response procedures
