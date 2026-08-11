using WindowsClientCenter.Shared.Diagnostics;

namespace WindowsClientCenter.Tests.IntuneServices;

public sealed class ErrorCodeResolverTests
{
    [Fact]
    public void Normalize_FormatsSignedDecimalAsHex()
    {
        var normalized = ErrorCodeResolver.Normalize("-2147024894");

        Assert.Equal("0x80070002", normalized);
    }

    [Fact]
    public void ResolveDescription_ResolvesWin32BackedHResult()
    {
        var description = ErrorCodeResolver.ResolveDescription("0x80070005");

        Assert.Contains("Access is denied.", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveDescription_UsesKnownOverrideForEnrollmentCode()
    {
        var description = ErrorCodeResolver.ResolveDescription("0x8018002B");

        Assert.Contains("already enrolled", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatForDisplay_AppendsDescriptionWhenKnown()
    {
        var display = ErrorCodeResolver.FormatForDisplay("2");

        Assert.Equal("0x00000002 - ERROR_FILE_NOT_FOUND - The system cannot find the file specified.", display);
    }

    [Fact]
    public void ResolveDescription_ResolvesWindowsUpdateSpecificCode()
    {
        var description = ErrorCodeResolver.ResolveDescription("0x80240017");

        Assert.Contains("not applicable", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveDescription_ResolvesIntuneSpecificCode()
    {
        var description = ErrorCodeResolver.ResolveDescription("0x87D1041C");

        Assert.Contains("not detected", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveDescription_ResolvesEnrollmentReuseCode()
    {
        var description = ErrorCodeResolver.ResolveDescription("0x80180014");

        Assert.Contains("re-enrollment", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveDescription_ResolvesEnrollmentPolicyBlockedCode()
    {
        var description = ErrorCodeResolver.ResolveDescription("0x80180026");

        Assert.Contains("blocked by policy", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lookup_ProvidesOfficialMetadataForWindowsUpdateCode()
    {
        var resolved = ErrorCodeResolver.Lookup("0x80240017");

        Assert.NotNull(resolved);
        Assert.Equal("0x80240017", resolved!.NormalizedCode);
        Assert.Equal("WU_E_NOT_APPLICABLE", resolved.Symbol);
        Assert.Equal(ErrorCodeCategory.WindowsUpdate, resolved.Category);
        Assert.Equal(ErrorCodeSource.MicrosoftLearn, resolved.Source);
        Assert.Equal(ErrorCodeConfidence.Official, resolved.Confidence);
    }

    [Fact]
    public void Lookup_ProvidesOfficialMetadataForEnrollmentCode()
    {
        var resolved = ErrorCodeResolver.Lookup("0x8018002B");

        Assert.NotNull(resolved);
        Assert.Equal(ErrorCodeCategory.Mdm, resolved!.Category);
        Assert.Equal(ErrorCodeSource.MicrosoftLearn, resolved.Source);
        Assert.Equal(ErrorCodeConfidence.Official, resolved.Confidence);
    }

    [Fact]
    public void Lookup_UsesCommunityCatalogForIntuneCode()
    {
        var resolved = ErrorCodeResolver.Lookup("0x87D00215");

        Assert.NotNull(resolved);
        Assert.Equal(ErrorCodeCategory.Intune, resolved!.Category);
        Assert.Equal(ErrorCodeSource.CommunityCatalog, resolved.Source);
        Assert.Equal(ErrorCodeConfidence.Community, resolved.Confidence);
        Assert.Contains("installation failed", resolved.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lookup_LoadsAdditionalCodesFromCsvLibrary()
    {
        var resolved = ErrorCodeResolver.Lookup("0x80CF201C");

        Assert.NotNull(resolved);
        Assert.Equal(ErrorCodeCategory.Intune, resolved!.Category);
        Assert.Equal(ErrorCodeSource.CommunityCatalog, resolved.Source);
        Assert.Equal(ErrorCodeConfidence.Community, resolved.Confidence);
        Assert.Contains("sideloading-enabled system", resolved.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lookup_ResolvesWingetSpecificCode()
    {
        var resolved = ErrorCodeResolver.Lookup("0x8A15010F");

        Assert.NotNull(resolved);
        Assert.Equal(ErrorCodeCategory.AppInstall, resolved!.Category);
        Assert.Equal(ErrorCodeSource.CommunityCatalog, resolved.Source);
        Assert.Equal(ErrorCodeConfidence.Community, resolved.Confidence);
        Assert.Contains("policies", resolved.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lookup_ResolvesDsregcmdAdalCodeFromGlobalCatalog()
    {
        var resolved = ErrorCodeResolver.Lookup("0xCAA90017");

        Assert.NotNull(resolved);
        Assert.Equal(ErrorCodeCategory.Entra, resolved!.Category);
        Assert.Equal(ErrorCodeSource.CommunityCatalog, resolved.Source);
        Assert.Contains("WS-Trust", resolved.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lookup_ResolvesCuratedCbsCode()
    {
        var resolved = ErrorCodeResolver.Lookup("0x800F0991");

        Assert.NotNull(resolved);
        Assert.Equal(ErrorCodeCategory.Cbs, resolved!.Category);
        Assert.Equal(ErrorCodeConfidence.Community, resolved.Confidence);
        Assert.Contains("payload file", resolved.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lookup_ResolvesWin32AndHResultForms()
    {
        var win32 = ErrorCodeResolver.Lookup("5");
        var hresult = ErrorCodeResolver.Lookup("0x80070005");

        Assert.NotNull(win32);
        Assert.NotNull(hresult);
        Assert.Contains("Access is denied", win32!.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Access is denied", hresult!.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindInText_ReturnsRecognizedCodesWithMetadata()
    {
        var detected = ErrorCodeResolver.FindInText("Install failed with 0x80070005 and then 0x80240017.");

        Assert.Equal(2, detected.Count);
        Assert.Equal("0x80070005", detected[0].Resolution.NormalizedCode);
        Assert.Equal(ErrorCodeCategory.Windows, detected[0].Resolution.Category);
        Assert.Equal("0x80240017", detected[1].Resolution.NormalizedCode);
        Assert.Equal("WU_E_NOT_APPLICABLE", detected[1].Resolution.Symbol);
    }

    [Fact]
    public void FormatForDisplay_LeavesUnknownCodeNormalized()
    {
        var display = ErrorCodeResolver.FormatForDisplay("0xDEADBEEF");

        Assert.Equal("0xDEADBEEF", display);
    }
}
