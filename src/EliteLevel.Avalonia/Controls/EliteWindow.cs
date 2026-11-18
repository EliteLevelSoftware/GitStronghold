using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace EliteLevel.Avalonia.Controls;

/// <summary>
/// Undecorated window. Restored it is a 28px-radius charcoal shell with its own shadow.
/// Maximised, or when the platform has no compositor, the corners go square and the shadow drops.
/// Caption buttons stay Windows-style on every operating system. There is no backdrop blur.
/// </summary>
public class EliteWindow : Window
{
    public static readonly DirectProperty<EliteWindow, double> ChromeMarginProperty =
        AvaloniaProperty.RegisterDirect<EliteWindow, double>(nameof(ChromeMargin), window => window.ChromeMargin);

    public static readonly DirectProperty<EliteWindow, CornerRadius> ChromeRadiusProperty =
        AvaloniaProperty.RegisterDirect<EliteWindow, CornerRadius>(nameof(ChromeRadius), window => window.ChromeRadius);

    public static readonly DirectProperty<EliteWindow, BoxShadows> ChromeShadowProperty =
        AvaloniaProperty.RegisterDirect<EliteWindow, BoxShadows>(nameof(ChromeShadow), window => window.ChromeShadow);

    private double _chromeMargin;
    private CornerRadius _chromeRadius;
    private BoxShadows _chromeShadow;

    public EliteWindow()
    {
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        ExtendClientAreaToDecorationsHint = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    public double ChromeMargin
    {
        get => _chromeMargin;
        private set => SetAndRaise(ChromeMarginProperty, ref _chromeMargin, value);
    }

    public CornerRadius ChromeRadius
    {
        get => _chromeRadius;
        private set => SetAndRaise(ChromeRadiusProperty, ref _chromeRadius, value);
    }

    public BoxShadows ChromeShadow
    {
        get => _chromeShadow;
        private set => SetAndRaise(ChromeShadowProperty, ref _chromeShadow, value);
    }

    public void DragMove(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed == false)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        BeginMoveDrag(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdateChrome();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == ActualTransparencyLevelProperty)
        {
            UpdateChrome();
        }
    }

    private void UpdateChrome()
    {
        var flat = WindowState is WindowState.Maximized or WindowState.FullScreen
            || ActualTransparencyLevel == WindowTransparencyLevel.None;
        if (flat)
        {
            ChromeMargin = 0;
            ChromeRadius = new CornerRadius(0);
            ChromeShadow = default;
            return;
        }

        ChromeMargin = Resource<double>("ElSizeChrome");
        ChromeRadius = Resource<CornerRadius>("ElRadiusWindow");
        ChromeShadow = Resource<BoxShadows>("ElShadowWindow");
    }

    private T Resource<T>(string key) => (T)(this.FindResource(key) ?? throw new InvalidOperationException("Missing theme token " + key));
}
