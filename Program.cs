namespace RustDeskList;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is [RustDeskServicePause.HelperArgument, var token])
        {
            RustDeskServicePause.HoldServiceAsync(token).GetAwaiter().GetResult();
            return;
        }

        ApplicationConfiguration.Initialize();

        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = new RustDeskProfile(
            Path.Combine(roaming, "RustDesk", "config"),
            Path.Combine(local, "RustDeskList"));

        using Stream iconResource = typeof(Program).Assembly.GetManifestResourceStream("RustDeskList.AppIcon")
            ?? throw new InvalidOperationException("Ресурс значка RustDeskList отсутствует.");
        using var icon = new Icon(iconResource);
        using var window = new MainForm(profile) { Icon = icon };
        Application.Run(window);
    }
}
