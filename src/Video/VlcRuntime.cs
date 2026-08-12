using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using PixelReel.Config;
using Vintagestory.API.Client;

namespace PixelReel.Video
{
    /// <summary>
    /// Owns the single process-wide LibVLC instance. Mirrors VlcRuntime.java from
    /// the Fabric mod: locate the system VLC, initialise once, fail soft if absent
    /// so the rest of the mod still works with a "no player" state.
    /// </summary>
    public static class VlcRuntime
    {
        private static LibVLC libVlc;
        private static ICoreClientAPI capi;
        private static bool attempted;

        public static bool Available => libVlc != null;
        public static string FailureReason { get; private set; }
        public static LibVLC Instance => libVlc;

        public static void Initialize(ICoreClientAPI api, PixelReelConfig config)
        {
            if (attempted) return;
            attempted = true;
            capi = api;

            try
            {
                string path = ResolveVlcPath(config);

                if (path != null)
                {
                    string archProblem = DescribeArchMismatch(path);
                    if (archProblem != null)
                    {
                        libVlc = null;
                        FailureReason = archProblem;
                        api.Logger.Warning("[pixelReel] " + archProblem);
                        return;
                    }

                    Core.Initialize(path);
                    api.Logger.Notification("[pixelReel] libvlc loaded from " + path);
                }
                else
                {
                    // Let LibVLCSharp try its own default probing.
                    Core.Initialize();
                    api.Logger.Notification("[pixelReel] libvlc loaded from default search path");
                }

                libVlc = new LibVLC(config.VlcOptions ?? new string[0]);
                api.Logger.Notification("[pixelReel] LibVLC ready, version " + libVlc.Version);
            }
            catch (BadImageFormatException e)
            {
                libVlc = null;
                FailureReason = "libvlc.dll is the wrong architecture (32-bit). Install 64-bit VLC from videolan.org.";
                api.Logger.Warning("[pixelReel] " + FailureReason + " Details: " + e.Message);
            }
            catch (Exception e)
            {
                libVlc = null;
                FailureReason = e.Message;
                api.Logger.Warning("[pixelReel] VLC unavailable, screens will show a placeholder state: " + e);
            }
        }

        public static MediaPlayer NewPlayer()
        {
            if (libVlc == null) return null;
            return new MediaPlayer(libVlc);
        }

        public static void Shutdown()
        {
            try { libVlc?.Dispose(); }
            catch (Exception e) { capi?.Logger.Warning("[pixelReel] error disposing LibVLC: " + e.Message); }
            libVlc = null;
            attempted = false;
        }

        private static string ResolveVlcPath(PixelReelConfig config)
        {
            foreach (string dir in CandidateList(config))
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                if (ContainsLibVlc(dir)) return dir;
            }

            return null;
        }

        private static List<string> CandidateList(PixelReelConfig config)
        {
            List<string> candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(config?.VlcPath)) candidates.Add(config.VlcPath);

            string env = Environment.GetEnvironmentVariable("PIXELREEL_VLC_PATH");
            if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                candidates.Add(@"C:\Program Files\VideoLAN\VLC");
                string pf = Environment.GetEnvironmentVariable("ProgramFiles");
                if (!string.IsNullOrWhiteSpace(pf)) candidates.Add(Path.Combine(pf, "VideoLAN", "VLC"));
                // Listed last: on a 64-bit game this is the wrong bitness, but finding it
                // lets us report that specifically instead of just "not found".
                candidates.Add(@"C:\Program Files (x86)\VideoLAN\VLC");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                candidates.Add("/Applications/VLC.app/Contents/MacOS/lib");
            }
            else
            {
                candidates.Add("/usr/lib/x86_64-linux-gnu");
                candidates.Add("/usr/lib64");
                candidates.Add("/usr/lib");
                candidates.Add("/var/lib/flatpak/app/org.videolan.VLC/current/active/files/lib");
            }

            return candidates;
        }

        /// <summary>
        /// Reads the PE header of libvlc.dll to check it matches this process's
        /// architecture. A 32-bit dll cannot be loaded into the 64-bit game process,
        /// and the raw failure ("not found" / BadImageFormat) is misleading, so we
        /// detect it up front and say so plainly.
        /// </summary>
        private static string DescribeArchMismatch(string dir)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

            string dll = Path.Combine(dir, "libvlc.dll");
            if (!File.Exists(dll)) return null;

            try
            {
                using (FileStream fs = File.OpenRead(dll))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    fs.Seek(0x3C, SeekOrigin.Begin);          // e_lfanew
                    int peOffset = br.ReadInt32();
                    fs.Seek(peOffset, SeekOrigin.Begin);
                    if (br.ReadUInt32() != 0x00004550) return null;   // "PE\0\0"
                    ushort machine = br.ReadUInt16();

                    bool dllIs64 = machine == 0x8664 || machine == 0xAA64;  // AMD64 / ARM64
                    bool weAre64 = Environment.Is64BitProcess;

                    if (dllIs64 != weAre64)
                    {
                        return "VLC at " + dir + " is " + (dllIs64 ? "64-bit" : "32-bit")
                             + " but the game is a " + (weAre64 ? "64-bit" : "32-bit")
                             + " process. Install " + (weAre64 ? "64-bit" : "32-bit")
                             + " VLC from videolan.org (the default folder is C:\\Program Files\\VideoLAN\\VLC), "
                             + "then clear VlcPath in pixelreel.json or point it at that folder.";
                    }
                }
            }
            catch
            {
                return null;   // Unreadable header is not itself a reason to refuse.
            }

            return null;
        }

        /// <summary>Every folder we looked in, for /tv status output.</summary>
        public static string DescribeSearch(PixelReelConfig config)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (string dir in CandidateList(config))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string state = !Directory.Exists(dir) ? "no such folder"
                             : ContainsLibVlc(dir) ? "libvlc found" : "no libvlc here";
                sb.AppendLine("  " + dir + "  -> " + state);
            }
            return sb.ToString();
        }

        private static bool ContainsLibVlc(string dir)
        {
            string[] names = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new[] { "libvlc.dll" }
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? new[] { "libvlc.dylib", "libvlc.5.dylib" }
                    : new[] { "libvlc.so", "libvlc.so.5" };

            foreach (string n in names)
            {
                if (File.Exists(Path.Combine(dir, n))) return true;
            }
            return false;
        }
    }
}
