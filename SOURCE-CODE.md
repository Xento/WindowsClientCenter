# Corresponding Source for Binary Releases

Windows Client Center binary releases contain the modified LGPL-covered
`sccmclictr.automation.dll` as a separate managed assembly.

The exact corresponding source for that DLL is available in two forms:

1. In the release-matched source archive published with the same GitHub release
   at https://github.com/Xento/WindowsClientCenter/releases.
2. In the separately generated `WindowsClientCenter-LGPL-source.zip` release
   artifact.

The relevant source directory is `src/SccmCliCtr.Automation/`. Its upstream
revision and all local changes are documented in
`src/SccmCliCtr.Automation/MODIFICATIONS.md`.

## Build and replacement

Install the .NET 8 SDK, unpack the source artifact, and run:

```powershell
dotnet restore src/SccmCliCtr.Automation/SccmCliCtr.Automation.csproj
dotnet build src/SccmCliCtr.Automation/SccmCliCtr.Automation.csproj -c Release
```

The resulting assembly is located under
`src/SccmCliCtr.Automation/bin/Release/net8.0/`. Replace the distributed
`sccmclictr.automation.dll` with an interface-compatible modified build while
the application is stopped. Windows Client Center does not strong-name or
cryptographically lock that assembly and does not prohibit reverse engineering
for debugging modifications to the LGPL-covered library.

Release maintainers must publish the source artifacts at the same time and for
as long as the corresponding binary release is offered.

