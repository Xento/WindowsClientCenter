using System.Collections.ObjectModel;

namespace WindowsClientCenter.Shared.Diagnostics;

public enum ErrorCodeCategory
{
    Unknown,
    Windows,
    WindowsUpdate,
    Mdm,
    Intune,
    ConfigMgr,
    Cbs,
    Entra,
    Bits,
    DeliveryOptimization,
    AppInstall,
    Certificate,
    Network,
    Security,
    Registry,
    FileSystem,
    Msi,
    Psadt
}

public enum ErrorCodeSource
{
    WindowsSdk,
    MicrosoftLearn,
    CommunityCatalog,
    ManualResearch,
    RuntimeWin32,
    RuntimeHResult
}

public enum ErrorCodeConfidence
{
    Official,
    Community,
    Runtime
}

public sealed record ResolvedErrorCode(
    string NormalizedCode,
    int SignedDecimalValue,
    string? Symbol,
    string Description,
    ErrorCodeCategory Category,
    ErrorCodeSource Source,
    ErrorCodeConfidence Confidence,
    IReadOnlyList<string> Descriptions);

public sealed record DetectedErrorCode(
    int Start,
    int Length,
    string RawCode,
    ResolvedErrorCode Resolution);

internal readonly record struct ErrorCatalogEntry(
    uint Code,
    string? Symbol,
    string Description,
    ErrorCodeCategory Category,
    ErrorCodeSource Source,
    ErrorCodeConfidence Confidence);

