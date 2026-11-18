using System.Reflection;

namespace GitStronghold.Core;

/// <summary>
/// The version is the Version property in Directory.Build.props.
/// </summary>
public static class AppVersion
{
    public static string Current { get; } = Read();

    public static string Product { get; } = Attribute<AssemblyProductAttribute>(attribute => attribute.Product);

    public static string Company { get; } = Attribute<AssemblyCompanyAttribute>(attribute => attribute.Company);

    public static string Copyright { get; } = Attribute<AssemblyCopyrightAttribute>(attribute => attribute.Copyright);

    public static string Credit => "by " + Company;

    public static string ProductVersion => Product + " " + Current;

    private static string Read()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static string Attribute<T>(Func<T, string> read) where T : Attribute
    {
        var attribute = typeof(AppVersion).Assembly.GetCustomAttribute<T>();
        return attribute is null ? "" : read(attribute);
    }
}
