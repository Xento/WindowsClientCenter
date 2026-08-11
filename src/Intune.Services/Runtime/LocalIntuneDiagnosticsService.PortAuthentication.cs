using System.Text.Json;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed partial class LocalIntuneDiagnosticsService
{
    public async ValueTask<PortAuthenticationSnapshot?> GetPortAuthenticationSnapshotAsync(string host, CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteForHostAsync(host, BuildPortAuthenticationSnapshotScript(), cancellationToken);
        return ParsePortAuthenticationOnlySnapshot(execution);
    }

    private static PortAuthenticationSnapshot ParsePortAuthenticationOnlySnapshot(PowershellExecutionResult execution)
    {
        if (execution.ExitCode != 0)
        {
            throw new InvalidOperationException(NormalizeError(execution));
        }

        try
        {
            if (!TryParsePowerShellJsonDocument(execution.StdOut, out var document, out _, out var parseError))
            {
                throw new InvalidOperationException(parseError);
            }

            using var parsedDocument = document;
            var payload = document.RootElement.Deserialize<PortAuthenticationOnlyPayload>(JsonOptions)
                          ?? throw new InvalidOperationException("Port authentication payload was empty.");
            return ParsePortAuthenticationSnapshot(payload.PortAuthentication)
                   ?? throw new InvalidOperationException("Port authentication payload was empty.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Port authentication parsing failed: {ex.Message}", ex);
        }
    }

    private static PortAuthenticationSnapshot? ParsePortAuthenticationSnapshot(PortAuthenticationPayload? payload)
    {
        if (payload is null)
        {
            return null;
        }

        var capturedAt = ParseCapturedAtUtc(payload.CapturedAtUtc) ?? DateTimeOffset.UtcNow;
        return new PortAuthenticationSnapshot(
            capturedAt,
            payload.OverallStatusText ?? "Unknown",
            payload.OverallStatusLevel ?? "Unknown",
            payload.OverallDetailText ?? "Port authentication status is not available.",
            payload.ApplicabilityText ?? "Unknown",
            payload.Fqdn ?? string.Empty,
            payload.ActiveInterfaceName ?? string.Empty,
            payload.ActiveInterfaceDescription ?? string.Empty,
            payload.AuthenticationStateText ?? "Unknown",
            payload.TracingModeText ?? "Unknown",
            payload.LastSuccessfulAuthenticationText ?? "No successful wired authentication event found.",
            (payload.Checks ?? [])
                .Select(static item => new PortAuthenticationCheckEntry(
                    item.Name ?? string.Empty,
                    item.StatusText ?? "Unknown",
                    item.StatusLevel ?? "Unknown",
                    item.Detail ?? string.Empty))
                .ToArray(),
            (payload.Profiles ?? [])
                .Select(static item => new PortAuthenticationProfileEntry(
                    item.Name ?? string.Empty,
                    item.InterfaceName ?? string.Empty,
                    item.AuthMode ?? string.Empty,
                    item.SsoMode ?? string.Empty,
                    item.OneXEnabledText ?? string.Empty,
                    item.OneXEnforcedText ?? string.Empty,
                    item.EapType ?? string.Empty,
                    item.ParseStatusText ?? string.Empty,
                    item.StatusLevel ?? "Unknown"))
                .ToArray(),
            (payload.Certificates ?? [])
                .Select(static item => new PortAuthenticationCertificateEntry(
                    item.Subject ?? string.Empty,
                    item.SanDns ?? string.Empty,
                    item.Thumbprint ?? string.Empty,
                    item.Issuer ?? string.Empty,
                    item.StoreName ?? string.Empty,
                    item.HasPrivateKeyText ?? string.Empty,
                    item.ValidityText ?? string.Empty,
                    item.ChainStatusText ?? string.Empty,
                    item.FqdnMatchText ?? string.Empty,
                    item.StatusLevel ?? "Unknown"))
                .ToArray(),
            (payload.Events ?? [])
                .Select(item => new PortAuthenticationEventEntry(
                    ParseTimestamp(item.TimeCreatedUtc),
                    item.LogName ?? string.Empty,
                    item.Id,
                    item.Level ?? string.Empty,
                    item.StatusLevel ?? "Unknown",
                    item.Summary ?? string.Empty,
                    item.RecommendedAction ?? string.Empty,
                    item.Message ?? string.Empty))
                .OrderByDescending(static item => item.TimeCreated ?? DateTimeOffset.MinValue)
                .ToArray());
    }

    private string BuildPortAuthenticationSnapshotScript()
    {
        var escapedVpnAdapterDescriptionMatch = _vpnAdapterDescriptionMatch.Replace("'", "''", StringComparison.Ordinal);
        return $$"""
        $ErrorActionPreference = 'Stop'
        {{BuildPortAuthenticationCommonScriptHelpers()}}

        try {
          $vpnAdapterMatch = '{{escapedVpnAdapterDescriptionMatch}}'
          $vpnStatusText = 'Not detected'

          try {
            foreach ($adapter in @(Get-NetAdapter -ErrorAction Stop)) {
              $description = Convert-ToDisplayString (Get-FirstPropertyValue $adapter @('InterfaceDescription', 'Name', 'InterfaceAlias'))
              if ([string]::IsNullOrWhiteSpace($description)) { continue }
              if (-not [string]::IsNullOrWhiteSpace($vpnAdapterMatch) -and $description -like ('*' + $vpnAdapterMatch + '*')) {
                $vpnStatusText = if ($adapter.Status -eq 'Up') { 'Connected' } else { 'Adapter detected' }
              }
            }
          } catch {
          }

          [ordered]@{
            PortAuthentication = Get-IccPortAuthenticationSnapshot -VpnStatusText $vpnStatusText
          } | ConvertTo-Json -Depth 10 -Compress
        } catch {
          $details = Get-IccPortAuthErrorDetails 'BuildPortAuthenticationSnapshotScript' $_
          Write-Error -Message $details
        }
        """;
    }

    private static string BuildPortAuthenticationCommonScriptHelpers() =>
        """
        function Get-FirstPropertyValue($obj, [string[]]$names) {
          if ($null -eq $obj -or $null -eq $names) { return $null }
          foreach ($name in $names) {
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            $prop = $obj.PSObject.Properties[$name]
            if ($null -eq $prop -or $null -eq $prop.Value) { continue }
            if ($prop.Value -is [string] -and [string]::IsNullOrWhiteSpace($prop.Value)) { continue }
            return $prop.Value
          }
          return $null
        }
        function Convert-ToDisplayString($value) {
          if ($null -eq $value) { return '' }
          if ($value -is [string]) { return $value.Trim() }
          return [string]$value
        }
        function Get-IccXmlNodeInnerText($xml, [string]$xpath) {
          if ($null -eq $xml -or [string]::IsNullOrWhiteSpace($xpath)) { return '' }
          try {
            $node = $xml.SelectSingleNode($xpath)
            if ($null -eq $node) { return '' }
            return Convert-ToDisplayString $node.InnerText
          } catch {
            return ''
          }
        }
        function Test-IccEventLogAvailable([string]$logName) {
          try { return $null -ne (Get-WinEvent -ListLog $logName -ErrorAction SilentlyContinue) } catch { return $false }
        }
        function Get-IccPortAuthErrorDetails([string]$stepName, $errorRecord) {
          $effectiveStep = if ([string]::IsNullOrWhiteSpace($stepName)) { 'UnknownStep' } else { $stepName }
          $lines = New-Object System.Collections.Generic.List[string]
          $message = if ($null -ne $errorRecord -and $null -ne $errorRecord.Exception) {
            Convert-ToDisplayString $errorRecord.Exception.Message
          } else {
            Convert-ToDisplayString $errorRecord
          }

          $lines.Add("Port authentication script step '$effectiveStep' failed: $message") | Out-Null

          if ($null -ne $errorRecord) {
            if ($null -ne $errorRecord.Exception) {
              $lines.Add('ExceptionType: ' + $errorRecord.Exception.GetType().FullName) | Out-Null
            }

            if ($null -ne $errorRecord.CategoryInfo) {
              $category = Convert-ToDisplayString $errorRecord.CategoryInfo
              if (-not [string]::IsNullOrWhiteSpace($category)) {
                $lines.Add('Category: ' + $category) | Out-Null
              }
            }

            $fullyQualifiedErrorId = Convert-ToDisplayString $errorRecord.FullyQualifiedErrorId
            if (-not [string]::IsNullOrWhiteSpace($fullyQualifiedErrorId)) {
              $lines.Add('FullyQualifiedErrorId: ' + $fullyQualifiedErrorId) | Out-Null
            }

            if ($null -ne $errorRecord.InvocationInfo) {
              $scriptName = Convert-ToDisplayString $errorRecord.InvocationInfo.ScriptName
              if (-not [string]::IsNullOrWhiteSpace($scriptName)) {
                $lines.Add('ScriptName: ' + $scriptName) | Out-Null
              }

              $invocationName = Convert-ToDisplayString $errorRecord.InvocationInfo.InvocationName
              if (-not [string]::IsNullOrWhiteSpace($invocationName)) {
                $lines.Add('InvocationName: ' + $invocationName) | Out-Null
              }

              $line = Convert-ToDisplayString $errorRecord.InvocationInfo.Line
              if (-not [string]::IsNullOrWhiteSpace($line)) {
                $lines.Add('InvocationLine: ' + $line) | Out-Null
              }

              $position = Convert-ToDisplayString $errorRecord.InvocationInfo.PositionMessage
              if (-not [string]::IsNullOrWhiteSpace($position)) {
                $lines.Add('Position:') | Out-Null
                $lines.Add($position) | Out-Null
              }
            }

            $scriptStackTrace = Convert-ToDisplayString $errorRecord.ScriptStackTrace
            if (-not [string]::IsNullOrWhiteSpace($scriptStackTrace)) {
              $lines.Add('ScriptStackTrace:') | Out-Null
              $lines.Add($scriptStackTrace) | Out-Null
            }
          }

          return ($lines -join [Environment]::NewLine)
        }
        function Invoke-IccPortAuthStep([string]$stepName, [scriptblock]$scriptBlock) {
          try {
            & $scriptBlock
          } catch {
            throw (Get-IccPortAuthErrorDetails $stepName $_)
          }
        }
        function New-IccPortAuthCheck([string]$name, [string]$statusText, [string]$statusLevel, [string]$detail) {
          [ordered]@{
            Name = $name
            StatusText = $statusText
            StatusLevel = $statusLevel
            Detail = $detail
          }
        }
        function Get-IccPortAuthStatusLevel([string]$statusText) {
          switch -Regex ($statusText) {
            '^(Healthy|Applicable|Authenticated)$' { 'Green'; break }
            '^(Degraded|Warning|Skipped)$' { 'Yellow'; break }
            '^(Unhealthy|Not applicable|Not authenticated)$' { 'Red'; break }
            default { 'Unknown' }
          }
        }
        function Get-IccPortAuthRecommendedAction([string]$logName, [string]$message) {
          $normalizedMessage = Convert-ToDisplayString $message
          if ($logName -like '*Wired-AutoConfig*' -and $normalizedMessage -match '(?i)certificate|credentials') {
            return 'Verify the wired 802.1X profile and the machine certificate used for EAP-TLS.'
          }
          if ($logName -like '*Wired-AutoConfig*' -and $normalizedMessage -match '(?i)profile|policy') {
            return 'Verify that a wired 802.1X LAN profile is present and assigned to the active interface.'
          }
          if ($logName -like '*EapHost*') {
            return 'Review the EAP method and credentials configured in the wired profile.'
          }
          if ($logName -like '*CAPI2*') {
            return 'Review certificate trust, chain build, EKU and SAN/CN matching for the machine certificate.'
          }
          return 'Review the event details and rerun the port authentication health check after remediation.'
        }
        function Test-IccPortAuthVpnLikeInterface([string]$name, [string]$description) {
          $combined = ((Convert-ToDisplayString $name) + ' ' + (Convert-ToDisplayString $description)).Trim()
          if ([string]::IsNullOrWhiteSpace($combined)) { return $false }
          if ($combined -match '(?i)check\s*point|virtual\s+network\s+adapter|\bvpn\b') { return $true }
          if (-not [string]::IsNullOrWhiteSpace($vpnAdapterMatch) -and $combined -like ('*' + $vpnAdapterMatch + '*')) { return $true }
          return $false
        }
        function Get-IccPortAuthLanInterfaces() {
          $entries = New-Object System.Collections.Generic.List[object]
          try {
            $output = (netsh lan show interfaces 2>&1 | Out-String)
            $current = @{}
            foreach ($line in @($output -split "`r?`n")) {
              if ([string]::IsNullOrWhiteSpace($line)) {
                if ($current.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace((Convert-ToDisplayString $current['Name']))) {
                  $entries.Add(([pscustomobject]@{
                    Name = Convert-ToDisplayString $current['Name']
                    Description = Convert-ToDisplayString $current['Description']
                    Status = Convert-ToDisplayString $current['Status']
                    AuthenticationState = Convert-ToDisplayString $current['AuthenticationState']
                  })) | Out-Null
                }
                $current = @{}
                continue
              }

              $match = [regex]::Match($line, '^\s*(?<key>[^:]+?)\s*:\s*(?<value>.*)$')
              if (-not $match.Success) { continue }

              $key = $match.Groups['key'].Value.Trim()
              $value = $match.Groups['value'].Value.Trim()
              switch -Regex ($key) {
                '^Name$' { $current['Name'] = $value; break }
                '^(Description|Beschreibung)$' { $current['Description'] = $value; break }
                '^(Status)$' { $current['Status'] = $value; break }
                '^(Authentication|Authentication state|Authentifizierung.*)$' { $current['AuthenticationState'] = $value; break }
              }
            }

            if ($current.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace((Convert-ToDisplayString $current['Name']))) {
              $entries.Add(([pscustomobject]@{
                Name = Convert-ToDisplayString $current['Name']
                Description = Convert-ToDisplayString $current['Description']
                Status = Convert-ToDisplayString $current['Status']
                AuthenticationState = Convert-ToDisplayString $current['AuthenticationState']
              })) | Out-Null
            }
          } catch {
          }

          return [object[]]$entries
        }
        function Get-IccPortAuthFqdn() {
          try {
            $cs = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
            $dnsName = Convert-ToDisplayString $cs.DNSHostName
            $domain = Convert-ToDisplayString $cs.Domain
            if (-not [string]::IsNullOrWhiteSpace($dnsName) -and -not [string]::IsNullOrWhiteSpace($domain) -and $domain -ne 'WORKGROUP') {
              return ($dnsName + '.' + $domain).ToLowerInvariant()
            }
            if (-not [string]::IsNullOrWhiteSpace($dnsName)) {
              return $dnsName.ToLowerInvariant()
            }
          } catch {
          }
          try {
            $name = [System.Net.Dns]::GetHostEntry('').HostName
            if (-not [string]::IsNullOrWhiteSpace($name)) { return $name.ToLowerInvariant() }
          } catch {
          }
          return $env:COMPUTERNAME.ToLowerInvariant()
        }
        function Get-IccPortAuthActiveWiredInterface() {
          $lanInterfaces = @(Get-IccPortAuthLanInterfaces | Where-Object {
            -not (Test-IccPortAuthVpnLikeInterface $_.Name $_.Description)
          })

          try {
            $wired = @(Get-NetAdapter -Physical -ErrorAction Stop | Where-Object {
              $_.Status -eq 'Up' -and
              (Convert-ToDisplayString (Get-FirstPropertyValue $_ @('InterfaceDescription', 'Name', 'InterfaceAlias'))) -match '(?i)ethernet|lan|gigabit|gbe' -and
              -not (Test-IccPortAuthVpnLikeInterface (Convert-ToDisplayString (Get-FirstPropertyValue $_ @('Name', 'InterfaceAlias'))) (Convert-ToDisplayString (Get-FirstPropertyValue $_ @('InterfaceDescription', 'Name', 'InterfaceAlias'))))
            } | Sort-Object ifIndex)
            foreach ($netshInterface in $lanInterfaces) {
              $item = $wired | Where-Object {
                [string]::Equals((Convert-ToDisplayString (Get-FirstPropertyValue $_ @('Name', 'InterfaceAlias'))), $netshInterface.Name, [System.StringComparison]::OrdinalIgnoreCase) -or
                [string]::Equals((Convert-ToDisplayString (Get-FirstPropertyValue $_ @('InterfaceDescription', 'Name', 'InterfaceAlias'))), $netshInterface.Description, [System.StringComparison]::OrdinalIgnoreCase)
              } | Select-Object -First 1

              if ($null -ne $item) {
                return [ordered]@{
                  Name = Convert-ToDisplayString (Get-FirstPropertyValue $item @('Name', 'InterfaceAlias'))
                  Description = Convert-ToDisplayString (Get-FirstPropertyValue $item @('InterfaceDescription', 'Name', 'InterfaceAlias'))
                  Status = Convert-ToDisplayString (Get-FirstPropertyValue $item @('Status'))
                }
              }
            }

            if ($wired.Count -gt 0) {
              $item = $wired[0]
              return [ordered]@{
                Name = Convert-ToDisplayString (Get-FirstPropertyValue $item @('Name', 'InterfaceAlias'))
                Description = Convert-ToDisplayString (Get-FirstPropertyValue $item @('InterfaceDescription', 'Name', 'InterfaceAlias'))
                Status = Convert-ToDisplayString (Get-FirstPropertyValue $item @('Status'))
              }
            }
          } catch {
          }

          if ($lanInterfaces.Count -gt 0) {
            $item = $lanInterfaces[0]
            return [ordered]@{
              Name = Convert-ToDisplayString $item.Name
              Description = Convert-ToDisplayString $item.Description
              Status = Convert-ToDisplayString $item.Status
            }
          }

          return [ordered]@{
            Name = ''
            Description = ''
            Status = ''
          }
        }
        function Get-IccPortAuthServiceStatus([string]$name) {
          try {
            $service = Get-Service -Name $name -ErrorAction Stop
            return [ordered]@{
              Name = $name
              Exists = $true
              Status = Convert-ToDisplayString $service.Status
            }
          } catch {
            return [ordered]@{
              Name = $name
              Exists = $false
              Status = 'Missing'
            }
          }
        }
        function Get-IccPortAuthTracingMode() {
          try {
            $output = (netsh lan show tracing 2>&1 | Out-String)
            if ($output -match '(?i)persistent') { return 'Persistent' }
            if ($output -match '(?i)running|aktiviert|enabled') { return 'Enabled' }
          } catch {
          }
          return 'Disabled'
        }
        function Get-IccPortAuthSanDns($cert) {
          try {
            foreach ($extension in @($cert.Extensions)) {
              if ($extension.Oid.Value -ne '2.5.29.17') { continue }
              $formatted = $extension.Format($true)
              $matches = [regex]::Matches($formatted, '(?im)DNS Name=(?<dns>[^\r\n,]+)')
              if ($matches.Count -gt 0) {
                return (($matches | ForEach-Object { $_.Groups['dns'].Value.Trim() }) -join ', ')
              }
            }
          } catch {
          }
          return ''
        }
        function Get-IccPortAuthCertificates([string]$fqdn) {
          $now = Get-Date
          $entries = New-Object System.Collections.Generic.List[object]
          $hasHealthy = $false
          try {
            foreach ($cert in @(Get-ChildItem -Path 'Cert:\LocalMachine\My' -ErrorAction SilentlyContinue)) {
              $ekuMatches = @($cert.EnhancedKeyUsageList | Where-Object {
                $_.ObjectId -eq '1.3.6.1.5.5.7.3.2' -or $_.Value -eq '1.3.6.1.5.5.7.3.2'
              })
              if ($ekuMatches.Count -eq 0) { continue }

              $sanDns = Get-IccPortAuthSanDns $cert
              $subject = Convert-ToDisplayString $cert.Subject
              $fqdnMatch = $false
              if (-not [string]::IsNullOrWhiteSpace($fqdn)) {
                $fqdnMatch = ($sanDns -split '\s*,\s*' | Where-Object { [string]::Equals($_, $fqdn, [System.StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
                if (-not $fqdnMatch -and $subject -match ('(?i)CN=' + [regex]::Escape($fqdn) + '(?:,|$)')) { $fqdnMatch = $true }
              }

              $isTimeValid = $cert.NotBefore -le $now -and $cert.NotAfter -gt $now
              $hasPrivateKey = [bool]$cert.HasPrivateKey
              $chainStatus = 'Unknown'
              $isChainValid = $false
              try {
                $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
                $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
                $isChainValid = $chain.Build($cert)
                $chainStatus = if ($isChainValid) { 'Chain valid' } elseif ($chain.ChainStatus.Count -gt 0) { ($chain.ChainStatus | ForEach-Object { $_.Status }) -join ', ' } else { 'Chain invalid' }
              } catch {
                $chainStatus = 'Chain check failed'
              }

              $statusLevel = if ($isTimeValid -and $hasPrivateKey -and $isChainValid -and $fqdnMatch) { 'Green' } elseif ($isTimeValid -and $hasPrivateKey) { 'Yellow' } else { 'Red' }
              if ($statusLevel -eq 'Green') { $hasHealthy = $true }

              $entries.Add(([ordered]@{
                Subject = $subject
                SanDns = $sanDns
                Thumbprint = Convert-ToDisplayString $cert.Thumbprint
                Issuer = Convert-ToDisplayString $cert.Issuer
                StoreName = 'LocalMachine\My'
                HasPrivateKeyText = if ($hasPrivateKey) { 'Yes' } else { 'No' }
                ValidityText = if ($isTimeValid) { 'Valid until ' + $cert.NotAfter.ToString('yyyy-MM-dd') } else { 'Invalid or expired' }
                ChainStatusText = $chainStatus
                FqdnMatchText = if ($fqdnMatch) { 'Yes' } else { 'No' }
                StatusLevel = $statusLevel
              } -as [object])) | Out-Null
            }
          } catch {
          }
          return [ordered]@{
            Entries = [object[]]$entries
            HasHealthyCertificate = $hasHealthy
          }
        }
        function Get-IccPortAuthProfiles([string]$interfaceName) {
          $entries = New-Object System.Collections.Generic.List[object]
          $profileNames = New-Object System.Collections.Generic.List[string]
          $parsedProfilesByName = @{}
          $hasHealthy = $false
          try {
            $profilesOutput = (netsh lan show profiles 2>&1 | Out-String)
            foreach ($line in @($profilesOutput -split "`r?`n")) {
              if ($line -match ':\s*(?<name>.+)$' -and $line -notmatch '(?i)profiles\s+on\s+interface') {
                $name = $Matches['name'].Trim()
                if (-not [string]::IsNullOrWhiteSpace($name) -and -not $profileNames.Contains($name)) { $profileNames.Add($name) | Out-Null }
              }
            }
          } catch {
          }

          $tempFolder = Join-Path ([System.IO.Path]::GetTempPath()) ('ICC-dot3svc-' + [Guid]::NewGuid().ToString('N'))
          try {
            New-Item -ItemType Directory -Path $tempFolder -Force | Out-Null
            netsh lan export profile folder="$tempFolder" | Out-Null
            $xmlFiles = @(Get-ChildItem -Path $tempFolder -Filter '*.xml' -ErrorAction SilentlyContinue)
            if ($xmlFiles.Count -eq 0 -and $profileNames.Count -eq 0) {
              return [pscustomobject]@{
                Entries = @()
                HasHealthyProfile = $false
                HasAnyProfile = $false
              }
            }

            foreach ($candidate in $xmlFiles) {
              $profileNameFromXml = ''
              $authMode = ''
              $ssoMode = ''
              $oneXEnabled = ''
              $oneXEnforced = ''
              $eapType = ''
              $parseStatusText = 'Profile exported without readable XML'
              $statusLevel = 'Yellow'

              try {
                [xml]$xml = Get-Content -LiteralPath $candidate.FullName -Raw -ErrorAction Stop
                $profileNameFromXml = Get-IccXmlNodeInnerText $xml '/*[local-name()="LANProfile"]/*[local-name()="name"]'
                if ([string]::IsNullOrWhiteSpace($profileNameFromXml)) {
                  $profileNameFromXml = Get-IccXmlNodeInnerText $xml '//*[local-name()="name"]'
                }
                if ([string]::IsNullOrWhiteSpace($profileNameFromXml)) {
                  $profileNameFromXml = [System.IO.Path]::GetFileNameWithoutExtension($candidate.Name)
                }

                $authMode = Get-IccXmlNodeInnerText $xml '//*[local-name()="authMode"]'
                $ssoMode = Get-IccXmlNodeInnerText $xml '//*[local-name()="ssoMode"]'
                $oneXEnabled = Get-IccXmlNodeInnerText $xml '//*[local-name()="OneXEnabled"]'
                $oneXEnforced = Get-IccXmlNodeInnerText $xml '//*[local-name()="OneXEnforced"]'
                $eapTypeRaw = Get-IccXmlNodeInnerText $xml '//*[local-name()="Type"]'
                $eapType = switch ($eapTypeRaw) {
                  '13' { 'EAP-TLS' }
                  '25' { 'PEAP' }
                  default { if ([string]::IsNullOrWhiteSpace($eapTypeRaw)) { 'Unknown' } else { $eapTypeRaw } }
                }
                $parseStatusText = 'Valid XML'
                $statusLevel = if (($oneXEnabled -match '^(?i:true|yes|1)$' -or [string]::IsNullOrWhiteSpace($oneXEnabled)) -and ($oneXEnforced -match '^(?i:true|yes|1)$' -or [string]::IsNullOrWhiteSpace($oneXEnforced))) { 'Green' } else { 'Yellow' }
              } catch {
                $profileNameFromXml = [System.IO.Path]::GetFileNameWithoutExtension($candidate.Name)
                $parseStatusText = 'Invalid XML'
                $statusLevel = 'Red'
              }

              $parsedProfile = [pscustomobject]@{
                Name = $profileNameFromXml
                InterfaceName = $interfaceName
                AuthMode = $authMode
                SsoMode = $ssoMode
                OneXEnabledText = if ([string]::IsNullOrWhiteSpace($oneXEnabled)) { 'Unknown' } else { $oneXEnabled }
                OneXEnforcedText = if ([string]::IsNullOrWhiteSpace($oneXEnforced)) { 'Unknown' } else { $oneXEnforced }
                EapType = $eapType
                ParseStatusText = $parseStatusText
                StatusLevel = $statusLevel
              }

              if (-not [string]::IsNullOrWhiteSpace($profileNameFromXml) -and -not $parsedProfilesByName.ContainsKey($profileNameFromXml)) {
                $parsedProfilesByName[$profileNameFromXml] = $parsedProfile
              }
            }

            foreach ($name in $profileNames) {
              $parsedProfile = $null
              foreach ($key in @($parsedProfilesByName.Keys)) {
                if ([string]::Equals($key, $name, [System.StringComparison]::OrdinalIgnoreCase)) {
                  $parsedProfile = $parsedProfilesByName[$key]
                  $parsedProfilesByName.Remove($key)
                  break
                }
              }

              if ($null -eq $parsedProfile) {
                $parsedProfile = [pscustomobject]@{
                  Name = $name
                  InterfaceName = $interfaceName
                  AuthMode = ''
                  SsoMode = ''
                  OneXEnabledText = 'Unknown'
                  OneXEnforcedText = 'Unknown'
                  EapType = ''
                  ParseStatusText = 'Profile detected, but export could not be matched to XML'
                  StatusLevel = 'Yellow'
                }
              }

              if ($parsedProfile.StatusLevel -eq 'Green') { $hasHealthy = $true }
              $entries.Add($parsedProfile) | Out-Null
            }

            foreach ($key in @($parsedProfilesByName.Keys)) {
              $parsedProfile = $parsedProfilesByName[$key]
              if ($parsedProfile.StatusLevel -eq 'Green') { $hasHealthy = $true }
              $entries.Add($parsedProfile) | Out-Null
            }
          } catch {
          } finally {
            try { Remove-Item -Path $tempFolder -Recurse -Force -ErrorAction SilentlyContinue } catch {}
          }

          return [pscustomobject]@{
            Entries = [object[]]$entries
            HasHealthyProfile = [bool]$hasHealthy
            HasAnyProfile = ($entries.Count -gt 0)
          }
        }
        function Get-IccPortAuthEvents() {
          $entries = New-Object System.Collections.Generic.List[object]
          $sinceUtc = (Get-Date).AddDays(-7)
          $lastSuccess = ''
          $hasBlockingErrors = $false
          $logs = @(
            'Microsoft-Windows-Wired-AutoConfig/Operational',
            'Microsoft-Windows-EapHost/Operational',
            'Microsoft-Windows-CAPI2/Operational'
          )
          foreach ($logName in $logs) {
            if (-not (Test-IccEventLogAvailable $logName)) { continue }
            try {
              $events = @(Get-WinEvent -FilterHashtable @{ LogName = $logName; StartTime = $sinceUtc } -MaxEvents 100 -ErrorAction Stop)
              foreach ($event in $events) {
                $message = Convert-ToDisplayString $event.Message
                $summary = if ([string]::IsNullOrWhiteSpace($message)) { 'Event ' + $event.Id } else { ($message -split "`r?`n" | Select-Object -First 1).Trim() }
                $statusLevel = switch -Regex ((Convert-ToDisplayString $event.LevelDisplayName)) {
                  'Critical|Error' { 'Red'; break }
                  'Warning' { 'Yellow'; break }
                  'Information' { 'Green'; break }
                  default { 'Unknown' }
                }
                $eventTimeUtc = $null
                if ($null -ne $event.TimeCreated) {
                  try {
                    $eventTimeUtc = ([DateTime]$event.TimeCreated).ToUniversalTime()
                  } catch {
                  }
                }
                if ($statusLevel -eq 'Red') { $hasBlockingErrors = $true }
                if ($logName -like '*Wired-AutoConfig*' -and $statusLevel -eq 'Green' -and $summary -match '(?i)success|authenticated') {
                  if ([string]::IsNullOrWhiteSpace($lastSuccess)) {
                    $lastSuccess = if ($null -ne $eventTimeUtc) { $eventTimeUtc.ToString('yyyy-MM-dd HH:mm:ssZ') + ' | ' + $summary } else { $summary }
                  }
                }
                $entries.Add(([ordered]@{
                  TimeCreatedUtc = if ($null -ne $eventTimeUtc) { $eventTimeUtc.ToString('o') } else { $null }
                  LogName = $logName
                  Id = [int]$event.Id
                  Level = Convert-ToDisplayString $event.LevelDisplayName
                  StatusLevel = $statusLevel
                  Summary = $summary
                  RecommendedAction = Get-IccPortAuthRecommendedAction $logName $message
                  Message = $message
                } -as [object])) | Out-Null
              }
            } catch {
            }
          }
          return [ordered]@{
            Entries = [object[]]$entries
            HasBlockingErrors = $hasBlockingErrors
            LastSuccess = if ([string]::IsNullOrWhiteSpace($lastSuccess)) { 'No successful wired authentication event found in the last 7 days.' } else { $lastSuccess }
          }
        }
        function Get-IccPortAuthenticationSnapshot([string]$VpnStatusText) {
          $fqdn = Invoke-IccPortAuthStep 'Get-IccPortAuthFqdn' { Get-IccPortAuthFqdn }
          $activeInterface = Invoke-IccPortAuthStep 'Get-IccPortAuthActiveWiredInterface' { Get-IccPortAuthActiveWiredInterface }
          $dot3svc = Invoke-IccPortAuthStep 'Get-IccPortAuthServiceStatus(dot3svc)' { Get-IccPortAuthServiceStatus 'dot3svc' }
          $eapHost = Invoke-IccPortAuthStep 'Get-IccPortAuthServiceStatus(EapHost)' { Get-IccPortAuthServiceStatus 'EapHost' }
          $tracingMode = Invoke-IccPortAuthStep 'Get-IccPortAuthTracingMode' { Get-IccPortAuthTracingMode }
          $authState = 'Unknown'

          $authState = Invoke-IccPortAuthStep 'ReadAuthenticationState' {
            $interfaces = @(Get-IccPortAuthLanInterfaces)
            if (-not [string]::IsNullOrWhiteSpace($activeInterface.Name)) {
              foreach ($item in $interfaces) {
                if ([string]::Equals((Convert-ToDisplayString $item.Name), $activeInterface.Name, [System.StringComparison]::OrdinalIgnoreCase)) {
                  $state = Convert-ToDisplayString $item.AuthenticationState
                  if (-not [string]::IsNullOrWhiteSpace($state)) { return $state }
                }
              }
            }

            foreach ($item in $interfaces) {
              $state = Convert-ToDisplayString $item.AuthenticationState
              if (-not [string]::IsNullOrWhiteSpace($state)) { return $state }
            }

            return 'Unknown'
          }

          $profiles = Invoke-IccPortAuthStep 'Get-IccPortAuthProfiles' { Get-IccPortAuthProfiles $activeInterface.Name }
          $certificates = Invoke-IccPortAuthStep 'Get-IccPortAuthCertificates' { Get-IccPortAuthCertificates $fqdn }
          $events = Invoke-IccPortAuthStep 'Get-IccPortAuthEvents' { Get-IccPortAuthEvents }

          $checks = New-Object System.Collections.Generic.List[object]
          $applicabilityText = if ($VpnStatusText -eq 'Connected') { 'Skipped' } elseif ([string]::IsNullOrWhiteSpace($activeInterface.Name)) { 'Not applicable' } else { 'Applicable' }
          $checks.Add((New-IccPortAuthCheck 'Applicability' $applicabilityText (Get-IccPortAuthStatusLevel $applicabilityText) $(if ($applicabilityText -eq 'Applicable') { 'A wired LAN interface is present for 802.1X validation.' } elseif ($applicabilityText -eq 'Skipped') { 'VPN is connected; wired health is skipped.' } else { 'No wired LAN interface was detected.' }))) | Out-Null

          $servicesHealthy = $dot3svc.Exists -and $dot3svc.Status -eq 'Running' -and $eapHost.Exists -and $eapHost.Status -eq 'Running'
          $checks.Add((New-IccPortAuthCheck 'Services' $(if ($servicesHealthy) { 'Healthy' } else { 'Unhealthy' }) $(if ($servicesHealthy) { 'Green' } else { 'Red' }) ('dot3svc=' + $dot3svc.Status + ', EapHost=' + $eapHost.Status + '.'))) | Out-Null

          $profileStatus = if (-not $profiles.HasAnyProfile) { 'Unhealthy' } elseif ($profiles.HasHealthyProfile) { 'Healthy' } else { 'Degraded' }
          $checks.Add((New-IccPortAuthCheck 'Profile' $profileStatus (Get-IccPortAuthStatusLevel $profileStatus) $(if (-not $profiles.HasAnyProfile) { 'No wired 802.1X profiles were found.' } elseif ($profiles.HasHealthyProfile) { 'A wired 802.1X profile with valid XML is present.' } else { 'A wired profile exists but XML or OneX parameters have issues.' }))) | Out-Null

          $certStatus = if ($certificates.HasHealthyCertificate) { 'Healthy' } elseif ($certificates.Entries.Count -gt 0) { 'Degraded' } else { 'Unhealthy' }
          $checks.Add((New-IccPortAuthCheck 'Certificate' $certStatus (Get-IccPortAuthStatusLevel $certStatus) $(if ($certificates.HasHealthyCertificate) { 'A valid LocalMachine client-authentication certificate matches the client FQDN.' } elseif ($certificates.Entries.Count -gt 0) { 'Client-authentication certificates were found, but none are fully healthy for this host.' } else { 'No suitable LocalMachine client-authentication certificate was found.' }))) | Out-Null

          $authHealthy = $authState -match '(?i)\bauthenticated\b' -and $authState -notmatch '(?i)not\s+authenticated'
          $checks.Add((New-IccPortAuthCheck 'Authentication state' $(if ($authHealthy) { 'Authenticated' } else { 'Not authenticated' }) $(if ($authHealthy) { 'Green' } else { 'Red' }) ('netsh lan reported: ' + $authState + '.'))) | Out-Null

          $eventStatus = if ($events.HasBlockingErrors) { 'Unhealthy' } else { 'Healthy' }
          $checks.Add((New-IccPortAuthCheck 'Events' $eventStatus (Get-IccPortAuthStatusLevel $eventStatus) $(if ($events.HasBlockingErrors) { 'Recent wired 802.1X related errors were found in Operational logs.' } else { 'No recent blocking Wired AutoConfig, EapHost or CAPI2 errors were found.' }))) | Out-Null

          $overallStatusText = 'Unknown'
          $overallStatusLevel = 'Unknown'
          $overallDetailText = 'Port authentication status is not available.'

          if ($applicabilityText -eq 'Skipped') {
            $overallStatusText = 'Skipped'
            $overallStatusLevel = 'Yellow'
            $overallDetailText = 'Port authentication health check was skipped because VPN is connected.'
          } elseif ($applicabilityText -eq 'Not applicable') {
            $overallStatusText = 'Not applicable'
            $overallStatusLevel = 'Unknown'
            $overallDetailText = 'No wired LAN interface was detected for 802.1X validation.'
          } elseif ($servicesHealthy -and $profiles.HasHealthyProfile -and $certificates.HasHealthyCertificate -and $authHealthy -and -not $events.HasBlockingErrors) {
            $overallStatusText = if ($tracingMode -eq 'Persistent') { 'Degraded' } else { 'Healthy' }
            $overallStatusLevel = if ($tracingMode -eq 'Persistent') { 'Yellow' } else { 'Green' }
            $overallDetailText = if ($tracingMode -eq 'Persistent') { '802.1X authentication is healthy, but tracing is still set to persistent.' } else { 'The active wired interface is authenticated, a valid profile is present, and a healthy client-authentication certificate matches the client FQDN.' }
          } elseif ($servicesHealthy -and $profiles.HasAnyProfile -and $certificates.Entries.Count -gt 0) {
            $overallStatusText = 'Degraded'
            $overallStatusLevel = 'Yellow'
            $overallDetailText = 'Port authentication is partially configured, but one or more checks are still failing or incomplete.'
          } else {
            $overallStatusText = 'Unhealthy'
            $overallStatusLevel = 'Red'
            $overallDetailText = 'Port authentication is not healthy because one or more required wired 802.1X prerequisites are missing or failing.'
          }

          [ordered]@{
            CapturedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
            OverallStatusText = $overallStatusText
            OverallStatusLevel = $overallStatusLevel
            OverallDetailText = $overallDetailText
            ApplicabilityText = $applicabilityText
            Fqdn = $fqdn
            ActiveInterfaceName = $activeInterface.Name
            ActiveInterfaceDescription = $activeInterface.Description
            AuthenticationStateText = $authState
            TracingModeText = $tracingMode
            LastSuccessfulAuthenticationText = $events.LastSuccess
            Checks = [object[]]$checks
            Profiles = [object[]]$profiles.Entries
            Certificates = [object[]]$certificates.Entries
            Events = [object[]]$events.Entries
          }
        }
        """;

    private sealed class PortAuthenticationOnlyPayload
    {
        public PortAuthenticationPayload? PortAuthentication { get; init; }
    }

    private sealed class PortAuthenticationPayload
    {
        public string? CapturedAtUtc { get; init; }
        public string? OverallStatusText { get; init; }
        public string? OverallStatusLevel { get; init; }
        public string? OverallDetailText { get; init; }
        public string? ApplicabilityText { get; init; }
        public string? Fqdn { get; init; }
        public string? ActiveInterfaceName { get; init; }
        public string? ActiveInterfaceDescription { get; init; }
        public string? AuthenticationStateText { get; init; }
        public string? TracingModeText { get; init; }
        public string? LastSuccessfulAuthenticationText { get; init; }
        public List<PortAuthenticationCheckPayload>? Checks { get; init; }
        public List<PortAuthenticationProfilePayload>? Profiles { get; init; }
        public List<PortAuthenticationCertificatePayload>? Certificates { get; init; }
        public List<PortAuthenticationEventPayload>? Events { get; init; }
    }

    private sealed class PortAuthenticationCheckPayload
    {
        public string? Name { get; init; }
        public string? StatusText { get; init; }
        public string? StatusLevel { get; init; }
        public string? Detail { get; init; }
    }

    private sealed class PortAuthenticationProfilePayload
    {
        public string? Name { get; init; }
        public string? InterfaceName { get; init; }
        public string? AuthMode { get; init; }
        public string? SsoMode { get; init; }
        public string? OneXEnabledText { get; init; }
        public string? OneXEnforcedText { get; init; }
        public string? EapType { get; init; }
        public string? ParseStatusText { get; init; }
        public string? StatusLevel { get; init; }
    }

    private sealed class PortAuthenticationCertificatePayload
    {
        public string? Subject { get; init; }
        public string? SanDns { get; init; }
        public string? Thumbprint { get; init; }
        public string? Issuer { get; init; }
        public string? StoreName { get; init; }
        public string? HasPrivateKeyText { get; init; }
        public string? ValidityText { get; init; }
        public string? ChainStatusText { get; init; }
        public string? FqdnMatchText { get; init; }
        public string? StatusLevel { get; init; }
    }

    private sealed class PortAuthenticationEventPayload
    {
        public string? TimeCreatedUtc { get; init; }
        public string? LogName { get; init; }
        public int Id { get; init; }
        public string? Level { get; init; }
        public string? StatusLevel { get; init; }
        public string? Summary { get; init; }
        public string? RecommendedAction { get; init; }
        public string? Message { get; init; }
    }
}
