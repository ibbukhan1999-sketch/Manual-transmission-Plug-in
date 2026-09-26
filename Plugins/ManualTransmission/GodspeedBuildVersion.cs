using System.Reflection;

namespace Godspeed.Diagnostics;

internal sealed class GodspeedBuildVersion
{
    private GodspeedBuildVersion(string informationalVersion, string assemblyVersion,
        string fileVersion, string buildUtc)
    {
        InformationalVersion = informationalVersion;
        AssemblyVersion = assemblyVersion;
        FileVersion = fileVersion;
        BuildUtc = buildUtc;
    }

    public string InformationalVersion { get; }
    public string AssemblyVersion { get; }
    public string FileVersion { get; }
    public string BuildUtc { get; }

    public string LogSnapshot =>
        $"informational_version={InformationalVersion};assembly_version={AssemblyVersion};file_version={FileVersion};build_utc={BuildUtc}";

    public static GodspeedBuildVersion Read(Assembly assembly)
    {
        Version? identity = assembly.GetName().Version;
        string assemblyVersion = identity?.ToString() ?? "0.0.0.0";
        string fallbackVersion = identity is null
            ? "0.0.0"
            : $"{identity.Major}.{identity.Minor}.{Math.Max(0, identity.Build)}";
        string informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? fallbackVersion;
        if (string.IsNullOrWhiteSpace(informationalVersion))
            informationalVersion = fallbackVersion;

        string fileVersion = assembly
            .GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? assemblyVersion;
        if (string.IsNullOrWhiteSpace(fileVersion))
            fileVersion = assemblyVersion;

        string buildUtc = "unknown";
        foreach (AssemblyMetadataAttribute metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (metadata.Key == "GodspeedBuildUtc" && !string.IsNullOrWhiteSpace(metadata.Value))
            {
                buildUtc = metadata.Value;
                break;
            }
        }

        return new GodspeedBuildVersion(
            informationalVersion.Trim(), assemblyVersion, fileVersion.Trim(), buildUtc);
    }
}
