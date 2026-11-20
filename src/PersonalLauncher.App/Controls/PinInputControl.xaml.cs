using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PersonalLauncher.Core.Services;

namespace PersonalLauncher.App.Controls;

public partial class PinInputControl : UserControl
{
    private static readonly SolidColorBrush IdleBorderBrush = new(Color.FromRgb(0x49, 0x52, 0x66));
    private static readonly SolidColorBrush ActiveBorderBrush = new(Color.FromRgb(0xFF, 0xD8, 0x00));
    private static readonly SolidColorBrush FilledBorderBrush = new(Color.FromRgb(0x7B, 0x87, 0xA3));

    private readonly Border[] _boxes;
    private readonly TextBlock[] _dots;
    private string _digits = string.Empty;
    private bool _submitDispatched;

    public static readonly DependencyProperty AutoSubmitProperty = DependencyProperty.Register(
        nameof(AutoSubmit), typeof(bool), typeof(PinInputControl), new PropertyMetadata(true));

    public static readonly DependencyProperty PinValueProperty = DependencyProperty.Register(
        nameof(PinValue), typeof(string), typeof(PinInputControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPinValueChanged));

    public bool AutoSubmit
    {
        get => (bool)GetValue(AutoSubmitProperty);
        set => SetValue(AutoSubmitProperty, value);
    }

    public string PinValue
    {
        get => (string)GetValue(PinValueProperty);
        set => SetValue(PinValueProperty, value);
    }

    public event EventHandler<string>? PinCompleted;
    public event EventHandler? InvalidInputAttempted;

    public PinInputControl()
    {
        InitializeComponent();
        _boxes = new[] { Box0, Box1, Box2, Box3 };
        _dots = new[] { Dot0, Dot1, Dot2, Dot3 };

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewTextInput += OnPreviewTextInput;
        MouseLeftButtonDown += (_, _) => Focus();
        GotKeyboardFocus += (_, _) => UpdateVisuals();
        LostKeyboardFocus += (_, _) => UpdateVisuals();
        UpdateVisuals();
    }

    public void Clear()
    {
        _digits = string.Empty;
        _submitDispatched = false;
        SetCurrentValue(PinValueProperty, string.Empty);
        UpdateVisuals();
    }

    public void FocusInput()
    {
        Focus();
        Keyboard.Focus(this);
        UpdateVisuals();
    }

    private static void OnPinValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PinInputControl ctrl)
        {
            string next = e.NewValue as string ?? string.Empty;
            if (!string.Equals(ctrl._digits, next, StringComparison.Ordinal))
            {
                ctrl._digits = next.Length <= 4 ? next : next[..4];
                if (ctrl._digits.Length < 4)
                    ctrl._submitDispatched = false;
                ctrl.UpdateVisuals();
            }
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsEnabled)
            return;

        // Support Ctrl+V paste of 4 digits
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            TryPasteFromClipboard();
            return;
        }

        if (e.Key == Key.Back)
        {
            e.Handled = true;
            if (_digits.Length > 0)
            {
                _digits = _digits[..^1];
                _submitDispatched = false;
                SetCurrentValue(PinValueProperty, _digits);
                UpdateVisuals();
            }
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Clear();
            return;
        }
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = true;
        if (!IsEnabled || string.IsNullOrEmpty(e.Text))
            return;

        foreach (char ch in e.Text)
        {
            if (ch < '0' || ch > '9')
            {
                InvalidInputAttempted?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (_digits.Length < PinValidation.RequiredLength)
            {
                _digits += ch;
            }
        }

        SetCurrentValue(PinValueProperty, _digits);
        UpdateVisuals();
        CheckAutoSubmit();
    }

    private void TryPasteFromClipboard()
    {
        if (!Clipboard.ContainsText())
            return;

        string text = Clipboard.GetText();
        if (PinValidation.TryExtractPastedPin(text, out string pasted))
        {
            _digits = pasted;
            _submitDispatched = false;
            SetCurrentValue(PinValueProperty, _digits);
            UpdateVisuals();
            CheckAutoSubmit();
        }
        else
        {
            InvalidInputAttempted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CheckAutoSubmit()
    {
        if (AutoSubmit && _digits.Length == PinValidation.RequiredLength && !_submitDispatched)
        {
            _submitDispatched = true;
            string completedPin = _digits;
            PinCompleted?.Invoke(this, completedPin);
        }
    }

    private void UpdateVisuals()
    {
        bool focused = IsKeyboardFocused;
        for (int i = 0; i < 4; i++)
        {
            bool filled = i < _digits.Length;
            _dots[i].Text = filled ? "●" : string.Empty;

            if (focused && i == Math.Min(_digits.Length, 3))
                _boxes[i].BorderBrush = ActiveBorderBrush;
            else if (filled)
                _boxes[i].BorderBrush = FilledBorderBrush;
            else
                _boxes[i].BorderBrush = IdleBorderBrush;
        }
    }
}
