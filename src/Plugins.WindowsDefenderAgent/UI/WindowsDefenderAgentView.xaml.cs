using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace WindowsClientCenter.Plugins.WindowsDefenderAgent.UI;

public partial class WindowsDefenderAgentView : UserControl
{
    public WindowsDefenderAgentView()
    {
        InitializeComponent();
    }

    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch
        {
            // Ignore browser launch errors; the URL remains visible in the UI.
        }
    }
}
