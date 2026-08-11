using System.Collections.ObjectModel;
using System.Diagnostics;

namespace WindowsClientCenter.Host.Runtime;

public sealed class ExplorerTargetItem
{
    private ExplorerTargetItem(
        string name,
        string? menuPath,
        string iconGlyph,
        bool isFolder,
        bool isGroupHeader,
        string? pathTemplate,
        string? resolvedPath,
        bool isDefault,
        bool isEnabled)
    {
        Name = name;
        MenuPath = menuPath;
        IconGlyph = iconGlyph;
        IsFolder = isFolder;
        IsGroupHeader = isGroupHeader;
        PathTemplate = pathTemplate;
        ResolvedPath = resolvedPath;
        IsDefault = isDefault;
        IsEnabled = isEnabled;
    }

    public string Name { get; }

    public string? MenuPath { get; }

    public string IconGlyph { get; }

    public bool IsFolder { get; }

    public bool IsGroupHeader { get; }

    public bool IsOpenTarget => !IsFolder && !IsGroupHeader;

    public string? PathTemplate { get; }

    public string? ResolvedPath { get; }

    public bool IsDefault { get; }

    public bool IsEnabled { get; }

    public ObservableCollection<ExplorerTargetItem> Children { get; } = [];

    public string? ToolTip => IsFolder
        ? null
        : ResolvedPath;

    public static ExplorerTargetItem CreateFolder(string name, string? menuPath)
    {
        return new ExplorerTargetItem(
            name,
            menuPath,
            iconGlyph: "\uE8B7",
            isFolder: true,
            isGroupHeader: false,
            pathTemplate: null,
            resolvedPath: null,
            isDefault: false,
            isEnabled: true);
    }

    public static ExplorerTargetItem CreateGroupHeader(string name, string? menuPath)
    {
        return new ExplorerTargetItem(
            name,
            menuPath,
            iconGlyph: string.Empty,
            isFolder: false,
            isGroupHeader: true,
            pathTemplate: null,
            resolvedPath: null,
            isDefault: false,
            isEnabled: true);
    }

    public static ExplorerTargetItem CreateLeaf(
        string name,
        string? menuPath,
        string pathTemplate,
        string? resolvedPath,
        bool isDefault,
        bool isEnabled)
    {
        return new ExplorerTargetItem(
            name,
            menuPath,
            iconGlyph: "\uE8B7",
            isFolder: false,
            isGroupHeader: false,
            pathTemplate: pathTemplate,
            resolvedPath: string.IsNullOrWhiteSpace(resolvedPath) ? null : resolvedPath,
            isDefault: isDefault,
            isEnabled: isEnabled);
    }
}

public static class ExplorerTargeting
{
    private const string HostNamePlaceholder = "%HOSTNAME%";
    private const string GroupTargetType = "Group";

    public static IReadOnlyList<ExplorerTargetItem> BuildTargets(
        IEnumerable<HostExplorerTargetOptions>? targets,
        string? currentHost)
    {
        var normalizedHost = string.IsNullOrWhiteSpace(currentHost)
            ? null
            : currentHost.Trim();

        var root = new ExplorerTreeNode(string.Empty, string.Empty, isFolder: true);
        foreach (var target in (targets ?? []).Where(static target => !string.IsNullOrWhiteSpace(target.Path) || target.Children.Count > 0))
        {
            AddTarget(root, target, normalizedHost);
        }

        return BuildMenuItems(root.Children);
    }

    public static ExplorerTargetItem? ResolveDefaultTarget(IReadOnlyList<ExplorerTargetItem> targets)
    {
        foreach (var target in EnumerateLeafTargets(targets))
        {
            if (target.IsDefault)
            {
                return target;
            }
        }

        return EnumerateLeafTargets(targets).FirstOrDefault();
    }

    public static ProcessStartInfo BuildStartInfo(string resolvedPath)
    {
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            throw new ArgumentException("Explorer path must not be empty.", nameof(resolvedPath));
        }

