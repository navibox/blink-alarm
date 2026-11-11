using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace BlinkAlarm.App.Controls;

/// <summary>"− 数值 +" 步进器。按钮大，便于用眼控仪或触摸操作。</summary>
public partial class Stepper : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Stepper),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnChanged));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(Stepper), new PropertyMetadata(0.0, OnChanged));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Stepper), new PropertyMetadata(100.0, OnChanged));
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(Stepper), new PropertyMetadata(1.0));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(Stepper), new PropertyMetadata("", OnChanged));
    public static readonly DependencyProperty ValueWidthProperty = DependencyProperty.Register(
        nameof(ValueWidth), typeof(double), typeof(Stepper), new PropertyMetadata(58.0));
    public static readonly DependencyProperty DisplayProperty = DependencyProperty.Register(
        nameof(Display), typeof(string), typeof(Stepper), new PropertyMetadata("0"));

    public Stepper()
    {
        InitializeComponent();
        Refresh();
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    /// <summary>显示在数值后面的单位，例如"秒"。</summary>
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public double ValueWidth { get => (double)GetValue(ValueWidthProperty); set => SetValue(ValueWidthProperty, value); }
    public string Display { get => (string)GetValue(DisplayProperty); private set => SetValue(DisplayProperty, value); }

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((Stepper)d).Refresh();

    void Refresh()
    {
        string unit = string.IsNullOrEmpty(Unit) ? "" : " " + Unit;
        Display = Value.ToString("0.##", CultureInfo.InvariantCulture) + unit;
        MinusButton.IsEnabled = Value > Minimum;
        PlusButton.IsEnabled = Value < Maximum;
    }

    void OnMinus(object sender, RoutedEventArgs e) => SetCurrentValue(ValueProperty, Math.Max(Minimum, Value - Step));
    void OnPlus(object sender, RoutedEventArgs e) => SetCurrentValue(ValueProperty, Math.Min(Maximum, Value + Step));
}