internal static partial class ErrorCatalog
{
    internal static readonly ErrorCatalogEntry[] OfficialEntries =
    [
        new(0x00000000u, "S_OK", "The operation completed successfully.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x00000002u, "ERROR_FILE_NOT_FOUND", "The system cannot find the file specified.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x00000003u, "ERROR_PATH_NOT_FOUND", "The system cannot find the path specified.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x00000005u, "ERROR_ACCESS_DENIED", "Access is denied.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x0000000Du, "ERROR_INVALID_DATA", "The data is invalid.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x00000020u, "ERROR_SHARING_VIOLATION", "The process cannot access the file because it is being used by another process.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x00000032u, "ERROR_NOT_SUPPORTED", "The request is not supported.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x00000057u, "ERROR_INVALID_PARAMETER", "One or more parameters are invalid.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x80004005u, "E_FAIL", "Unspecified failure.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x80070002u, "ERROR_FILE_NOT_FOUND", "The system cannot find the file specified.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x80070003u, "ERROR_PATH_NOT_FOUND", "The system cannot find the path specified.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x80070005u, "E_ACCESSDENIED", "Access is denied.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x8007000Du, "ERROR_INVALID_DATA", "The data is invalid.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x80070020u, "ERROR_SHARING_VIOLATION", "The process cannot access the file because it is being used by another process.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x80070032u, "ERROR_NOT_SUPPORTED", "The request is not supported.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x80070057u, "E_INVALIDARG", "One or more arguments are invalid.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x80070422u, "ERROR_SERVICE_DISABLED", "The service is disabled.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x800706D9u, "RPC_S_NO_BINDINGS", "There are no more endpoints available from the endpoint mapper.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x80090016u, "NTE_BAD_KEYSET", "Keyset does not exist.", ErrorCodeCategory.Windows, ErrorCodeSource.WindowsSdk, ErrorCodeConfidence.Official),
        new(0x800F081Fu, "CBS_E_SOURCE_MISSING", "The source files could not be found.", ErrorCodeCategory.Cbs, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80180014u, "MENROLL_E_DEVICE_CERTIFICATEREQUEST_ERROR", "Device re-enrollment or reuse is blocked until the existing Intune or Autopilot device record is removed.", ErrorCodeCategory.Mdm, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80180018u, "MENROLL_E_USERLICENSE", "Intune enrollment failed because the user is missing a required license or has reached the device enrollment limit.", ErrorCodeCategory.Mdm, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80180023u, "MENROLL_E_PROV_SERVICE_DISCOVERY_FAILED", "MDM enrollment failed because the dmwappushservice service is missing or not working correctly.", ErrorCodeCategory.Mdm, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80180026u, "MENROLL_E_DEVICE_MANAGEMENT_BLOCKED", "MDM enrollment is blocked by policy.", ErrorCodeCategory.Mdm, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x8018002Au, "MENROLL_E_MDM_NOT_CONFIGURED", "Auto-enrollment failed because MFA or another interactive sign-in requirement blocked enrollment.", ErrorCodeCategory.Mdm, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x8018002Bu, "MENROLL_E_DEVICE_ALREADY_ENROLLED", "The device is already enrolled or stale enrollment artifacts are blocking auto-enrollment.", ErrorCodeCategory.Mdm, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80240001u, "WU_E_NO_SERVICE", "Windows Update Agent was unable to provide the service.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80240002u, "WU_E_MAX_CAPACITY_REACHED", "Maximum capacity of the service was exceeded.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80240003u, "WU_E_UNKNOWN_ID", "An ID was not recognized.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80240004u, "WU_E_NOT_INITIALIZED", "The object could not be initialized.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80240005u, "WU_E_RANGEOVERLAP", "The update handler requested a byte range overlapping a previously requested range.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80240008u, "WU_E_ITEMNOTFOUND", "The key for the item queried could not be found.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80240009u, "WU_E_OPERATIONINPROGRESS", "Another conflicting operation was in progress.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x8024000Du, "WU_E_XML_MISSINGDATA", "Windows Update XML is missing required data.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x8024000Eu, "WU_E_XML_INVALID", "Windows Update XML is invalid.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80240016u, "WU_E_INSTALL_NOT_ALLOWED", "Install is not allowed.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80240017u, "WU_E_NOT_APPLICABLE", "The update is not applicable to this computer.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80240018u, "WU_E_NO_USERTOKEN", "A user token was missing or invalid.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x8024001Au, "WU_E_POLICY_NOT_SET", "A policy value was not set.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x8024001Du, "WU_E_INVALID_UPDATE", "An update contains invalid metadata.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x8024001Eu, "WU_E_SERVICE_STOP", "A service stop is required.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80240022u, "WU_E_ALL_UPDATES_FAILED", "The operation failed for all updates.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80240024u, "WU_E_NO_UPDATE", "There are no updates.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80240025u, "WU_E_USER_ACCESS_DISABLED", "Group policy has disabled user access to Windows Update.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80240026u, "WU_E_INVALID_UPDATE_TYPE", "The type of update is invalid.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x8024002Eu, "WU_E_WU_DISABLED", "Windows Update access is disabled.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80244001u, "WU_E_PT_SOAPCLIENT_INITIALIZE", "The SOAP client failed to initialize.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80244004u, "WU_E_PT_SOAPCLIENT_CONNECT", "The SOAP client failed to connect.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80244005u, "WU_E_PT_SOAPCLIENT_SEND", "The SOAP client failed to send the request.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x80244006u, "WU_E_PT_SOAPCLIENT_SERVER", "The SOAP client received a server fault.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x8024401Cu, "WU_E_PT_HTTP_STATUS_REQUEST_TIMEOUT", "A connection to the server could not be established before the request timed out.", ErrorCodeCategory.WindowsUpdate, ErrorCodeSource.MicrosoftLearn, ErrorCodeConfidence.Official),
        new(0x82AC0201u, null, "Invalid parameter in SyncML command.", ErrorCodeCategory.Mdm, ErrorCodeSource.ManualResearch, ErrorCodeConfidence.Community),
        new(0x82AC0202u, null, "Command format not supported.", ErrorCodeCategory.Mdm, ErrorCodeSource.ManualResearch, ErrorCodeConfidence.Community),
        new(0x82AC0203u, null, "Access denied for requested operation.", ErrorCodeCategory.Mdm, ErrorCodeSource.ManualResearch, ErrorCodeConfidence.Community),
        new(0x82AC0204u, null, "Command failed or not allowed.", ErrorCodeCategory.Mdm, ErrorCodeSource.ManualResearch, ErrorCodeConfidence.Community),
        new(0x82AC0205u, null, "Target node not found.", ErrorCodeCategory.Mdm, ErrorCodeSource.ManualResearch, ErrorCodeConfidence.Community),
        new(0x82AC0206u, null, "Value rejected by CSP.", ErrorCodeCategory.Mdm, ErrorCodeSource.ManualResearch, ErrorCodeConfidence.Community),
        new(0x82AC0207u, null, "Command not executed due to dependency.", ErrorCodeCategory.Mdm, ErrorCodeSource.ManualResearch, ErrorCodeConfidence.Community),
        new(0x87D1FDE8u, null, "Remediation script execution failed.", ErrorCodeCategory.Intune, ErrorCodeSource.ManualResearch, ErrorCodeConfidence.Community),
        new(0x87D1FDE9u, null, "Detection script execution failed.", ErrorCodeCategory.Intune, ErrorCodeSource.ManualResearch, ErrorCodeConfidence.Community),
        new(0x800F0991u, "PSFX_E_MISSING_PAYLOAD_FILE", "The servicing stack could not find a required payload file for the update.", ErrorCodeCategory.Cbs, ErrorCodeSource.ManualResearch, ErrorCodeConfidence.Community)
    ];

    private static readonly ReadOnlyDictionary<uint, ErrorCatalogEntry> EntryMap = new(BuildEntryMap());
    internal static bool TryGetEntry(uint code, out ErrorCatalogEntry entry)
    {
        return EntryMap.TryGetValue(code, out entry);
    }

    private static Dictionary<uint, ErrorCatalogEntry> BuildEntryMap()
    {
        var map = new Dictionary<uint, ErrorCatalogEntry>(CommunityEntries.Length + OfficialEntries.Length);

        foreach (var entry in CommunityEntries)
        {
            map[entry.Code] = entry;
        }

        foreach (var entry in OfficialEntries)
        {
            map[entry.Code] = entry;
        }

        return map;
    }
}
