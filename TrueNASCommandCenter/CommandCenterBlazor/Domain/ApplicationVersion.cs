using System.Reflection;

namespace TrueNasCommandCenter.Domain;

public static class ApplicationVersion
{
    public static string Current { get; } = ResolveCurrent();

    public static string Display => $"v{Current}";

    private static string ResolveCurrent()
    {
        var assembly = typeof(ApplicationVersion).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+', 2)[0];
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}
