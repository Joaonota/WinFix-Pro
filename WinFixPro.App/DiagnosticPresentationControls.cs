using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinFixPro;

public sealed class DiagnosticResultCard : Border
{
    public DiagnosticResultCard(
        DiagnosticFindingPresentation finding,
        Brush statusBrush,
        Brush primaryBrush,
        Brush secondaryBrush,
        Brush panelBrush,
        Brush borderBrush)
    {
        Background = panelBrush;
        BorderBrush = statusBrush;
        BorderThickness = new Thickness(3, 0, 0, 0);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(14);
        Margin = new Thickness(0, 0, 0, 10);

        var content = new StackPanel();
        var heading = new DockPanel { LastChildFill = true };
        var badge = new Border
        {
            Background = panelBrush,
            BorderBrush = statusBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(8, 0, 0, 0),
            Child = new TextBlock
            {
                Text = finding.StatusLabel,
                Foreground = statusBrush,
                FontWeight = FontWeights.SemiBold,
                FontSize = 10
            }
        };
        DockPanel.SetDock(badge, Dock.Right);
        heading.Children.Add(badge);
        heading.Children.Add(new TextBlock
        {
            Text = finding.Title,
            Foreground = primaryBrush,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(heading);

        AddField(content, "Componente", finding.Component, primaryBrush, secondaryBrush);
        AddField(content, "Resumo", finding.Description, primaryBrush, secondaryBrush);
        AddField(content, "Evidências", finding.Evidence, primaryBrush, secondaryBrush);
        AddField(content, "Possível causa", finding.PossibleCause, primaryBrush, secondaryBrush);
        AddField(content, "Impacto", finding.Impact, primaryBrush, secondaryBrush);

        var recommendation = new Border
        {
            Background = panelBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 10, 0, 0),
            Child = new StackPanel()
        };
        var recommendationPanel = (StackPanel)recommendation.Child;
        recommendationPanel.Children.Add(new TextBlock
        {
            Text = "Recomendação e próxima ação",
            Foreground = secondaryBrush,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold
        });
        recommendationPanel.Children.Add(new TextBlock
        {
            Text = finding.Recommendation,
            Foreground = primaryBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });
        if (!string.Equals(finding.NextAction, finding.Recommendation, StringComparison.Ordinal))
        {
            recommendationPanel.Children.Add(new TextBlock
            {
                Text = $"Próxima ação: {finding.NextAction}",
                Foreground = primaryBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }
        content.Children.Add(recommendation);
        Child = content;
    }

    private static void AddField(Panel panel, string label, string value, Brush primaryBrush, Brush secondaryBrush)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var field = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        field.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = secondaryBrush,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold
        });
        field.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = primaryBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        panel.Children.Add(field);
    }
}

public sealed class TechnicalDetailsPanel : Expander
{
    public TechnicalDetailsPanel(string details, Brush foreground, Brush background, Brush borderBrush)
    {
        Header = "Detalhes técnicos";
        IsExpanded = false;
        Foreground = foreground;
        Background = background;
        BorderBrush = borderBrush;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(10);
        Margin = new Thickness(0, 4, 0, 10);
        Content = new TextBox
        {
            Text = details,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 360,
            Background = background,
            Foreground = foreground,
            BorderBrush = borderBrush,
            Padding = new Thickness(10),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10
        };
    }
}

public sealed class DiagnosticProgressPanel : Border
{
    private readonly TextBlock _message;
    private readonly ProgressBar _progress;

    public DiagnosticProgressPanel(Brush foreground, Brush secondaryBrush, Brush background, Brush borderBrush)
    {
        Background = background;
        BorderBrush = borderBrush;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(12);
        Margin = new Thickness(0, 0, 0, 10);
        Visibility = Visibility.Collapsed;

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "A operação está em curso",
            Foreground = foreground,
            FontWeight = FontWeights.SemiBold
        });
        _message = new TextBlock
        {
            Text = "A preparar a análise…",
            Foreground = secondaryBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 8)
        };
        content.Children.Add(_message);
        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 8 };
        content.Children.Add(_progress);
        Child = content;
    }

    public void Update(int percent, string message)
    {
        _progress.Value = Math.Clamp(percent, 0, 100);
        _message.Text = message;
        Visibility = Visibility.Visible;
    }
}
