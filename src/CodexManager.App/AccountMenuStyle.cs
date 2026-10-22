using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace CodexManager;

internal static class AccountMenuStyle
{
    // Native MenuItem behavior (keyboard, accessibility, click) with no legacy icon/check gutter.
    [ThreadStatic] private static ResourceDictionary? _styles;
    private static ResourceDictionary Styles => _styles ??= (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <ControlTemplate x:Key="MenuShell" TargetType="ContextMenu">
            <Border Background="{TemplateBinding Background}" BorderBrush="{DynamicResource Line}" BorderThickness="1" CornerRadius="10" Padding="4">
              <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle"/>
            </Border>
          </ControlTemplate>
          <Style x:Key="MenuRow" TargetType="MenuItem">
            <Setter Property="Foreground" Value="{DynamicResource Text}"/>
            <Setter Property="FontFamily" Value="Segoe UI"/>
            <Setter Property="FontSize" Value="12"/>
            <Setter Property="Padding" Value="11,7"/>
            <Setter Property="MinWidth" Value="154"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="MenuItem">
                  <Border x:Name="Item" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="6" Padding="{TemplateBinding Padding}">
                    <ContentPresenter ContentSource="Header" RecognizesAccessKey="True"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Item" Property="Background" Value="{DynamicResource Hover}"/></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Item" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
        </ResourceDictionary>
        """);
    public static void Apply(ContextMenu menu)
    {
        menu.Template = (ControlTemplate)Styles["MenuShell"];
        menu.ItemContainerStyle = (Style)Styles["MenuRow"];
        menu.HasDropShadow = true;
    }
}
