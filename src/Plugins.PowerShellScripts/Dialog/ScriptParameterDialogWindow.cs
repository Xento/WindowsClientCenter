using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace WindowsClientCenter.Plugins.PowerShellScripts.Dialog;

internal sealed class ScriptParameterDialogWindow : Window
{
    private readonly List<ParameterInputRow> _parameterRows = [];
    private readonly TextBlock _errorTextBlock;

    public ScriptParameterDialogWindow(
        string scriptName,
        string computerName,
        IReadOnlyList<PowerShellScriptParameterDefinition> requiredParameters)
    {
        Title = $"Script Parameters - {scriptName}";
        Width = 720;
        Height = 520;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brushes.White;
        Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(static window => window.IsActive)
            ?? Application.Current?.MainWindow;

        var root = new DockPanel
        {
            Margin = new Thickness(16)
        };

        var footerPanel = new StackPanel
        {
            Orientation = Orientation.Vertical
        };
        DockPanel.SetDock(footerPanel, Dock.Bottom);
        root.Children.Add(footerPanel);

        _errorTextBlock = new TextBlock
        {
            Foreground = Brushes.Firebrick,
            Margin = new Thickness(0, 8, 0, 12),
            TextWrapping = TextWrapping.Wrap
        };
        footerPanel.Children.Add(_errorTextBlock);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        footerPanel.Children.Add(buttonPanel);

        var cancelButton = new Button
        {
            Content = "Cancel",
            Width = 90,
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        };
        buttonPanel.Children.Add(cancelButton);

        var runButton = new Button
        {
            Content = "Run Script",
            Width = 110,
            IsDefault = true
        };
        runButton.Click += OnRunButtonClick;
        buttonPanel.Children.Add(runButton);

        var introPanel = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(introPanel, Dock.Top);
        root.Children.Add(introPanel);

        introPanel.Children.Add(new TextBlock
        {
            Text = scriptName,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(23, 55, 88))
        });

        introPanel.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            Text = "The script defines a ComputerName parameter and additional required parameters.",
            Foreground = new SolidColorBrush(Color.FromRgb(83, 107, 136))
        });

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        root.Children.Add(scrollViewer);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scrollViewer.Content = grid;

        AddFixedComputerNameRow(grid, computerName);

        var currentRowIndex = 1;
        foreach (var parameter in requiredParameters)
        {
            AddParameterRow(grid, parameter, currentRowIndex++);
        }

        Content = root;
    }

    public IReadOnlyDictionary<string, string> ParameterLiterals { get; private set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private void AddFixedComputerNameRow(Grid grid, string computerName)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = CreateLabel("ComputerName", "String (fixed)");
        Grid.SetRow(label, 0);
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var textBox = new TextBox
        {
            Text = computerName,
            IsReadOnly = true,
            Background = new SolidColorBrush(Color.FromRgb(244, 247, 252)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(195, 205, 220)),
            Padding = new Thickness(8, 5, 8, 5)
        };
        Grid.SetRow(textBox, 0);
        Grid.SetColumn(textBox, 2);
        grid.Children.Add(textBox);
    }

    private void AddParameterRow(Grid grid, PowerShellScriptParameterDefinition definition, int rowIndex)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = CreateLabel(definition.Name, BuildDisplayType(definition));
        Grid.SetRow(label, rowIndex);
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var editor = CreateEditor(definition);
        editor.Root.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetRow(editor.Root, rowIndex);
        Grid.SetColumn(editor.Root, 2);
        grid.Children.Add(editor.Root);

        _parameterRows.Add(new ParameterInputRow(definition, editor));
    }

    private static TextBlock CreateLabel(string name, string typeText)
    {
        var textBlock = new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 10),
            TextWrapping = TextWrapping.Wrap
        };

        textBlock.Inlines.Add(new Run(name)
        {
            FontWeight = FontWeights.SemiBold
        });
        textBlock.Inlines.Add(new Run(Environment.NewLine));
        textBlock.Inlines.Add(new Run(typeText)
        {
            Foreground = new SolidColorBrush(Color.FromRgb(83, 107, 136))
        });

        return textBlock;
    }

    private static string BuildDisplayType(PowerShellScriptParameterDefinition definition)
    {
        var suffix = definition.IsArray ? "[]" : string.Empty;
        var nullable = definition.IsNullable ? "?" : string.Empty;
        return $"{definition.DisplayTypeName}{nullable}{suffix}";
    }

    private static ParameterEditor CreateEditor(PowerShellScriptParameterDefinition definition)
    {
        if (!definition.IsArray && definition.Kind == PowerShellScriptParameterKind.Switch)
        {
            var checkBox = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            return new ParameterEditor(
                checkBox,
                () => new ParseResult(true, checkBox.IsChecked == true ? "$true" : "$false", null));
        }

        if (!definition.IsArray &&
            (definition.Kind == PowerShellScriptParameterKind.Boolean || definition.Kind == PowerShellScriptParameterKind.Enum))
        {
            var comboBox = new ComboBox
            {
                MinWidth = 220,
                Padding = new Thickness(6, 2, 6, 2)
            };

            if (definition.Kind == PowerShellScriptParameterKind.Boolean)
            {
                comboBox.ItemsSource = new[] { "True", "False" };
            }
            else
            {
                comboBox.ItemsSource = definition.EnumValues ?? [];
            }

            comboBox.SelectedIndex = 0;
            return new ParameterEditor(
                comboBox,
                () =>
                {
                    var rawValue = comboBox.SelectedItem?.ToString() ?? string.Empty;
                    var success = PowerShellScriptLiteralBuilder.TryCreateLiteral(definition, [rawValue], out var literal, out var error);
                    return new ParseResult(success, literal, error);
                });
        }

        var textBox = new TextBox
        {
            MinWidth = 280,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = definition.IsArray,
            MinHeight = definition.IsArray ? 72 : 0,
            VerticalScrollBarVisibility = definition.IsArray ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
            Padding = new Thickness(8, 5, 8, 5)
        };

        return new ParameterEditor(
            textBox,
            () =>
            {
                var rawValues = definition.IsArray
                    ? SplitArrayValues(textBox.Text)
                    : [textBox.Text.Trim()];
                var success = PowerShellScriptLiteralBuilder.TryCreateLiteral(definition, rawValues, out var literal, out var error);
                return new ParseResult(success, literal, error);
            });
    }

    private static IReadOnlyList<string> SplitArrayValues(string text)
    {
        return text
            .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private void OnRunButtonClick(object sender, RoutedEventArgs e)
    {
        var parameterLiterals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in _parameterRows)
        {
            var result = row.Editor.Parse();
            if (!result.Success)
            {
                _errorTextBlock.Text = result.ErrorMessage ?? $"Parameter '{row.Definition.Name}' is invalid.";
                return;
            }

            parameterLiterals[row.Definition.Name] = result.Literal ?? string.Empty;
        }

        _errorTextBlock.Text = string.Empty;
        ParameterLiterals = parameterLiterals;
        DialogResult = true;
        Close();
    }

    private sealed record ParameterInputRow(PowerShellScriptParameterDefinition Definition, ParameterEditor Editor);

    private sealed record ParameterEditor(FrameworkElement Root, Func<ParseResult> Parse);

    private sealed record ParseResult(bool Success, string? Literal, string? ErrorMessage);
}
