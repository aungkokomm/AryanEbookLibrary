using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AryanEbookLibrary.Reader;

/// <summary>
/// The readers' zoom: one toolbar button that says how the page is shown ("Fit width", "150%"), and under it a panel
/// with a slider between minus and plus and buttons back to the fitted sizes. Each reader says what the numbers are:
/// the PDF reader's percent of the page's own size, a comic's zoom past its fitted page, a book's text size. A
/// DropDownButton, so the toolbar gives the keyboard back to the page when the panel closes, as it does for its menus.
/// </summary>
public sealed class ZoomButton : DropDownButton
{
    private readonly TextBlock _label = new() { Text = "Fit width" };
    private readonly Slider _slider = new() { VerticalAlignment = VerticalAlignment.Center, IsThumbToolTipEnabled = false };
    private readonly TextBlock _value = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button _minus = PanelButton(0xE738);
    private readonly Button _plus = PanelButton(0xE710);
    private readonly StackPanel _fits = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly DispatcherQueueTimer _settle;
    private bool _showing;

    /// <summary>The slider was moved and has come to rest: the value it rests at.</summary>
    public event Action<double>? SliderMoved;
    /// <summary>Minus (-1) or plus (+1).</summary>
    public event Action<int>? Stepped;
    /// <summary>One of the fitted sizes, by the key it was given.</summary>
    public event Action<string>? FitClicked;

    public ZoomButton()
    {
        Height = 32;
        MinWidth = 112;
        Padding = new Thickness(8, 0, 8, 0);
        AllowFocusOnInteraction = false;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = ((char)0xE71E).ToString(), FontSize = 14 });
        content.Children.Add(_label);
        Content = content;

        _minus.Click += (_, _) => Stepped?.Invoke(-1);
        _plus.Click += (_, _) => Stepped?.Invoke(+1);
        // A drag sends a stream of values; the reader redraws once it stops.
        _settle = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _settle.Interval = TimeSpan.FromMilliseconds(150);
        _settle.IsRepeating = false;
        _settle.Tick += (_, _) => SliderMoved?.Invoke(_slider.Value);
        _slider.ValueChanged += (_, e) =>
        {
            _value.Text = $"{e.NewValue:0}%";
            if (_showing) return;
            _settle.Stop();
            _settle.Start();
        };

        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_slider, 1);
        Grid.SetColumn(_plus, 2);
        row.Children.Add(_minus);
        row.Children.Add(_slider);
        row.Children.Add(_plus);

        var bottom = new Grid();
        bottom.Children.Add(_fits);
        bottom.Children.Add(_value);

        var panel = new StackPanel { Spacing = 12, Width = 270 };
        panel.Children.Add(row);
        panel.Children.Add(bottom);
        Flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight, Content = panel };
    }

    private static Button PanelButton(int glyph) => new()
    {
        Width = 36,
        Height = 32,
        Padding = new Thickness(0),
        Content = new FontIcon { Glyph = ((char)glyph).ToString(), FontSize = 12 },
    };

    /// <summary>
    /// What the numbers are: the slider's range and step, the names of minus and plus (read aloud, and in their tips
    /// with their keys), and the fitted sizes offered, each a key and its label.
    /// </summary>
    public void Configure(double minimum, double maximum, double step, string smaller, string larger,
        params (string Key, string Text)[] fits)
    {
        // Setting the range moves the value (0 becomes the minimum). That is not the user's doing: as a move it started
        // the settle timer, which fired after a fast-opening book had shown its fit and made it a custom zoom, so the
        // page stopped refitting to the window (measured: 150 ms after the reader opened).
        _showing = true;
        try
        {
            _slider.Minimum = minimum;
            _slider.Maximum = maximum;
        }
        finally
        {
            _showing = false;
        }
        _slider.StepFrequency = _slider.SmallChange = step;
        _slider.LargeChange = step * 5;
        AutomationProperties.SetName(_slider, AutomationProperties.GetName(this));
        Describe(_minus, smaller, "Ctrl+minus");
        Describe(_plus, larger, "Ctrl+plus");
        _fits.Children.Clear();
        foreach (var (key, text) in fits)
        {
            var button = new Button { Content = text, Height = 32 };
            button.Click += (_, _) => FitClicked?.Invoke(key);
            _fits.Children.Add(button);
        }
    }

    private static void Describe(Button button, string name, string keys)
    {
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, $"{name} ({keys})");
    }

    /// <summary>How the page is shown now, on the button and on the slider (which then says nothing back).</summary>
    public void Show(string label, double value)
    {
        _label.Text = label;
        _showing = true;
        try
        {
            _slider.Value = Math.Clamp(value, _slider.Minimum, _slider.Maximum);
            _value.Text = $"{value:0}%";
        }
        finally
        {
            _showing = false;
        }
    }
}
