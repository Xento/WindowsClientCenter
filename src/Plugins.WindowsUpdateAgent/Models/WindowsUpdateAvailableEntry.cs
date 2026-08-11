using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models;

public partial class WindowsUpdateAvailableEntry : ObservableObject
{
    public WindowsUpdateAvailableEntry(
        string title,
        string type,
        string status,
        bool isInstalled,
        bool isHidden,
        string kbArticles,
        bool isDownloaded,
        bool isMandatory,
        bool eulaAccepted,
        string categories,
        string deadline,
        string updateId,
        int revision)
    {
        Title = title;
        Type = NormalizeType(type);
        Status = status;
        IsInstalled = isInstalled;
        IsHidden = isHidden;
        KbArticles = kbArticles;
        IsDownloaded = isDownloaded;
        IsMandatory = isMandatory;
        EulaAccepted = eulaAccepted;
        Categories = categories;
        Deadline = deadline;
        UpdateId = updateId;
        Revision = revision;
    }

    public string Title { get; }
    public string Type { get; }
    public string Status { get; }
    public bool IsInstalled { get; }
    public bool IsHidden { get; }
    public string KbArticles { get; }
    public bool IsDownloaded { get; }
    public bool IsMandatory { get; }
    public bool EulaAccepted { get; }
    public string Categories { get; }
    public string Deadline { get; }
    public string UpdateId { get; }
    public int Revision { get; }
    public bool IsAvailable => !IsInstalled && !IsHidden;

    [ObservableProperty]
    private bool _isSelected;

    private static string NormalizeType(string type)
    {
        var normalized = type.Trim();
        return normalized switch
        {
            "1" => "Software",
            "2" => "Driver",
            _ when normalized.Equals("software", StringComparison.OrdinalIgnoreCase) => "Software",
            _ when normalized.Equals("driver", StringComparison.OrdinalIgnoreCase) => "Driver",
            _ => normalized
        };
    }
}
