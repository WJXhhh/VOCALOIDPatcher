namespace VOCALOIDPatcher.ConsonantTiming;

internal static class AcousticControlStyle
{
    internal const string ComboStyle = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ComboBox">
          <Setter Property="Foreground" Value="#D6E0EA"/><Setter Property="Background" Value="#14191F"/>
          <Setter Property="BorderBrush" Value="#46515D"/><Setter Property="Padding" Value="5,3"/>
          <Setter Property="ItemContainerStyle"><Setter.Value><Style TargetType="ComboBoxItem">
            <Setter Property="Foreground" Value="#D6E0EA"/><Setter Property="Padding" Value="7,5"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem">
              <Border x:Name="Surface" Background="#1B2026" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border>
              <ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Surface" Property="Background" Value="#203F54"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style></Setter.Value></Setter>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
            <Grid><ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
              <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border x:Name="Frame" Background="#14191F" BorderBrush="#46515D" BorderThickness="1" CornerRadius="4"><Path Data="M0,0 L4,4 L8,0" Stroke="#A3AFBC" StrokeThickness="1" HorizontalAlignment="Right" Margin="0,0,7,0" VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="#437D9F"/></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template>
            </ToggleButton><ContentPresenter IsHitTestVisible="False" Margin="6,4,22,4" Content="{TemplateBinding SelectionBoxItem}" VerticalAlignment="Center"/>
            <Popup x:Name="PART_Popup" Placement="Bottom" IsOpen="{TemplateBinding IsDropDownOpen}" AllowsTransparency="True" Focusable="False">
              <Border Background="#1B2026" BorderBrush="#46515D" BorderThickness="1" CornerRadius="4" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"><ScrollViewer MaxHeight="240"><ItemsPresenter/></ScrollViewer></Border>
            </Popup></Grid>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        """;
    internal const string SegmentStyle = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="RadioButton">
          <Setter Property="Foreground" Value="#ACB8C6"/>
          <Setter Property="Template"><Setter.Value>
            <ControlTemplate TargetType="RadioButton">
              <Border x:Name="Surface" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="4" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="#303C48"/><Setter Property="Foreground" Value="#E3EBF4"/></Trigger>
                <Trigger Property="IsChecked" Value="True"><Setter TargetName="Surface" Property="Background" Value="#203F54"/><Setter TargetName="Surface" Property="BorderBrush" Value="#437D9F"/><Setter Property="Foreground" Value="#D6EFFF"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="Surface" Property="Background" Value="#2B526C"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Surface" Property="BorderBrush" Value="#85CFFF"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value></Setter>
        </Style>
        """;

    internal const string ActionStyle = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Setter Property="Foreground" Value="#B7C7D6"/>
          <Setter Property="Template"><Setter.Value>
            <ControlTemplate TargetType="Button">
              <Border x:Name="Surface" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="4" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="#303C48"/><Setter Property="Foreground" Value="#E3EBF4"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="Surface" Property="Background" Value="#2B526C"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Surface" Property="BorderBrush" Value="#85CFFF"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value></Setter>
        </Style>
        """;
}
