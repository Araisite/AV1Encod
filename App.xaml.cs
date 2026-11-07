using System.Windows;

namespace AV1Encod;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show("Erreur inattendue :\n\n" + ex.Exception, "AV1 Encod", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        // On peut glisser des vidéos ou un dossier de série sur l'exe : ils sont ajoutés à la file au démarrage
        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
    }
}
