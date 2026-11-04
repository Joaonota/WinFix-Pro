using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace WinFixPro;

public partial class App : Application
{
    private const string ElevatedRestartArgument = "--elevated-restart";

    private void OnStartup(object sender, StartupEventArgs e)
    {
        var elevated = IsAdministrator();
        var elevationCancelled = false;
        if (!elevated && !e.Args.Contains(ElevatedRestartArgument, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppContext.BaseDirectory
                };
                start.ArgumentList.Add(ElevatedRestartArgument);
                Process.Start(start);
                Shutdown();
                return;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                elevationCancelled = true;
            }
            catch (Win32Exception ex)
            {
                MessageBox.Show($"Não foi possível solicitar elevação UAC. A aplicação continuará em modo limitado.\n\n{ex.Message}",
                    "WinFix Pro - elevação indisponível", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        var main = new MainWindow(elevated || IsAdministrator(), elevationCancelled);
        MainWindow = main;
        main.Show();
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
