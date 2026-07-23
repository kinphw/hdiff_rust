using Hdiff.UI.Worker;

namespace Hdiff.UI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--worker", StringComparer.OrdinalIgnoreCase))
        {
            HwpWorkerEntry.Run();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

