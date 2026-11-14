using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;

namespace Zhulong.Desktop;

internal sealed class ZhulongContextMenu : ContextMenu
{
    static readonly ControlTemplate MenuTemplate = (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         TargetType="{x:Type ContextMenu}">
            <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                    BorderThickness="1" CornerRadius="9" Padding="5">
                <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle"/>
            </Border>
        </ControlTemplate>
        """);

    static readonly ControlTemplate RowTemplate = (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         TargetType="{x:Type MenuItem}">
            <Border x:Name="Row" CornerRadius="5" Background="Transparent" Padding="9,4" MinHeight="28">
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="18"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <TextBlock x:Name="Check" VerticalAlignment="Center" Text="✓" Visibility="Hidden" />
                    <ContentPresenter Grid.Column="1" ContentSource="Header" RecognizesAccessKey="True"
                                      VerticalAlignment="Center"/>
                </Grid>
            </Border>
            <ControlTemplate.Triggers>
                <Trigger Property="IsHighlighted" Value="True">
                    <Setter TargetName="Row" Property="Background" Value="{Binding BorderBrush, RelativeSource={RelativeSource TemplatedParent}}"/>
                </Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True">
                    <Setter TargetName="Row" Property="Background" Value="{Binding BorderBrush, RelativeSource={RelativeSource TemplatedParent}}"/>
                </Trigger>
                <Trigger Property="IsChecked" Value="True">
                    <Setter TargetName="Check" Property="Visibility" Value="Visible"/>
                </Trigger>
                <Trigger Property="IsEnabled" Value="False">
                    <Setter TargetName="Row" Property="Opacity" Value=".45"/>
                </Trigger>
            </ControlTemplate.Triggers>
        </ControlTemplate>
        """);
    readonly Style rowStyle;

    internal ZhulongContextMenu(bool night = true)
    {
        Background = Brush(night ? 0x222b28 : 0xf3f0e7);
        BorderBrush = Brush(night ? 0x475750 : 0xc6cabe);
        Foreground = Brush(night ? 0xe9eee6 : 0x253d34);
        var selected = Brush(night ? 0x35463d : 0xdce6d9);
        FontSize = 12;
        MinWidth = 146;
        SnapsToDevicePixels = UseLayoutRounding = true;
        Template = MenuTemplate;
        rowStyle = new Style(typeof(MenuItem))
        {
            Setters =
            {
                new Setter(TemplateProperty, RowTemplate),
                new Setter(BorderBrushProperty, selected),
                new Setter(ForegroundProperty, Foreground),
                new Setter(FocusVisualStyleProperty, null)
            }
        };

        var line = new FrameworkElementFactory(typeof(Border));
        line.SetValue(Border.HeightProperty, 1d);
        line.SetValue(Border.MarginProperty, new Thickness(8, 4, 8, 4));
        line.SetValue(Border.BackgroundProperty, BorderBrush);
        Resources[typeof(Separator)] = new Style(typeof(Separator))
        {
            Setters = { new Setter(TemplateProperty, new ControlTemplate(typeof(Separator)) { VisualTree = line }) }
        };
    }

    internal MenuItem Add(string caption, Action execute, bool isChecked = false)
    {
        var item = new MenuItem { Header = caption, IsChecked = isChecked, Style = rowStyle };
        item.Click += (_, _) => execute();
        Items.Add(item);
        return item;
    }

    internal void OpenAtPointer(UIElement? target = null)
    {
        PlacementTarget = target;
        Placement = PlacementMode.MousePoint;
        IsOpen = true;
    }

    static SolidColorBrush Brush(int rgb)
    {
        var value = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        value.Freeze();
        return value;
    }
}
