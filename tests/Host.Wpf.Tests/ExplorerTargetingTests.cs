using WindowsClientCenter.Host.Runtime;
using Xunit;

namespace WindowsClientCenter.Tests.HostWpf;

public sealed class ExplorerTargetingTests
{
    [Fact]
    public void BuildTargets_GroupsEntriesIntoFolderHierarchy_AndReplacesHostPlaceholder()
    {
        var targets = ExplorerTargeting.BuildTargets(
            [
                new HostExplorerTargetOptions
                {
                    Name = "Remote Shares",
                    Children =
                    [
                        new HostExplorerTargetOptions { Name = "Remote C$", Path = "\\\\%HOSTNAME%\\c$", IsDefault = true },
                        new HostExplorerTargetOptions { Name = "Remote Admin$", Path = "\\\\%HOSTNAME%\\admin$" }
                    ]
                },
                new HostExplorerTargetOptions
                {
                    Name = "Local",
                    Children =
                    [
                        new HostExplorerTargetOptions { Name = "Local Tools", Path = "C:\\Tools" }
                    ]
                }
            ],
            "pc01");

        var remoteFolder = Assert.Single(targets, target => target.IsFolder && target.Name == "Remote Shares");
        Assert.Equal("\uE8B7", remoteFolder.IconGlyph);
        Assert.Equal(2, remoteFolder.Children.Count);

        var remote = Assert.Single(remoteFolder.Children, target => target.IsDefault);
        Assert.Equal("Remote C$", remote.Name);
        Assert.Equal("\\\\pc01\\c$", remote.ResolvedPath);
        Assert.True(remote.IsEnabled);
        Assert.Equal("\uE8B7", remote.IconGlyph);

        var localFolder = Assert.Single(targets, target => target.IsFolder && target.Name == "Local");
        var local = Assert.Single(localFolder.Children);
        Assert.Equal("Local Tools", local.Name);
        Assert.Equal("C:\\Tools", local.ResolvedPath);
        Assert.True(local.IsEnabled);
    }

    [Fact]
    public void BuildTargets_DisablesHostDependentTargets_WhenHostIsMissing()
    {
        var targets = ExplorerTargeting.BuildTargets(
            [
                new HostExplorerTargetOptions
                {
                    Name = "Remote Shares",
                    Children =
                    [
                        new HostExplorerTargetOptions { Name = "Remote C$", Path = "\\\\%HOSTNAME%\\c$", IsDefault = true }
                    ]
                }
            ],
            currentHost: string.Empty);

        var target = Assert.Single(Assert.Single(targets).Children);
        Assert.Null(target.ResolvedPath);
        Assert.False(target.IsEnabled);
    }

    [Fact]
    public void BuildTargets_RendersGroupChildrenInlineAfterHeader()
    {
        var targets = ExplorerTargeting.BuildTargets(
            [
                new HostExplorerTargetOptions
                {
                    Name = "Windows",
                    Type = "Group",
                    Children =
                    [
                        new HostExplorerTargetOptions { Name = "C$", Path = "\\\\%HOSTNAME%\\c$", IsDefault = true },
                        new HostExplorerTargetOptions { Name = "Temp", Path = "\\\\%HOSTNAME%\\c$\\Windows\\Temp" }
                    ]
                }
            ],
            currentHost: "pc01");

        Assert.Equal(3, targets.Count);
        Assert.True(targets[0].IsGroupHeader);
        Assert.False(targets[0].IsOpenTarget);
        Assert.False(targets[0].IsFolder);
        Assert.Equal("Windows", targets[0].Name);
        Assert.True(targets[0].IsEnabled);
        Assert.Equal(string.Empty, targets[0].IconGlyph);

        Assert.True(targets[1].IsOpenTarget);
        Assert.Equal("C$", targets[1].Name);
        Assert.Equal("\\\\pc01\\c$", targets[1].ResolvedPath);
        Assert.True(targets[2].IsOpenTarget);
        Assert.Equal("Temp", targets[2].Name);
    }

