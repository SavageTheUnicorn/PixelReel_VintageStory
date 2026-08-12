using System;
using System.IO;
using System.Reflection;

namespace PixelReel
{
    /// <summary>
    /// Safety net for bundled dependency DLLs (LibVLCSharp).
    ///
    /// Vintage Story loads mod assemblies out of a zip archive, so .NET's normal
    /// "look next to the calling assembly" resolution may not find a sibling DLL
    /// inside that archive. This hooks AssemblyResolve at module load time and
    /// searches the Mods folders on disk as a fallback.
    ///
    /// If VS resolves the dependency on its own this code never fires, which is
    /// the intended outcome. It exists so a resolution failure isn't a hard stop.
    /// </summary>
    internal static class DependencyResolver
    {
        private static bool hooked;

        /// <summary>
        /// Called from the mod system's StartPre. Previously a [ModuleInitializer],
        /// which fires earlier but trips CA2255 in library assemblies; StartPre is
        /// early enough since nothing touches LibVLCSharp types before it.
        /// </summary>
        internal static void Init()
        {
            if (hooked) return;
            hooked = true;

            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            }
            catch
            {
                // Never let this take the game down. Worst case, VS's own resolution wins or fails loudly.
            }
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string wanted = new AssemblyName(args.Name).Name;
                if (wanted == null) return null;

                // Only ever resolve our own bundled dependency. Don't become a global hook.
                if (!wanted.Equals("LibVLCSharp", StringComparison.OrdinalIgnoreCase)) return null;

                foreach (string dir in CandidateDirectories())
                {
                    if (dir == null || !Directory.Exists(dir)) continue;

                    string direct = Path.Combine(dir, wanted + ".dll");
                    if (File.Exists(direct)) return Assembly.LoadFrom(direct);

                    // Also look one level down, in case the dll landed in an extracted subfolder.
                    foreach (string sub in Directory.GetDirectories(dir))
                    {
                        string nested = Path.Combine(sub, wanted + ".dll");
                        if (File.Exists(nested)) return Assembly.LoadFrom(nested);
                    }
                }
            }
            catch
            {
                // Fall through to null: let the runtime report the original failure.
            }

            return null;
        }

        private static string[] CandidateDirectories()
        {
            string self = null;
            try
            {
                string loc = typeof(DependencyResolver).Assembly.Location;
                if (!string.IsNullOrEmpty(loc)) self = Path.GetDirectoryName(loc);
            }
            catch { }

            string dataPath = Environment.GetEnvironmentVariable("VINTAGE_STORY_DATA");
            string appdata = Environment.GetEnvironmentVariable("APPDATA");
            string home = Environment.GetEnvironmentVariable("HOME");

            return new string[]
            {
                self,
                AppContext.BaseDirectory,
                dataPath == null ? null : Path.Combine(dataPath, "Mods"),
                appdata == null ? null : Path.Combine(appdata, "VintagestoryData", "Mods"),
                home == null ? null : Path.Combine(home, ".config", "VintagestoryData", "Mods"),
                home == null ? null : Path.Combine(home, ".local", "share", "VintagestoryData", "Mods")
            };
        }
    }
}
