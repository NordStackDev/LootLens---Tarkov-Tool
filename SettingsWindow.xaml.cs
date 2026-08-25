using System;
using System.Windows;
using System.Windows.Input;

namespace Lootlens;

public partial class SettingsWindow : Window {
    public SettingsWindow(Settings settings) {
        InitializeComponent();

        EditableSettings = settings.Clone();

        InventoryValueEnabledCheckBox.IsChecked = EditableSettings.InventoryValueEnabled;
        ShowFleaPriceCheckBox.IsChecked = EditableSettings.ShowFleaPrice;
        ShowTraderPriceCheckBox.IsChecked = EditableSettings.ShowTraderPrice;
        ShowProfitCheckBox.IsChecked = EditableSettings.ShowProfit;
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
        EditableSettings.InventoryValueEnabled = InventoryValueEnabledCheckBox.IsChecked == true;
        EditableSettings.ShowFleaPrice = ShowFleaPriceCheckBox.IsChecked == true;
        EditableSettings.ShowTraderPrice = ShowTraderPriceCheckBox.IsChecked == true;
        EditableSettings.ShowProfit = ShowProfitCheckBox.IsChecked == true;
        EditableSettings.Normalize();

        DialogResult = true;
        Close();
    }
}
