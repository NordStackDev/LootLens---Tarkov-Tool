using System.Configuration;
using System.Data;
using System.Windows;

namespace Lootlens {
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application {
        protected override void OnStartup(StartupEventArgs e) {
            base.OnStartup(e);

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;

            // Force handle/Loaded creation (needed for hotkeys) without showing the panel until F6.
            mainWindow.Show();
            mainWindow.Hide();
        }
    }

}
