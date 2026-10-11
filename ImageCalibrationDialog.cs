using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistOSets.Core;
using HistOSets.Services;

namespace HistOSets;

/// <summary>Edits calibration locally; the parent owns the working copy and catalog save.</summary>
internal sealed class ImageCalibrationDialog : Window
{
    private readonly int pixelWidth;
    private readonly int pixelHeight;
    private readonly TextBox widthInput;
    private readonly TextBox heightInput;
    private readonly TextBox sourceInput;
    private readonly ComboBox unitList;
    private readonly TextBlock validationStatus;
    private sealed record UnitChoice(string Code, string Label);
    private static string T(string ru, string en) => UiLanguage.T(ru, en);

    public ImageCalibration? Calibration { get; private set; }

    public ImageCalibrationDialog(int pixelWidth, int pixelHeight, ImageCalibration? calibration)
    {
        this.pixelWidth = pixelWidth;
        this.pixelHeight = pixelHeight;
        Calibration = calibration;
        NameScope.SetNameScope(this, new NameScope());
        Title = T("Калибровка текущего изображения", "Current image calibration");
        Width = 570;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(244, 241, 248));
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = T($"Оригинал: {pixelWidth} × {pixelHeight} px. Введите известные физические размеры всего изображения — отдельно по ширине и высоте.",
                $"Original: {pixelWidth} × {pixelHeight} px. Enter known physical dimensions of the entire image, separately for width and height."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        widthInput = AddInput("CalibrationWidthInput", T("Физическая ширина изображения", "Physical image width"));
        heightInput = AddInput("CalibrationHeightInput", T("Физическая высота изображения", "Physical image height"));
        panel.Children.Add(new TextBlock { Text = T("Единица для обоих размеров", "Unit for both dimensions"), Margin = new Thickness(0, 0, 0, 4) });
        var units = new[] { "m", "cm", "mm", "um", "nm" }.Select(code => new UnitChoice(code, UnitLabel(code))).ToArray();
        unitList = new ComboBox
        {
            Name = "CalibrationUnitList", ItemsSource = units, DisplayMemberPath = "Label", SelectedValuePath = "Code",
            Margin = new Thickness(0, 0, 0, 12), MinHeight = 30
        };
        RegisterName(unitList.Name, unitList);
        System.Windows.Automation.AutomationProperties.SetName(unitList, T("Единица физических размеров", "Physical dimension unit"));
        panel.Children.Add(unitList);
        sourceInput = AddInput("CalibrationSourceNoteInput", T("Основание / источник размеров (необязательно)", "Dimension source / reference (optional)"));
        sourceInput.TextWrapping = TextWrapping.Wrap;
        sourceInput.AcceptsReturn = true;
        sourceInput.MinHeight = 55;
        sourceInput.MaxHeight = 95;
        panel.Children.Add(new TextBlock
        {
            Text = T("Размер пикселя рассчитывается отдельно по X и Y. DPI файла и масштаб окна не используются. Калибровка применяется к текущему изображению и сохраняется вместе с объектом; точность зависит от источника размеров и нанесённого контура.",
                "Pixel size is calculated separately for X and Y. File DPI and window zoom are not used. Calibration applies to the current image and is saved with the object; accuracy depends on the dimension source and drawn contour."),
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 12)
        });
        validationStatus = new TextBlock
        {
            Name = "CalibrationValidationStatus", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkRed,
            Margin = new Thickness(0, 0, 0, 10)
        };
        RegisterName(validationStatus.Name, validationStatus);
        panel.Children.Add(validationStatus);
        var actions = new WrapPanel();
        AddButton("ApplyCalibrationButton", T("Применить", "Apply"), () => Apply(), isDefault: true);
        var remove = AddButton("RemoveCalibrationButton", T("Удалить калибровку", "Remove calibration"), () => { Calibration = null; DialogResult = true; });
        remove.IsEnabled = calibration is not null;
        AddButton("CancelCalibrationButton", T("Отмена", "Cancel"), () => DialogResult = false, isCancel: true);
        panel.Children.Add(actions);
        Content = panel;

        var unit = calibration?.InputUnit is "m" or "cm" or "mm" or "nm" ? calibration.InputUnit : "um";
        unitList.SelectedValue = unit;
        if (calibration is not null)
        {
            var metres = MetresPerUnit(unit);
            widthInput.Text = (calibration.InputWidth ?? calibration.PixelSizeXMetres * pixelWidth / metres).ToString("R", DisplayCulture);
            heightInput.Text = (calibration.InputHeight ?? calibration.PixelSizeYMetres * pixelHeight / metres).ToString("R", DisplayCulture);
            sourceInput.Text = calibration.SourceNote;
        }
        Loaded += (_, _) => { widthInput.Focus(); widthInput.SelectAll(); };

        TextBox AddInput(string name, string label)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) });
            var input = new TextBox { Name = name, Margin = new Thickness(0, 0, 0, 12) };
            System.Windows.Automation.AutomationProperties.SetName(input, label);
            RegisterName(name, input);
            panel.Children.Add(input);
            return input;
        }

        Button AddButton(string name, string label, Action action, bool isDefault = false, bool isCancel = false)
        {
            var button = new Button { Name = name, Content = label, IsDefault = isDefault, IsCancel = isCancel };
            button.Click += (_, _) => action();
            RegisterName(name, button);
            actions.Children.Add(button);
            return button;
        }
    }

    private static CultureInfo DisplayCulture => CultureInfo.GetCultureInfo(UiLanguage.Locale == "ru" ? "ru-RU" : "en-US");
    private static double MetresPerUnit(string unit) => unit switch { "m" => 1, "cm" => 1e-2, "mm" => 1e-3, "nm" => 1e-9, _ => 1e-6 };

    internal static string UnitLabel(string unit) => unit switch
    {
        "m" => T("м", "m"), "cm" => T("см", "cm"), "mm" => T("мм", "mm"), "nm" => T("нм", "nm"), _ => T("мкм", "µm")
    };

    internal static bool TryPositiveNumber(string text, out double value)
        => double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value > 0;

    private void Apply()
    {
        if (!TryPositiveNumber(widthInput.Text, out var width) || !TryPositiveNumber(heightInput.Text, out var height))
        {
            validationStatus.Text = T("Ширина и высота должны быть конечными положительными числами. Допустимы десятичная точка или запятая.",
                "Width and height must be finite positive numbers. A decimal point or comma is accepted.");
            return;
        }
        if (unitList.SelectedValue is not string unit)
        {
            validationStatus.Text = T("Выберите единицу физических размеров.", "Select the physical dimension unit.");
            return;
        }
        try
        {
            Calibration = ImageCalibration.FromDimensions(width, height, unit, pixelWidth, pixelHeight) with { SourceNote = sourceInput.Text.Trim() };
            Calibration.Validate();
            DialogResult = true;
        }
        catch (ArgumentException)
        {
            validationStatus.Text = T("Не удалось вычислить допустимый размер пикселя. Проверьте физические размеры и единицу: значения не должны быть слишком малыми или большими.",
                "A valid pixel size could not be calculated. Check dimensions and units: values must not be excessively small or large.");
        }
    }
}
