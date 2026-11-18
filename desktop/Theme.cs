using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
namespace PackagePicker;
internal static class Theme {
    static SolidColorBrush Brush(string hex){var b=(SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;b.Freeze();return b;}
    static readonly ControlTemplate Surface=(ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
 <Border x:Name="surface" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="6" Padding="{TemplateBinding Padding}">
  <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
 </Border>
 <ControlTemplate.Triggers>
  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="surface" Property="BorderBrush" Value="#758A79"/></Trigger>
  <Trigger Property="IsPressed" Value="True"><Setter TargetName="surface" Property="Opacity" Value="0.76"/></Trigger>
  <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="surface" Property="BorderBrush" Value="#316DAB"/><Setter TargetName="surface" Property="BorderThickness" Value="2"/></Trigger>
  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="surface" Property="Opacity" Value="0.52"/></Trigger>
 </ControlTemplate.Triggers>
</ControlTemplate>
""");
    internal static void Apply(Window w){w.FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI");w.FontSize=14;w.Background=Brush("#FAFAF8");w.Foreground=Brush("#252A26");w.UseLayoutRounding=true;var s=new Style(typeof(Button));s.Setters.Add(new Setter(Control.TemplateProperty,Surface));s.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.White));s.Setters.Add(new Setter(Control.BorderBrushProperty,Brush("#CDD2CA")));s.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(12,5,12,5)));s.Setters.Add(new Setter(FrameworkElement.MinHeightProperty,32d));w.Resources[typeof(Button)]=s;}
    internal static void Primary(Button b){b.Background=Brush("#263F32");b.Foreground=Brushes.White;b.BorderBrush=b.Background;b.FontWeight=FontWeights.SemiBold;b.Padding=new Thickness(16,6,16,6);}
    internal static void Quiet(Button b){b.Background=Brushes.Transparent;b.BorderBrush=Brushes.Transparent;b.Foreground=Brush("#40594A");b.Padding=new Thickness(8,4,8,4);}
}
