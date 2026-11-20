using PersonalLauncher.Infrastructure.Windows;

namespace PersonalLauncher.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var singleInstance = new SingleInstanceManager("PersonalLauncher");
        if (!singleInstance.IsPrimaryInstance)
        {
            // Bring existing launcher window to foreground and exit cleanly
            singleInstance.NotifyPrimaryInstance();
            return 0;
        }

        bool startInBackground = args.Any(a =>
            string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase));

        var app = new App(singleInstance, startInBackground);
        return app.Run();
    }
}
