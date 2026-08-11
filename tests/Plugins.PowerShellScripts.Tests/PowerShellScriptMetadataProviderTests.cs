using System.Collections;
using System.Reflection;
using WindowsClientCenter.Plugins.PowerShellScripts;
using Xunit;

namespace WindowsClientCenter.Tests.Plugins.PowerShellScripts;

public sealed class PowerShellScriptMetadataProviderTests
{
    [Fact]
    public async Task LoadAsync_ClassifiesScriptWithoutParamBlockAsRemotingWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var scriptDirectory = Path.Combine(Path.GetTempPath(), $"icc-ps-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scriptDirectory);
        var scriptPath = Path.Combine(scriptDirectory, "Get-TopCpuProcesses.ps1");

        try
        {
            await File.WriteAllTextAsync(
                scriptPath,
                """
                $processes = Get-Process |
                    Sort-Object -Property CPU -Descending |
                    Select-Object -First 10 Name, Id, CPU

                $processes | Format-Table -AutoSize
                """);

            var provider = new PowerShellScriptMetadataProvider();

            var entries = await provider.LoadAsync(scriptDirectory, CancellationToken.None);

            var entry = Assert.Single(entries);
            Assert.Equal(PowerShellScriptExecutionMode.RemotingWindow, entry.ExecutionMode);
            Assert.Null(entry.ErrorMessage);
            Assert.Empty(entry.RequiredParameters);
        }
        finally
        {
            if (Directory.Exists(scriptDirectory))
            {
                Directory.Delete(scriptDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void DeserializeParameterSets_AcceptsEnumNamesSerializedAsSingleString()
    {
        const string json = """
            [
              {
                "Name": "__AllParameterSets",
                "Parameters": [
                  {
                    "Name": "Mode",
                    "IsMandatory": true,
                    "TypeFullName": "Demo.Mode",
                    "TypeName": "Mode",
                    "IsArray": false,
                    "ArrayRank": 0,
                    "EffectiveTypeFullName": "Demo.Mode",
                    "EffectiveTypeName": "Mode",
                    "IsNullable": false,
                    "IsEnum": true,
                    "EnumNames": "Fast"
                  }
                ]
              }
            ]
            """;

        var parameterSets = InvokeDeserializeParameterSets(json);
        var parameterSet = Assert.Single(parameterSets);
        var parameters = ReadCollectionProperty(parameterSet, "Parameters");
        var parameter = Assert.Single(parameters);
        var enumNames = Assert.IsAssignableFrom<IReadOnlyList<string>?>(parameter.GetType().GetProperty("EnumNames")!.GetValue(parameter));

        Assert.Equal(["Fast"], enumNames);
    }

    [Fact]
    public void DeserializeParameterSets_AcceptsSingleParameterObjectInsteadOfArray()
    {
        const string json = """
            {
              "Name": "__AllParameterSets",
              "Parameters": {
                "Name": "ComputerName",
                "IsMandatory": true,
                "TypeFullName": "System.String",
                "TypeName": "String",
                "IsArray": false,
                "ArrayRank": 0,
                "EffectiveTypeFullName": "System.String",
                "EffectiveTypeName": "String",
                "IsNullable": false,
                "IsEnum": false,
                "EnumNames": null
              }
            }
            """;

        var parameterSets = InvokeDeserializeParameterSets(json);
        var parameterSet = Assert.Single(parameterSets);
        var parameters = ReadCollectionProperty(parameterSet, "Parameters");
        var parameter = Assert.Single(parameters);

        Assert.Equal("ComputerName", parameter.GetType().GetProperty("Name")!.GetValue(parameter));
    }

    [Fact]
    public void DeserializeInspectionResults_AcceptsSingleScriptObject()
    {
        const string json = """
            {
              "FullPath": "C:/Scripts/Test.ps1",
              "Success": true,
              "ErrorMessage": null,
              "ParameterSets": {
                "Name": "__AllParameterSets",
                "Parameters": {
                  "Name": "ComputerName",
                  "IsMandatory": true,
                  "TypeFullName": "System.String",
                  "TypeName": "String",
                  "IsArray": false,
                  "ArrayRank": 0,
                  "EffectiveTypeFullName": "System.String",
                  "EffectiveTypeName": "String",
                  "IsNullable": false,
                  "IsEnum": false,
                  "EnumNames": null
                }
              }
            }
            """;

        var scriptResults = InvokeDeserializeInspectionResults(json);
        var scriptResult = Assert.Single(scriptResults);
        Assert.Equal("C:/Scripts/Test.ps1", scriptResult.GetType().GetProperty("FullPath")!.GetValue(scriptResult));
        Assert.Equal(true, scriptResult.GetType().GetProperty("Success")!.GetValue(scriptResult));
        var parameterSets = ReadCollectionProperty(scriptResult, "ParameterSets");
        Assert.Single(parameterSets);
    }

    [Fact]
    public void DeserializeInspectionResults_AcceptsErrorEntry()
    {
        const string json = """
            [
              {
                "FullPath": "C:/Scripts/Broken.ps1",
                "Success": false,
                "ErrorMessage": "Inspection failed.",
                "ParameterSets": []
              }
            ]
            """;

        var scriptResults = InvokeDeserializeInspectionResults(json);
        var scriptResult = Assert.Single(scriptResults);
        Assert.Equal(false, scriptResult.GetType().GetProperty("Success")!.GetValue(scriptResult));
        Assert.Equal("Inspection failed.", scriptResult.GetType().GetProperty("ErrorMessage")!.GetValue(scriptResult));
    }

    private static object[] InvokeDeserializeParameterSets(string json)
    {
        var method = typeof(PowerShellScriptMetadataProvider).GetMethod(
            "DeserializeParameterSets",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var result = method!.Invoke(null, [json]);
        Assert.NotNull(result);

        return ((IEnumerable)result!).Cast<object>().ToArray();
    }

    private static object[] InvokeDeserializeInspectionResults(string json)
    {
        var method = typeof(PowerShellScriptMetadataProvider).GetMethod(
            "DeserializeInspectionResults",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var result = method!.Invoke(null, [json]);
        Assert.NotNull(result);

        return ((IEnumerable)result!).Cast<object>().ToArray();
    }

    private static object[] ReadCollectionProperty(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        var value = property!.GetValue(target);
        Assert.NotNull(value);
        return ((IEnumerable)value!).Cast<object>().ToArray();
    }
}
