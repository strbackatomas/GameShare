namespace GameShare.AdminGui.ViewModels;

/// <summary>What an .msi says about itself, read through Windows Installer: the name Apps and Features shows, and the version.</summary>
public sealed record MsiInfo(string? ProductName, string? ProductVersion)
{
    /// <summary>Null when the file cannot be read, or not on Windows. Only a suggestion for the form, so nothing here is fatal.</summary>
    public static MsiInfo? Read(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;
        try
        {
            dynamic installer = Activator.CreateInstance(Type.GetTypeFromProgID("WindowsInstaller.Installer", throwOnError: true)!)!;
            dynamic database = installer.OpenDatabase(path, 0); // 0: read only

            string? Property(string name)
            {
                dynamic view = database.OpenView($"SELECT `Value` FROM `Property` WHERE `Property`='{name}'");
                view.Execute();
                dynamic? record = view.Fetch();
                return record is null ? null : (string)record.StringData[1];
            }

            return new MsiInfo(Property("ProductName"), Property("ProductVersion"));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                       or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }
}
