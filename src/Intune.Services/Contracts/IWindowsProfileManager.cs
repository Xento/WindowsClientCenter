using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Contracts;

public interface IWindowsProfileManager
{
    ValueTask<WindowsProfileSnapshot> GetProfilesAsync(string host, CancellationToken cancellationToken);
    ValueTask<WindowsProfileSizeResult> CalculateProfileSizeAsync(string host, string profileLocalPath, ProfileSizeCalculationMode mode, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> DeleteProfileAsync(string host, string sid, string profileLocalPath, CancellationToken cancellationToken);
}
