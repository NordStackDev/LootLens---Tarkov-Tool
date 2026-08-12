using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace LootLens;

public partial class SettingsWindow : Window {
    public SettingsWindow(Settings settings) {
        InitializeComponent();

        EditableSettings = settings.Clone();

        InventoryValueEnabledCheckBox.IsChecked = EditableSettings.InventoryValueEnabled;
        ShowFleaPriceCheckBox.IsChecked = EditableSettings.ShowFleaPrice;
        ShowTraderPriceCheckBox.IsChecked = EditableSettings.ShowTraderPrice;
        ShowProfitCheckBox.IsChecked = EditableSettings.ShowProfit;
        RegionWidthBox.Text = EditableSettings.InventoryRegionWidth.ToString(CultureInfo.InvariantCulture);
        RegionHeightBox.Text = EditableSettings.InventoryRegionHeight.ToString(CultureInfo.InvariantCulture);
    }

    public Settings EditableSettings { get; }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) {
        DialogResult = false;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) {
        DialogResult = false;
        Close();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) {
        if (!int.TryParse(RegionWidthBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(RegionHeightBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)) {
            System.Windows.MessageBox.Show(this,
                "OCR region width/height must be valid numbers.",
                "Invalid settings",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        EditableSettings.InventoryValueEnabled = InventoryValueEnabledCheckBox.IsChecked == true;
        EditableSettings.ShowFleaPrice = ShowFleaPriceCheckBox.IsChecked == true;
        EditableSettings.ShowTraderPrice = ShowTraderPriceCheckBox.IsChecked == true;
        EditableSettings.ShowProfit = ShowProfitCheckBox.IsChecked == true;
        EditableSettings.InventoryRegionWidth = width;
        EditableSettings.InventoryRegionHeight = height;
        EditableSettings.Normalize();

        DialogResult = true;
        Close();
    }
}
