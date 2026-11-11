using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MGRScreen.App;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(Path.GetTempPath(), "mgrscreen_crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Sem isso, uma exceção não tratada num "async void" (ex: handler de clique de botão)
        // derruba o processo inteiro sem deixar rastro — foi o que causava o crash ao clicar em
        // "Escolher tela". Agora ela é logada e mostrada, e o app continua rodando.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash(args.ExceptionObject as Exception, "AppDomain.UnhandledException");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash(args.Exception, "TaskScheduler.UnobservedTaskException");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception, "DispatcherUnhandledException");
        MessageBox.Show(
            $"Ocorreu um erro:\n\n{e.Exception.Message}\n\nDetalhes em:\n{CrashLogPath}",
            "MGRScreen - Erro",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true; // não deixa o app crashar
    }

    private static void LogCrash(Exception? ex, string source)
    {
        try
        {
            File.AppendAllText(CrashLogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}]\n{ex}\n\n");
        }
        catch { }
    }
}
