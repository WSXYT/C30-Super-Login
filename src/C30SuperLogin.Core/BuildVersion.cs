using System.Reflection;
namespace C30SuperLogin.Core;
public static class BuildVersion
{
    public static string Current => Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";
}
