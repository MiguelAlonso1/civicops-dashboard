namespace CivicOps.Web.Components;

public static class AppInfo
{
    public static readonly string Version =
        (typeof(AppInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "1.0.0")
        .Split('+')[0];
}
