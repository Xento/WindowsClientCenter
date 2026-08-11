using System.Collections.ObjectModel;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;

namespace WindowsClientCenter.Plugins.PowerShellScripts;

public sealed class PowerShellScriptMetadataProvider : IPowerShellScriptMetadataProvider
{
    private const int CacheFormatVersion = 2;
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly HashSet<string> CommonParameterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Verbose",
        "Debug",
        "ErrorAction",
        "WarningAction",
        "InformationAction",
        "ErrorVariable",
        "WarningVariable",
        "InformationVariable",
        "OutVariable",
        "OutBuffer",
        "PipelineVariable",
        "ProgressAction",
        "WhatIf",
        "Confirm"
    };
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsClientCenter",
        "cache");
    private static readonly string CacheFilePath = Path.Combine(CacheDirectory, "powershell-script-metadata-cache.json");

    public async ValueTask<IReadOnlyList<PowerShellScriptCatalogEntry>> LoadAsync(string scriptDirectory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(scriptDirectory))
        {
            return [];
        }

        var scriptPaths = Directory.EnumerateFiles(scriptDirectory, "*.ps1", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (scriptPaths.Length == 0)
        {
            return [];
        }

        if (!OperatingSystem.IsWindows())
        {
            return scriptPaths
                .Select(scriptPath =>
                {
                    var relativePath = Path.GetRelativePath(scriptDirectory, scriptPath).Replace('\\', '/');
                    return new PowerShellScriptCatalogEntry(
                        relativePath,
                        Path.GetFileNameWithoutExtension(scriptPath),
                        relativePath,
                        scriptPath,
                        PowerShellScriptExecutionMode.Unsupported,
                        [],
                        "PowerShell script inspection is only supported on Windows hosts.");
                })
                .ToArray();
        }

        var cacheEntriesByPath = await LoadCacheAsync(cancellationToken);
        var currentScriptPaths = new HashSet<string>(scriptPaths, StringComparer.OrdinalIgnoreCase);
        var entries = new List<PowerShellScriptCatalogEntry>(scriptPaths.Length);
        var scriptPathsToInspect = new List<string>();

        foreach (var scriptPath in scriptPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryGetCachedEntry(cacheEntriesByPath, scriptPath, out var cachedEntry))
            {
                entries.Add(ToCatalogEntry(cachedEntry!));
                continue;
            }

            scriptPathsToInspect.Add(scriptPath);
        }

        if (scriptPathsToInspect.Count > 0)
        {
            var inspectionResults = await RunInspectionAsync(scriptPathsToInspect, cancellationToken);
            var inspectionResultsByPath = inspectionResults.ToDictionary(
                static result => result.FullPath,
                StringComparer.OrdinalIgnoreCase);

            foreach (var scriptPath in scriptPathsToInspect)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!inspectionResultsByPath.TryGetValue(scriptPath, out var inspectionResult))
                {
                    var missingEntry = CreateUnsupportedEntry(
                        scriptDirectory,
                        scriptPath,
                        "PowerShell metadata inspection returned no result for the script.");
                    entries.Add(missingEntry);
                    cacheEntriesByPath[scriptPath] = CreateCacheEntry(missingEntry);
                    continue;
                }

                var catalogEntry = CreateCatalogEntry(scriptDirectory, inspectionResult);
                entries.Add(catalogEntry);
                cacheEntriesByPath[scriptPath] = CreateCacheEntry(catalogEntry);
            }
        }

        await SaveCacheAsync(
            cacheEntriesByPath.Values
                .Where(entry => currentScriptPaths.Contains(entry.FullPath))
                .OrderBy(static entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            cancellationToken);

        return entries
            .OrderBy(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static PowerShellScriptCatalogEntry CreateCatalogEntry(
        string scriptDirectory,
        InspectionScriptResult inspectionResult)
    {
        if (!inspectionResult.Success)
        {
            return CreateUnsupportedEntry(
                scriptDirectory,
                inspectionResult.FullPath,
                inspectionResult.ErrorMessage ?? "PowerShell metadata inspection failed.");
        }

        var relativePath = Path.GetRelativePath(scriptDirectory, inspectionResult.FullPath).Replace('\\', '/');
        var displayName = Path.GetFileNameWithoutExtension(inspectionResult.FullPath);
        try
        {
            return Classify(
                relativePath,
                displayName,
                relativePath,
                inspectionResult.FullPath,
                inspectionResult.ParameterSets);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateUnsupportedEntry(
                scriptDirectory,
                inspectionResult.FullPath,
                $"PowerShell metadata inspection failed: {ex.Message}");
        }
    }

    private static PowerShellScriptCatalogEntry CreateUnsupportedEntry(
        string scriptDirectory,
        string scriptPath,
        string errorMessage)
    {
        var relativePath = Path.GetRelativePath(scriptDirectory, scriptPath).Replace('\\', '/');
        return new PowerShellScriptCatalogEntry(
            relativePath,
            Path.GetFileNameWithoutExtension(scriptPath),
            relativePath,
            scriptPath,
            PowerShellScriptExecutionMode.Unsupported,
            [],
            errorMessage);
    }

    private static PowerShellScriptCatalogEntry Classify(
        string itemId,
        string displayName,
        string relativePath,
        string scriptPath,
        IReadOnlyList<InspectionParameterSet> parameterSets)
    {
        var candidateSets = parameterSets
            .Where(static set => set.Parameters.Any(parameter => parameter.Name.Equals("ComputerName", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (candidateSets.Length == 0)
        {
            return new PowerShellScriptCatalogEntry(
                itemId,
                displayName,
                relativePath,
                scriptPath,
                PowerShellScriptExecutionMode.RemotingWindow,
                []);
        }

        var signatures = candidateSets
            .Select(BuildSignature)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (signatures.Length != 1)
        {
            return new PowerShellScriptCatalogEntry(
                itemId,
                displayName,
                relativePath,
                scriptPath,
                PowerShellScriptExecutionMode.Unsupported,
                [],
                "The script exposes multiple ComputerName parameter sets with different required parameters.");
        }

        var requiredParameters = candidateSets[0].Parameters
            .Where(static parameter =>
                parameter.IsMandatory &&
                !parameter.Name.Equals("ComputerName", StringComparison.OrdinalIgnoreCase) &&
                !CommonParameterNames.Contains(parameter.Name))
            .OrderBy(static parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var parameterDefinitions = new List<PowerShellScriptParameterDefinition>(requiredParameters.Length);
        foreach (var requiredParameter in requiredParameters)
        {
            if (!TryCreateParameterDefinition(requiredParameter, out var definition, out var errorMessage))
            {
                return new PowerShellScriptCatalogEntry(
                    itemId,
                    displayName,
                    relativePath,
                    scriptPath,
                    PowerShellScriptExecutionMode.Unsupported,
                    [],
                    errorMessage);
            }

            parameterDefinitions.Add(definition);
        }

        return new PowerShellScriptCatalogEntry(
            itemId,
            displayName,
            relativePath,
            scriptPath,
            parameterDefinitions.Count == 0
                ? PowerShellScriptExecutionMode.DirectComputerName
                : PowerShellScriptExecutionMode.PromptForComputerNameParameters,
            parameterDefinitions);
    }

    private static string BuildSignature(InspectionParameterSet parameterSet)
    {
        return string.Join(
            "|",
            parameterSet.Parameters
                .Where(static parameter =>
                    parameter.IsMandatory &&
                    !parameter.Name.Equals("ComputerName", StringComparison.OrdinalIgnoreCase) &&
                    !CommonParameterNames.Contains(parameter.Name))
                .OrderBy(static parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static parameter =>
                    $"{parameter.Name}:{parameter.EffectiveTypeFullName}:{parameter.IsArray}:{parameter.ArrayRank}:{parameter.IsNullable}"));
    }

    private static bool TryCreateParameterDefinition(
        InspectionParameter parameter,
        out PowerShellScriptParameterDefinition definition,
        out string errorMessage)
    {
        definition = null!;
        errorMessage = string.Empty;

        if (parameter.IsArray && parameter.ArrayRank != 1)
        {
            errorMessage = $"Parameter '{parameter.Name}' uses a multidimensional array and is not supported.";
            return false;
        }

        var effectiveType = parameter.EffectiveTypeFullName ?? parameter.TypeFullName ?? string.Empty;
        var displayType = parameter.EffectiveTypeName ?? parameter.TypeName ?? effectiveType;

        if (TryMapParameterKind(effectiveType, out var kind))
        {
            definition = new PowerShellScriptParameterDefinition(
                parameter.Name,
                effectiveType,
                displayType,
                kind,
                parameter.IsArray,
                parameter.IsNullable);
            return true;
        }

        if (parameter.IsEnum)
        {
            definition = new PowerShellScriptParameterDefinition(
                parameter.Name,
                effectiveType,
                displayType,
                PowerShellScriptParameterKind.Enum,
                parameter.IsArray,
                parameter.IsNullable,
                parameter.EnumNames ?? []);
            return true;
        }

        errorMessage = $"Parameter '{parameter.Name}' uses unsupported type '{displayType}'.";
        return false;
    }

    private static bool TryMapParameterKind(string effectiveTypeFullName, out PowerShellScriptParameterKind kind)
    {
        switch (effectiveTypeFullName)
        {
            case "System.String":
                kind = PowerShellScriptParameterKind.String;
                return true;
            case "System.Char":
                kind = PowerShellScriptParameterKind.Char;
                return true;
            case "System.Boolean":
                kind = PowerShellScriptParameterKind.Boolean;
                return true;
            case "System.Management.Automation.SwitchParameter":
                kind = PowerShellScriptParameterKind.Switch;
                return true;
            case "System.Int16":
                kind = PowerShellScriptParameterKind.Int16;
                return true;
            case "System.UInt16":
                kind = PowerShellScriptParameterKind.UInt16;
                return true;
            case "System.Int32":
                kind = PowerShellScriptParameterKind.Int32;
                return true;
            case "System.UInt32":
                kind = PowerShellScriptParameterKind.UInt32;
                return true;
            case "System.Int64":
                kind = PowerShellScriptParameterKind.Int64;
                return true;
            case "System.UInt64":
                kind = PowerShellScriptParameterKind.UInt64;
                return true;
            case "System.Single":
                kind = PowerShellScriptParameterKind.Single;
                return true;
            case "System.Double":
                kind = PowerShellScriptParameterKind.Double;
                return true;
            case "System.Decimal":
                kind = PowerShellScriptParameterKind.Decimal;
                return true;
            case "System.Guid":
                kind = PowerShellScriptParameterKind.Guid;
                return true;
            case "System.DateTime":
                kind = PowerShellScriptParameterKind.DateTime;
                return true;
            case "System.TimeSpan":
                kind = PowerShellScriptParameterKind.TimeSpan;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static async ValueTask<IReadOnlyList<InspectionScriptResult>> RunInspectionAsync(
        IReadOnlyList<string> scriptPaths,
        CancellationToken cancellationToken)
    {
        if (scriptPaths.Count == 0)
        {
            return [];
        }

        var tempFilePath = Path.Combine(Path.GetTempPath(), $"icc-powershell-script-inspection-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempFilePath, JsonSerializer.Serialize(scriptPaths), cancellationToken);

        var command = BuildInspectionCommand(tempFilePath);

        try
        {
            using var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
            runspace.Open();
            using var powerShell = PowerShell.Create();
            powerShell.Runspace = runspace;
            powerShell.AddScript(command, useLocalScope: false);

            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    powerShell.Stop();
                }
                catch
                {
                }
            });

            Collection<PSObject> output;
            try
            {
                output = await Task.Run(powerShell.Invoke, CancellationToken.None);
            }
            catch (RuntimeException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var stdOut = string.Join(Environment.NewLine, output.Select(static item => item?.ToString() ?? string.Empty));
            var stdErr = string.Join(Environment.NewLine, powerShell.Streams.Error.Select(static item => item.ToString()));
            if (powerShell.HadErrors)
            {
                var error = string.IsNullOrWhiteSpace(stdErr) ? "PowerShell metadata inspection failed." : stdErr.Trim();
                throw new InvalidOperationException(error);
            }

            return DeserializeInspectionResults(stdOut);
        }
        finally
        {
            try
            {
                File.Delete(tempFilePath);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private static string BuildInspectionCommand(string inputFilePath)
    {
        var escapedPath = inputFilePath.Replace("'", "''", StringComparison.Ordinal);
        return
            "$inputFile = '" + escapedPath + "';" +
            "$scriptPaths = @((Get-Content -LiteralPath $inputFile -Raw | ConvertFrom-Json) | Sort-Object);" +
            "$results = @($scriptPaths | ForEach-Object {" +
            "  $scriptPath = [string]$_;" +
            "  try {" +
            "    $tokens = $null;" +
            "    $parseErrors = $null;" +
            "    $ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors);" +
            "    if ($ast -and ($null -eq $parseErrors -or $parseErrors.Count -eq 0)) {" +
            "      $paramBlock = $ast.ParamBlock;" +
            "      if ($null -eq $paramBlock -or $paramBlock.Parameters.Count -eq 0) {" +
            "        [pscustomobject]@{ FullPath = $scriptPath; Success = $true; ErrorMessage = $null; ParameterSets = @() }" +
            "      } else {" +
            "        $command = Get-Command -Name $scriptPath -ErrorAction Stop | Select-Object -First 1;" +
            "        $sets = @($command.ParameterSets | ForEach-Object {" +
            "          [pscustomobject]@{" +
            "            Name = $_.Name;" +
            "            Parameters = @($_.Parameters | ForEach-Object {" +
            "              $parameterType = $_.ParameterType;" +
            "              $isArray = $parameterType.IsArray;" +
            "              $elementType = if ($isArray) { $parameterType.GetElementType() } else { $null };" +
            "              $effectiveType = if ($isArray -and $elementType) { $elementType } else { $parameterType };" +
            "              $isNullable = $false;" +
            "              if ($effectiveType -and $effectiveType.IsGenericType -and $effectiveType.GetGenericTypeDefinition().FullName -eq 'System.Nullable`1') {" +
            "                $isNullable = $true;" +
            "                $effectiveType = $effectiveType.GetGenericArguments()[0];" +
            "              };" +
            "              [pscustomobject]@{" +
            "                Name = $_.Name;" +
            "                IsMandatory = $_.IsMandatory;" +
            "                TypeFullName = $parameterType.FullName;" +
            "                TypeName = $parameterType.Name;" +
            "                IsArray = $isArray;" +
            "                ArrayRank = if ($isArray) { $parameterType.GetArrayRank() } else { 0 };" +
            "                EffectiveTypeFullName = if ($effectiveType) { $effectiveType.FullName } else { $null };" +
            "                EffectiveTypeName = if ($effectiveType) { $effectiveType.Name } else { $null };" +
            "                IsNullable = $isNullable;" +
            "                IsEnum = if ($effectiveType) { $effectiveType.IsEnum } else { $false };" +
            "                EnumNames = if ($effectiveType -and $effectiveType.IsEnum) { @([System.Enum]::GetNames($effectiveType)) } else { @() };" +
            "              }" +
            "            })" +
            "          }" +
            "        });" +
            "        [pscustomobject]@{ FullPath = $scriptPath; Success = $true; ErrorMessage = $null; ParameterSets = $sets }" +
            "      }" +
            "    } else {" +
            "      throw 'PowerShell AST parsing failed.';" +
            "    }" +
            "  } catch {" +
            "    [pscustomobject]@{ FullPath = $scriptPath; Success = $false; ErrorMessage = $_.Exception.Message; ParameterSets = @() }" +
            "  }" +
            "});" +
            "$results | ConvertTo-Json -Depth 10 -Compress";
    }

    private static bool TryGetCachedEntry(
        IReadOnlyDictionary<string, CachedScriptMetadata> cacheEntriesByPath,
        string scriptPath,
        out CachedScriptMetadata? cachedEntry)
    {
        cachedEntry = null;
        if (!cacheEntriesByPath.TryGetValue(scriptPath, out var candidate))
        {
            return false;
        }

        var fileInfo = new FileInfo(scriptPath);
        if (!fileInfo.Exists ||
            candidate.CacheFormatVersion != CacheFormatVersion ||
            fileInfo.Length != candidate.FileLength ||
            fileInfo.LastWriteTimeUtc.Ticks != candidate.LastWriteUtcTicks)
        {
            return false;
        }

        cachedEntry = candidate;
        return true;
    }

    private static PowerShellScriptCatalogEntry ToCatalogEntry(CachedScriptMetadata cachedEntry)
    {
        return new PowerShellScriptCatalogEntry(
            cachedEntry.ItemId,
            cachedEntry.DisplayName,
            cachedEntry.RelativePath,
            cachedEntry.FullPath,
            cachedEntry.ExecutionMode,
            cachedEntry.RequiredParameters,
            cachedEntry.ErrorMessage);
    }

    private static CachedScriptMetadata CreateCacheEntry(PowerShellScriptCatalogEntry entry)
    {
        var fileInfo = new FileInfo(entry.FullPath);
        return new CachedScriptMetadata(
            CacheFormatVersion,
            entry.FullPath,
            fileInfo.Exists ? fileInfo.Length : 0,
            fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : 0,
            entry.ItemId,
            entry.DisplayName,
            entry.RelativePath,
            entry.ExecutionMode,
            entry.RequiredParameters,
            entry.ErrorMessage);
    }

    private static async Task<Dictionary<string, CachedScriptMetadata>> LoadCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(CacheFilePath))
        {
            return new Dictionary<string, CachedScriptMetadata>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            await using var stream = File.OpenRead(CacheFilePath);
            var entries = await JsonSerializer.DeserializeAsync<CachedScriptMetadata[]>(stream, CacheJsonOptions, cancellationToken)
                ?? [];
            return entries
                .Where(static entry => !string.IsNullOrWhiteSpace(entry.FullPath))
                .ToDictionary(static entry => entry.FullPath, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, CachedScriptMetadata>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static async Task SaveCacheAsync(
        IReadOnlyList<CachedScriptMetadata> entries,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            await using var stream = File.Create(CacheFilePath);
            await JsonSerializer.SerializeAsync(stream, entries, CacheJsonOptions, cancellationToken);
        }
        catch
        {
            // Cache persistence is optional.
        }
    }

    private static IReadOnlyList<InspectionParameterSet> DeserializeParameterSets(string stdOut)
    {
        if (string.IsNullOrWhiteSpace(stdOut))
        {
            return [];
        }

        var trimmed = stdOut.Trim();
        using var document = JsonDocument.Parse(trimmed);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => ParseParameterSets(document.RootElement),
            JsonValueKind.Object => [ParseParameterSet(document.RootElement)],
            _ => []
        };
    }

    private static IReadOnlyList<InspectionScriptResult> DeserializeInspectionResults(string stdOut)
    {
        if (string.IsNullOrWhiteSpace(stdOut))
        {
            return [];
        }

        var trimmed = stdOut.Trim();
        using var document = JsonDocument.Parse(trimmed);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.Object)
                .Select(ParseInspectionScriptResult)
                .ToArray(),
            JsonValueKind.Object => [ParseInspectionScriptResult(document.RootElement)],
            _ => []
        };
    }

    private static InspectionScriptResult ParseInspectionScriptResult(JsonElement element)
    {
        return new InspectionScriptResult(
            ReadString(element, "FullPath"),
            ReadBoolean(element, "Success"),
            ReadOptionalString(element, "ErrorMessage"),
            ReadObjectArray(element, "ParameterSets", ParseParameterSet));
    }

    private static IReadOnlyList<InspectionParameterSet> ParseParameterSets(JsonElement rootElement)
    {
        var parameterSets = new List<InspectionParameterSet>();
        foreach (var item in rootElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                parameterSets.Add(ParseParameterSet(item));
            }
        }

        return parameterSets;
    }

    private static InspectionParameterSet ParseParameterSet(JsonElement element)
    {
        return new InspectionParameterSet(
            ReadString(element, "Name"),
            ReadObjectArray(element, "Parameters", ParseParameter));
    }

    private static InspectionParameter ParseParameter(JsonElement element)
    {
        return new InspectionParameter(
            ReadString(element, "Name"),
            ReadBoolean(element, "IsMandatory"),
            ReadOptionalString(element, "TypeFullName"),
            ReadOptionalString(element, "TypeName"),
            ReadBoolean(element, "IsArray"),
            ReadInt32(element, "ArrayRank"),
            ReadOptionalString(element, "EffectiveTypeFullName"),
            ReadOptionalString(element, "EffectiveTypeName"),
            ReadBoolean(element, "IsNullable"),
            ReadBoolean(element, "IsEnum"),
            ReadStringArray(element, "EnumNames"));
    }

    private static IReadOnlyList<T> ReadObjectArray<T>(JsonElement element, string propertyName, Func<JsonElement, T> parser)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return [];
        }

        return property.ValueKind switch
        {
            JsonValueKind.Array => property.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.Object)
                .Select(parser)
                .ToArray(),
            JsonValueKind.Object => [parser(property)],
            _ => []
        };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return [];
        }

        return property.ValueKind switch
        {
            JsonValueKind.Array => property.EnumerateArray()
                .Select(ReadStringValue)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToArray()!,
            JsonValueKind.String => [property.GetString() ?? string.Empty],
            JsonValueKind.Null => [],
            JsonValueKind.Undefined => [],
            _ => [ReadStringValue(property)]
        };
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return ReadOptionalString(element, propertyName) ?? string.Empty;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => ReadStringValue(property)
        };
    }

    private static string ReadStringValue(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.ToString();
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var value) => value,
            _ => false
        };
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => 0
        };
    }

    private sealed record InspectionScriptResult(
        string FullPath,
        bool Success,
        string? ErrorMessage,
        IReadOnlyList<InspectionParameterSet> ParameterSets);

    private sealed record CachedScriptMetadata(
        int CacheFormatVersion,
        string FullPath,
        long FileLength,
        long LastWriteUtcTicks,
        string ItemId,
        string DisplayName,
        string RelativePath,
        PowerShellScriptExecutionMode ExecutionMode,
        IReadOnlyList<PowerShellScriptParameterDefinition> RequiredParameters,
        string? ErrorMessage);

    private sealed record InspectionParameterSet(string Name, IReadOnlyList<InspectionParameter> Parameters);

    private sealed record InspectionParameter(
        string Name,
        bool IsMandatory,
        string? TypeFullName,
        string? TypeName,
        bool IsArray,
        int ArrayRank,
        string? EffectiveTypeFullName,
        string? EffectiveTypeName,
        bool IsNullable,
        bool IsEnum,
        IReadOnlyList<string>? EnumNames);
}
