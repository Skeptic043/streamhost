using System.Reflection;

namespace Spectari.Util;

internal static class AppVersion
{
    internal static string Current =>
        typeof(AppVersion).Assembly
            .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            is [AssemblyInformationalVersionAttribute attribute, ..]
                ? attribute.InformationalVersion
                : "dev";
}
