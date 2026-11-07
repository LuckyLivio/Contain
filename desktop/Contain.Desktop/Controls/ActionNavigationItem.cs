using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Contain.Desktop.Controls;

/// <summary>
/// A WPF UI navigation entry with a real, accessible action button. WPF's
/// ItemsControl data-item peer masks patterns on the outer navigation entry,
/// so the stable action identity belongs to its child button instead.
/// </summary>
public sealed class ActionNavigationItem : NavigationViewItem
{
    private readonly TextBlock caption = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly SymbolIcon symbol = new() { FontSize = 16, VerticalAlignment = VerticalAlignment.Center };

    public ActionNavigationItem()
    {
        Focusable = false;
        IsTabStop = false;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(symbol);
        content.Children.Add(caption);
        caption.SetBinding(VisibilityProperty, new Binding(nameof(IsPaneOpen))
        {
            Source = this, Converter = new BooleanToVisibilityConverter()
        });
        ActionButton = new Button
        {
            Appearance = ControlAppearance.Transparent, Content = content,
            Padding = new Thickness(12, 8, 8, 8), MinHeight = 40,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(0)
        };
        ActionButton.Click += (_, args) =>
        {
            // Prevent the inner routed event and the forwarded outer event from
            // executing the shell handler twice. Also recheck a queued UIA click
            // after navigation was disabled for shutdown.
            args.Handled = true;
            if (IsEnabled && ActionButton.IsEnabled) OnClick();
        };
        Content = ActionButton;
    }

    public Button ActionButton { get; }
    public string Caption
    {
        get => caption.Text;
        set { caption.Text = value; AutomationProperties.SetName(ActionButton, value); ToolTip = value; }
    }
    public SymbolRegular Symbol { get => symbol.Symbol; set => symbol.Symbol = value; }
    public string ActionId
    {
        get => AutomationProperties.GetAutomationId(ActionButton);
        set { AutomationProperties.SetAutomationId(ActionButton, value); TargetPageTag = value; }
    }

    /// <summary>
    /// Use the same composition in the shell and the isolated external UIA test.
    /// The library's default compact template hides its Content presenter; here
    /// only the caption collapses, keeping the actual icon button operable.
    /// </summary>
    public static void ConfigureNavigation(NavigationView navigation)
    {
        navigation.ItemTemplate = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
                TargetType="{x:Type ui:NavigationViewItem}">
              <Border x:Name="Surface" Background="Transparent" CornerRadius="6">
                <Grid>
                  <ContentPresenter Content="{TemplateBinding Content}" HorizontalAlignment="Stretch" />
                  <Border x:Name="Selection" Width="3" Height="16" CornerRadius="2" HorizontalAlignment="Left"
                      VerticalAlignment="Center" Background="{DynamicResource NavigationViewSelectionIndicatorForeground}"
                      Visibility="Collapsed" IsHitTestVisible="False" />
                </Grid>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsActive" Value="True">
                  <Setter TargetName="Selection" Property="Visibility" Value="Visible" />
                  <Setter TargetName="Surface" Property="Background" Value="{DynamicResource NavigationViewItemBackgroundSelected}" />
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
    }
}
