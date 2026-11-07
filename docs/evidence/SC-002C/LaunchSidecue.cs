using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

// Launched through the ordinary interactive user session; inherits that token.
internal static class LaunchSidecue
{
    [STAThread]
    private static int Main()
    {
        try
        {
            string root = Directory.GetParent(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).FullName).FullName;
            string dotnet = Path.Combine(root, "artifacts", "dotnet");
            string executable = Path.Combine(root, "src", "Sidecue.Desktop", "bin", "Release", "net10.0-windows10.0.26100.0", "Sidecue.Desktop.exe");
            if (!File.Exists(Path.Combine(dotnet, "dotnet.exe")) || !File.Exists(executable))
                throw new FileNotFoundException("Build Sidecue and ensure the local SDK exists before launching.");
            var start = new ProcessStartInfo(executable);
            start.UseShellExecute = false;
            start.WorkingDirectory = root;
            start.EnvironmentVariables["DOTNET_ROOT"] = dotnet;
            start.EnvironmentVariables["DOTNET_ROOT_X64"] = dotnet;
            Process.Start(start);
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Sidecue local test launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
