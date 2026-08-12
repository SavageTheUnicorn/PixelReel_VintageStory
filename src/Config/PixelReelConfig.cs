namespace PixelReel.Config
{
    /// <summary>
    /// Config for both sides. Jellyfin credentials are only ever read on the server:
    /// clients receive stream URLs, never the raw key from this file.
    ///
    /// On a dedicated server the Jellyfin fields matter and the video fields are ignored;
    /// on a client it's the reverse. Singleplayer uses both.
    /// </summary>
    public class PixelReelConfig
    {
        // ---------------- Jellyfin (server side) ----------------

        /// <summary>e.g. "http://192.168.1.50:8096". No trailing slash needed.</summary>
        public string JellyfinUrl = "";

        /// <summary>
        /// An API key from Jellyfin's Dashboard > API Keys.
        ///
        /// Note this key ends up inside the playback URLs sent to clients, because their
        /// VLC has to authenticate to fetch the stream. Anyone who can use a display can
        /// therefore read it. Use a dedicated playback-only Jellyfin user, not an admin.
        /// </summary>
        public string JellyfinApiKey = "";

        /// <summary>The user id whose libraries are browsed. See .tv jellyfin, or Dashboard > Users.</summary>
        public string JellyfinUserId = "";

        public int RequestTimeoutSeconds = 15;

        /// <summary>Play the next episode automatically when one finishes.</summary>
        public bool AutoplayNextEpisode = true;

        /// <summary>Fetch and display subtitles when the media has them.</summary>
        public bool SubtitlesEnabled = true;

        /// <summary>
        /// Preferred subtitle language, as a 3-letter code ("eng", "spa", "jpn").
        /// Falls back to the default or first available track when absent.
        /// </summary>
        public string SubtitleLanguage = "eng";

        // ---------------- video (client side) ----------------

        /// <summary>Optional local file or URL for testing without Jellyfin.</summary>
        public string TestStreamUrl = "";

        /// <summary>Folder holding libvlc.dll / libvlc.so. Empty means autodetect.</summary>
        public string VlcPath = "";

        public string[] VlcOptions = new string[] {
            "--no-osd",
            "--no-video-title-show",
            "--no-snapshot-preview",
            "--quiet",
            "--network-caching=1500",
            // Subtitle rendering size, roughly 1/16th of video height.
            "--freetype-rel-fontsize=16"
        };

        /// <summary>Decode height cap. 4K into a world texture is 33MB of VRAM per screen.</summary>
        public int MaxDecodeHeight = 1080;

        /// <summary>
        /// Leave false. Hardware decoding usually stops frames reaching libvlc's vmem
        /// callback after the first one.
        /// </summary>
        public bool HardwareDecoding = false;

        /// <summary>Screen brightness, 0.1 to 1.0. 1.0 shows the video's own colours.</summary>
        public float ScreenBrightness = 1.0f;

        /// <summary>
        /// How far above the projector block the bottom of the image sits, in blocks.
        /// Default 1.5 keeps the picture clear of the block itself so the projector
        /// stays reachable and doesn't sit in the middle of the frame.
        /// </summary>
        public float VerticalOffsetBlocks = 1.5f;

        /// <summary>
        /// How far in front of the projector the image hangs, in blocks.
        ///
        /// Note the curved screen is concave, so its centre sits about 1.4 blocks
        /// further back than its edges. 1.0 clears the block for every type; raise it
        /// if you want more room.
        /// </summary>
        public float ForwardOffsetBlocks = 1.0f;

        /// <summary>
        /// Draw an opaque backdrop in theatre mode, covering the HUD and filling the
        /// letterbox bars. Turn off to keep the hotbar and chat visible over the video.
        /// </summary>
        public bool FullscreenHidesHud = true;

        /// <summary>Ignore distance falloff while watching in theatre mode.</summary>
        public bool FullscreenFullVolume = true;

        /// <summary>Master gain multiplier for pixelReel audio, 0..1.</summary>
        public float MasterVolume = 1.0f;
    }
}
