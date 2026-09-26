using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace Godspeed.Bootstrap;

// This file is compiled into each plugin. It must not reference Godspeed.Shared:
// the host discovers plugin classes before the shared assembly can be installed.
internal static class GodspeedDependencyBootstrap
{
    private const string SharedFileName = "Godspeed.Shared.dll";
    private const string DeploymentMutexName = @"Local\GodspeedSharedDeploymentMutex";

    internal static bool Deploy(Assembly pluginAssembly)
    {
        using var mutex = new Mutex(false, DeploymentMutexName);
        bool acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
                throw new TimeoutException("Timed out waiting for Godspeed shared dependency deployment.");

            using Stream resource = pluginAssembly.GetManifestResourceStream(SharedFileName)
                ?? throw new FileNotFoundException("Embedded Godspeed shared dependency is missing.");
            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            byte[] embedded = buffer.ToArray();
            byte[] expectedHash = SHA256.HashData(embedded);
            string target = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "Libraries", SharedFileName);
            bool installedMatches = File.Exists(target)
                && SHA256.HashData(File.ReadAllBytes(target)).AsSpan().SequenceEqual(expectedHash);

            if (!installedMatches)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(temporary, embedded);
                    File.Move(temporary, target, true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                // ETS2LA loaded libraries before plugins. Never use the old
                // in-memory assembly in the same process after an update.
                return false;
            }

            Assembly? loaded = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(
                assembly => string.Equals(assembly.GetName().Name, "Godspeed.Shared",
                    StringComparison.Ordinal));
            if (loaded is null || string.IsNullOrEmpty(loaded.Location)
                || !File.Exists(loaded.Location))
                return false;
            return SHA256.HashData(File.ReadAllBytes(loaded.Location))
                .AsSpan().SequenceEqual(expectedHash);
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }
}