    [Fact]
    public void BuildTargets_RendersExplicitFolderAsSubMenu()
    {
        var targets = ExplorerTargeting.BuildTargets(
            [
                new HostExplorerTargetOptions
                {
                    Name = "MECM",
                    Type = "Folder",
                    Children =
                    [
                        new HostExplorerTargetOptions { Name = "CCM Logs", Path = "\\\\%HOSTNAME%\\c$\\Windows\\CCM\\Logs" }
                    ]
                }
            ],
            currentHost: "pc01");

        var folder = Assert.Single(targets);
        Assert.True(folder.IsFolder);
        Assert.False(folder.IsGroupHeader);
        Assert.False(folder.IsOpenTarget);
        Assert.Equal("MECM", folder.Name);

        var child = Assert.Single(folder.Children);
        Assert.True(child.IsOpenTarget);
        Assert.Equal("CCM Logs", child.Name);
    }

    [Fact]
    public void BuildTargets_RendersGroupInsideFolderWithoutCreatingNestedSubMenu()
    {
        var targets = ExplorerTargeting.BuildTargets(
            [
                new HostExplorerTargetOptions
                {
                    Name = "Tools",
                    Type = "Folder",
                    Children =
                    [
                        new HostExplorerTargetOptions
                        {
                            Name = "Windows",
                            Type = "Group",
                            Children =
                            [
                                new HostExplorerTargetOptions { Name = "Temp", Path = "\\\\%HOSTNAME%\\c$\\Windows\\Temp" }
                            ]
                        },
                        new HostExplorerTargetOptions { Name = "Root", Path = "\\\\%HOSTNAME%\\c$" }
                    ]
                }
            ],
            currentHost: "pc01");

        var folder = Assert.Single(targets);
        Assert.True(folder.IsFolder);
        Assert.Equal(3, folder.Children.Count);
        Assert.True(folder.Children[0].IsGroupHeader);
        Assert.Equal("Windows", folder.Children[0].Name);
        Assert.True(folder.Children[1].IsOpenTarget);
        Assert.Equal("Temp", folder.Children[1].Name);
        Assert.True(folder.Children[2].IsOpenTarget);
        Assert.Equal("Root", folder.Children[2].Name);
    }

    [Fact]
    public void ResolveDefaultTarget_PrefersExplicitDefault_AndFallsBackToFirstEntry()
    {
        var explicitDefaultTargets = ExplorerTargeting.BuildTargets(
            [
                new HostExplorerTargetOptions
                {
                    Name = "A",
                    Children =
                    [
                        new HostExplorerTargetOptions { Name = "First", Path = "C:\\First" },
                        new HostExplorerTargetOptions { Name = "Default", Path = "C:\\Default", IsDefault = true }
                    ]
                }
            ],
            currentHost: "pc01");

        Assert.Equal("Default", ExplorerTargeting.ResolveDefaultTarget(explicitDefaultTargets)?.Name);

        var fallbackTargets = ExplorerTargeting.BuildTargets(
            [
                new HostExplorerTargetOptions
                {
                    Name = "A",
                    Children =
                    [
                        new HostExplorerTargetOptions { Name = "First", Path = "C:\\First" }
                    ]
                },
                new HostExplorerTargetOptions
                {
                    Name = "B",
                    Children =
                    [
                        new HostExplorerTargetOptions { Name = "Second", Path = "C:\\Second" }
                    ]
                }
            ],
            currentHost: "pc01");

        Assert.Equal("First", ExplorerTargeting.ResolveDefaultTarget(fallbackTargets)?.Name);
    }

    [Fact]
    public void ResolveDefaultTarget_IgnoresGroupHeaders()
    {
        var targets = ExplorerTargeting.BuildTargets(
            [
                new HostExplorerTargetOptions
                {
                    Name = "Grouped",
                    Type = "Group",
                    Children =
                    [
                        new HostExplorerTargetOptions { Name = "First", Path = "C:\\First" }
                    ]
                }
            ],
            currentHost: "pc01");

        Assert.Equal("First", ExplorerTargeting.ResolveDefaultTarget(targets)?.Name);
    }

    [Fact]
    public void BuildStartInfo_QuotesTheResolvedPath()
    {
        var startInfo = ExplorerTargeting.BuildStartInfo(@"C:\Program Files\Some Folder");

        Assert.Equal("explorer.exe", startInfo.FileName);
        Assert.Equal("\"C:\\Program Files\\Some Folder\"", startInfo.Arguments);
        Assert.True(startInfo.UseShellExecute);
    }
}