        return new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{resolvedPath}\"",
            UseShellExecute = true
        };
    }

    public static string ResolvePath(string pathTemplate, string? currentHost)
    {
        if (string.IsNullOrWhiteSpace(pathTemplate))
        {
            return string.Empty;
        }

        var trimmedTemplate = pathTemplate.Trim();
        if (!trimmedTemplate.Contains(HostNamePlaceholder, StringComparison.OrdinalIgnoreCase))
        {
            return trimmedTemplate;
        }

        if (string.IsNullOrWhiteSpace(currentHost))
        {
            return string.Empty;
        }

        return trimmedTemplate.Replace(HostNamePlaceholder, currentHost.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static void AddTarget(ExplorerTreeNode root, HostExplorerTargetOptions target, string? currentHost)
    {
        if (target.Children.Count > 0)
        {
            var folderName = string.IsNullOrWhiteSpace(target.Name)
                ? target.MenuPath?.Trim() ?? string.Empty
                : target.Name.Trim();

            if (string.IsNullOrWhiteSpace(folderName))
            {
                foreach (var child in target.Children)
                {
                    AddTarget(root, child, currentHost);
                }

                return;
            }

            var menuPath = NormalizeMenuPath(target.MenuPath);
            var current = GetMenuPathNode(root, menuPath);

            if (IsGroup(target))
            {
                current.Children.Add(new ExplorerTreeNode(
                    folderName,
                    menuPath ?? string.Empty,
                    isFolder: false,
                    ExplorerTargetItem.CreateGroupHeader(folderName, menuPath)));

                foreach (var child in target.Children)
                {
                    AddTarget(current, child, currentHost);
                }

                return;
            }

            current = current.GetOrAddFolder(folderName, menuPath is null ? folderName : $"{menuPath}/{folderName}");
            foreach (var child in target.Children)
            {
                AddTarget(current, child, currentHost);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(target.Path))
        {
            return;
        }

        var menuPathValue = NormalizeMenuPath(target.MenuPath);
        var currentValue = GetMenuPathNode(root, menuPathValue);

        var name = string.IsNullOrWhiteSpace(target.Name)
            ? target.Path.Trim()
            : target.Name.Trim();
        var pathTemplate = target.Path.Trim();
        var resolvedPath = ResolvePath(pathTemplate, currentHost);
        var requiresHost = pathTemplate.Contains(HostNamePlaceholder, StringComparison.OrdinalIgnoreCase);
        var isEnabled = !requiresHost || !string.IsNullOrWhiteSpace(resolvedPath);

        currentValue.Children.Add(new ExplorerTreeNode(
            name,
            menuPathValue ?? string.Empty,
            isFolder: false,
            ExplorerTargetItem.CreateLeaf(
                name,
                menuPathValue ?? string.Empty,
                pathTemplate,
                resolvedPath,
                target.IsDefault,
                isEnabled)));
    }

    private static IReadOnlyList<ExplorerTargetItem> BuildMenuItems(IReadOnlyList<ExplorerTreeNode> nodes)
    {
        return nodes
            .Select(static node => node.IsFolder
                ? BuildFolderItem(node)
                : node.Target!)
            .ToArray();
    }

    private static ExplorerTargetItem BuildFolderItem(ExplorerTreeNode node)
    {
        var folder = ExplorerTargetItem.CreateFolder(node.Name, node.Path);
        foreach (var child in BuildMenuItems(node.Children))
        {
            folder.Children.Add(child);
        }

        return folder;
    }

    private static IEnumerable<ExplorerTargetItem> EnumerateLeafTargets(IEnumerable<ExplorerTargetItem> targets)
    {
        foreach (var target in targets)
        {
            if (target.IsFolder)
            {
                foreach (var child in EnumerateLeafTargets(target.Children))
                {
                    yield return child;
                }

                continue;
            }

            if (!target.IsOpenTarget)
            {
                continue;
            }

            yield return target;
        }
    }

    private static ExplorerTreeNode GetMenuPathNode(ExplorerTreeNode root, string? menuPath)
    {
        var current = root;
        if (menuPath is null)
        {
            return current;
        }

        var segments = menuPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var currentPath = string.Join('/', segments.Take(i + 1));
            current = current.GetOrAddFolder(segments[i], currentPath);
        }

        return current;
    }

    private static bool IsGroup(HostExplorerTargetOptions target)
    {
        return target.Type.Equals(GroupTargetType, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeMenuPath(string? menuPath)
    {
        if (string.IsNullOrWhiteSpace(menuPath))
        {
            return null;
        }

        var normalized = menuPath.Trim().Replace('\\', '/');
        normalized = string.Join('/', normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed class ExplorerTreeNode(string name, string path, bool isFolder, ExplorerTargetItem? target = null)
    {
        public string Name { get; } = name;

        public string Path { get; } = path;

        public bool IsFolder { get; } = isFolder;

        public ExplorerTargetItem? Target { get; } = target;

        public List<ExplorerTreeNode> Children { get; } = [];

        public ExplorerTreeNode GetOrAddFolder(string folderName, string folderPath)
        {
            var existing = Children.FirstOrDefault(node =>
                node.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            var created = new ExplorerTreeNode(folderName, folderPath, isFolder: true);
            Children.Add(created);
            return created;
        }
    }
}
