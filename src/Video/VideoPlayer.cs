using System;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace PixelReel.Video
{
    /// <summary>
    /// One libvlc media player driving one <see cref="VideoTexture"/>.
    /// Equivalent of ChannelPlayer.java, minus the channel/retry logic (Phase 3+).
    ///
    /// Uses libvlc's vmem output: we hand it a raw buffer and it hands us BGRA
    /// frames on its own decode thread. No window, no surface, no interop with
    /// Vintage Story's GL context from the wrong thread.
    /// </summary>
    public class VideoPlayer : IDisposable
    {
        private readonly ICoreClientAPI capi;
        private readonly VideoTexture videoTexture;
        private readonly int maxDecodeHeight;
        private readonly bool hardwareDecoding;

        private MediaPlayer player;

        // Frame buffer libvlc writes into. Unmanaged so its address is stable.
        private IntPtr frameBuffer;
        private int frameBufferBytes;
        private int frameWidth;
        private int frameHeight;

        // Delegates MUST be kept alive as fields. If they are collected while
        // libvlc still holds the function pointers the game dies with a hard crash.
        private MediaPlayer.LibVLCVideoFormatCb formatCb;
        private MediaPlayer.LibVLCVideoCleanupCb cleanupCb;
        private MediaPlayer.LibVLCVideoLockCb lockCb;
        private MediaPlayer.LibVLCVideoUnlockCb unlockCb;
        private MediaPlayer.LibVLCVideoDisplayCb displayCb;

        private int lastAppliedVolume = -1;
        private bool disposed;
        private volatile bool ended;

        public bool IsPlaying => player != null && player.IsPlaying;

        /// <summary>True once the stream reached its natural end (not a stop or error).</summary>
        public bool HasEnded => ended;

        /// <summary>Current playback position in seconds, or 0 when unknown.</summary>
        public long TimeSeconds
        {
            get
            {
                try { return player == null ? 0 : Math.Max(0, player.Time / 1000); }
                catch { return 0; }
            }
        }

        /// <summary>Total length in seconds, or 0 when the stream hasn't reported one.</summary>
        public long LengthSeconds
        {
            get
            {
                try { return player == null ? 0 : Math.Max(0, player.Length / 1000); }
                catch { return 0; }
            }
        }

        public bool IsPaused
        {
            get
            {
                try { return player != null && player.State == LibVLCSharp.Shared.VLCState.Paused; }
                catch { return false; }
            }
        }
        public string CurrentUrl { get; private set; }

        public VideoPlayer(ICoreClientAPI capi, VideoTexture videoTexture, int maxDecodeHeight,
                           bool hardwareDecoding)
        {
            this.capi = capi;
            this.videoTexture = videoTexture;
            this.maxDecodeHeight = maxDecodeHeight > 0 ? maxDecodeHeight : 1080;
            this.hardwareDecoding = hardwareDecoding;
        }

        public bool Play(string url)
        {
            if (disposed) return false;
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!VlcRuntime.Available)
            {
                capi.Logger.Warning("[pixelReel] play requested but VLC is unavailable");
                return false;
            }

            Stop();

            try
            {
                player = VlcRuntime.NewPlayer();
                if (player == null) return false;

                formatCb = OnVideoFormat;
                cleanupCb = OnVideoCleanup;
                lockCb = OnVideoLock;
                unlockCb = OnVideoUnlock;
                displayCb = OnVideoDisplay;

                player.SetVideoFormatCallbacks(formatCb, cleanupCb);
                player.SetVideoCallbacks(lockCb, unlockCb, displayCb);

                // EndReached fires on a libvlc thread and must not call back into
                // libvlc, so we only set a flag and let the game tick act on it.
                ended = false;
                player.EndReached += (s2, e2) => ended = true;
                player.EncounteredError += (s2, e2) =>
                {
                    ended = true;
                    capi.Logger.Warning("[pixelReel] libvlc reported a playback error");
                };

                // Off by default. Hardware decoding hands libvlc's output to the GPU,
                // which frequently means the vmem callbacks below receive nothing after
                // the first frame. Software decode is the reliable path for frame grabbing.
                player.EnableHardwareDecoding = hardwareDecoding;

                using (Media media = BuildMedia(url))
                {
                    if (media == null) return false;
                    CurrentUrl = url;
                    lastAppliedVolume = -1;
                    return player.Play(media);
                }
            }
            catch (Exception e)
            {
                capi.Logger.Error("[pixelReel] failed to start playback: {0}", e);
                Stop();
                return false;
            }
        }

        private Media BuildMedia(string url)
        {
            LibVLC vlc = VlcRuntime.Instance;
            if (vlc == null) return null;

            bool isUri = url.Contains("://");
            return isUri
                ? new Media(vlc, url, FromType.FromLocation)
                : new Media(vlc, url, FromType.FromPath);
        }

        public void Stop()
        {
            MediaPlayer old = player;
            player = null;
            if (old != null)
            {
                try
                {
                    old.Stop();
                    old.Dispose();
                }
                catch (Exception e)
                {
                    capi.Logger.Warning("[pixelReel] error stopping player: {0}", e.Message);
                }
            }
            CurrentUrl = null;
            ended = false;
            FreeFrameBuffer();
        }

        /// <summary>
        /// Moves the playhead without restarting the stream. Works because Jellyfin
        /// serves direct-play files over HTTP with range request support, so libvlc can
        /// jump within the file itself.
        /// </summary>
        public bool SeekTo(long seconds)
        {
            if (player == null) return false;
            try
            {
                if (!player.IsSeekable) return false;
                player.Time = Math.Max(0, seconds) * 1000L;
                return true;
            }
            catch (Exception e)
            {
                capi.Logger.Debug("[pixelReel] seek failed: {0}", e.Message);
                return false;
            }
        }

        public bool IsSeekable
        {
            get
            {
                try { return player != null && player.IsSeekable; }
                catch { return false; }
            }
        }

        // ---------------- subtitles ----------------

        /// <summary>
        /// Attaches an external subtitle file (Jellyfin stores many subs as separate
        /// .srt rather than muxed into the video) and selects it.
        /// </summary>
        public bool AddSubtitleSlave(string uri, bool select)
        {
            if (player == null || string.IsNullOrWhiteSpace(uri)) return false;
            try
            {
                return player.AddSlave(MediaSlaveType.Subtitle, uri, select);
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[pixelReel] could not attach subtitles: {0}", e.Message);
                return false;
            }
        }

        /// <summary>
        /// Picks an embedded subtitle track, preferring the given language code.
        /// Returns true once a track was chosen (or deliberately disabled).
        /// </summary>
        public bool SelectSubtitleTrack(string preferredLanguage)
        {
            if (player == null) return false;

            try
            {
                TrackDescription[] tracks = player.SpuDescription;
                if (tracks == null || tracks.Length == 0) return false;

                int chosen = -1;

                if (!string.IsNullOrWhiteSpace(preferredLanguage))
                {
                    foreach (TrackDescription t in tracks)
                    {
                        if (t.Id < 0) continue;   // -1 is the "Disable" pseudo-track
                        if (t.Name != null &&
                            t.Name.IndexOf(preferredLanguage, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            chosen = t.Id;
                            break;
                        }
                    }
                }

                if (chosen < 0)
                {
                    foreach (TrackDescription t in tracks)
                    {
                        if (t.Id >= 0) { chosen = t.Id; break; }
                    }
                }

                if (chosen < 0) return false;

                player.SetSpu(chosen);
                return true;
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[pixelReel] subtitle track selection failed: {0}", e.Message);
                return false;
            }
        }

        /// <summary>Cycles: off, then each available track, then back to off.</summary>
        public string CycleSubtitles()
        {
            if (player == null) return "No player";

            try
            {
                TrackDescription[] tracks = player.SpuDescription;
                if (tracks == null || tracks.Length == 0) return "No subtitle tracks";

                int current = player.Spu;
                int currentIndex = -1;
                for (int i = 0; i < tracks.Length; i++)
                {
                    if (tracks[i].Id == current) { currentIndex = i; break; }
                }

                int nextIndex = (currentIndex + 1) % tracks.Length;
                player.SetSpu(tracks[nextIndex].Id);

                return tracks[nextIndex].Id < 0
                    ? "Subtitles off"
                    : "Subtitles: " + (tracks[nextIndex].Name ?? "track " + tracks[nextIndex].Id);
            }
            catch (Exception e)
            {
                return "Subtitle switch failed: " + e.Message;
            }
        }

        public bool HasSubtitleTracks
        {
            get
            {
                try
                {
                    TrackDescription[] t = player?.SpuDescription;
                    return t != null && t.Length > 1;
                }
                catch { return false; }
            }
        }

        public void SetPaused(bool paused)
        {
            if (player == null) return;
            try { player.SetPause(paused); }
            catch (Exception e) { capi.Logger.Warning("[pixelReel] pause failed: {0}", e.Message); }
        }

        /// <summary>
        /// Distance-attenuated volume, same model as the Fabric mod: it is not true
        /// 3D audio, just libvlc's own volume scaled by listener distance.
        /// Debounced because libvlc dislikes being spammed with volume changes.
        /// </summary>
        public void ApplyGain(float gain01)
        {
            if (player == null) return;

            int volume = (int)Math.Round(GameMath.Clamp(gain01, 0f, 1f) * 100f);
            if (lastAppliedVolume >= 0 && Math.Abs(volume - lastAppliedVolume) < 2) return;

            lastAppliedVolume = volume;
            try
            {
                player.Mute = volume <= 0;
                player.Volume = volume;
            }
            catch (Exception e)
            {
                capi.Logger.Debug("[pixelReel] volume set failed: {0}", e.Message);
            }
        }

        // ---------------- libvlc callbacks (decode thread) ----------------

        private uint OnVideoFormat(ref IntPtr opaque, IntPtr chroma,
                                   ref uint width, ref uint height,
                                   ref uint pitches, ref uint lines)
        {
            // Ask for BGRA so the upload path needs no channel swizzle.
            WriteChroma(chroma, "BGRA");

            // Downscale in libvlc rather than shipping 4K frames across the bus.
            // Modifying width/height here makes libvlc insert its own scaler.
            if (maxDecodeHeight > 0 && height > (uint)maxDecodeHeight && height > 0)
            {
                double scale = (double)maxDecodeHeight / height;
                uint newW = (uint)Math.Max(2, (int)Math.Round(width * scale) & ~1);
                uint newH = (uint)Math.Max(2, maxDecodeHeight & ~1);
                width = newW;
                height = newH;
            }

            frameWidth = (int)width;
            frameHeight = (int)height;

            pitches = width * 4;
            lines = height;

            AllocFrameBuffer((int)(pitches * lines));

            // Return value is the number of planes. BGRA is a single packed plane.
            return 1;
        }

        private void OnVideoCleanup(ref IntPtr opaque)
        {
            FreeFrameBuffer();
        }

        private IntPtr OnVideoLock(IntPtr opaque, IntPtr planes)
        {
            if (frameBuffer == IntPtr.Zero) return IntPtr.Zero;
            Marshal.WriteIntPtr(planes, 0, frameBuffer);
            return frameBuffer;
        }

        private void OnVideoUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
        {
            // Nothing to do: we copy in Display, once the frame is complete.
        }

        private void OnVideoDisplay(IntPtr opaque, IntPtr picture)
        {
            if (frameBuffer == IntPtr.Zero) return;
            videoTexture.Submit(frameBuffer, frameWidth, frameHeight);
        }

        private static void WriteChroma(IntPtr chroma, string fourcc)
        {
            byte[] bytes = new byte[4];
            for (int i = 0; i < 4 && i < fourcc.Length; i++) bytes[i] = (byte)fourcc[i];
            Marshal.Copy(bytes, 0, chroma, 4);
        }

        private void AllocFrameBuffer(int bytes)
        {
            if (frameBuffer != IntPtr.Zero && frameBufferBytes >= bytes) return;
            FreeFrameBuffer();
            frameBuffer = Marshal.AllocHGlobal(bytes);
            frameBufferBytes = bytes;
        }

        private void FreeFrameBuffer()
        {
            if (frameBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(frameBuffer);
                frameBuffer = IntPtr.Zero;
                frameBufferBytes = 0;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Stop();
        }
    }
}
