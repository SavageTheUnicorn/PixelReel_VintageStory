using System;
using System.Text;
using PixelReel.Blocks;
using PixelReel.Config;
using PixelReel.Displays;
using PixelReel.Gui;
using PixelReel.Network;
using PixelReel.Render;
using PixelReel.Video;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace PixelReel.BlockEntities
{
    /// <summary>
    /// One projector.
    ///
    /// State lives here and is synced through tree attributes, which Vintage Story
    /// replicates to every client and replays for late joiners. That gives multiplayer
    /// sync essentially for free -- no hand-written state packets needed.
    ///
    /// The server is authoritative: clients never set these fields directly, they send
    /// a request and wait for the sync to come back.
    /// </summary>
    public class BlockEntityDisplay : BlockEntity
    {
        public bool Powered;
        public float Volume = 1f;

        /// <summary>Jellyfin item id currently assigned, or null.</summary>
        public string MediaId;
        public string MediaTitle;

        /// <summary>Playable URL. Carries the API key, so it is only ever set server-side.</summary>
        public string StreamUrl;

        public bool IsEpisode;

        /// <summary>External subtitle file to attach, or null. Set server-side.</summary>
        public string SubtitleUrl;

        /// <summary>Synced so a pause is a pause for everyone watching, not just you.</summary>
        public bool Paused;

        /// <summary>Where the server wants the playhead. Applied without restarting.</summary>
        public long SeekSeconds;

        /// <summary>
        /// Bumped on every seek request. Separate from Epoch: Epoch means "new media,
        /// restart the stream", SeekEpoch means "same stream, move the playhead".
        /// </summary>
        public int SeekEpoch;

        /// <summary>
        /// Bumped whenever the media changes. Clients echo it back when reporting the
        /// end of playback, so a stale report can't advance the next episode.
        /// </summary>
        public int Epoch;

        private ICoreClientAPI capi;
        private VideoTexture videoTexture;
        private VideoPlayer videoPlayer;
        private DisplayRenderer renderer;

        private string playingUrl;
        private int playingEpoch = -1;
        private int appliedSeekEpoch = -1;
        private bool reportedEnd;

        /// <summary>Resume position to apply once the stream is actually rolling.</summary>
        private long pendingStartSeconds;
        private bool pendingStartApplied = true;

        /// <summary>Subtitles can only be attached once the stream is open, so this
        /// is retried from the tick loop until it takes.</summary>
        private bool subtitlesApplied = true;
        private int subtitleAttempts;

        private static PixelReelConfig Config => PixelReelModSystem.Config;

        /// <summary>Exposed for the fullscreen overlay, which draws this same texture.</summary>
        public VideoTexture Texture => videoTexture;

        public long PositionSeconds => videoPlayer?.TimeSeconds ?? 0;
        public long LengthSeconds => videoPlayer?.LengthSeconds ?? 0;
        public bool HasVideo => videoPlayer != null && playingUrl != null;

        public DisplayType Type => (Block as BlockDisplay)?.Type ?? DisplayType.CompactTelevision;
        public BlockFacing Facing => (Block as BlockDisplay)?.Facing ?? BlockFacing.NORTH;

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);

            capi = api as ICoreClientAPI;
            if (capi == null) return;

            videoTexture = new VideoTexture(capi);
            videoPlayer = new VideoPlayer(capi, videoTexture, Config.MaxDecodeHeight, Config.HardwareDecoding);

            renderer = new DisplayRenderer(capi, Pos, videoTexture, Type, Facing,
                                           Config.ScreenBrightness,
                                           Config.VerticalOffsetBlocks,
                                           Config.ForwardOffsetBlocks);
            capi.Event.RegisterRenderer(renderer, EnumRenderStage.Opaque, "pixelreel-display");

            RegisterGameTickListener(OnClientTick, 100);
            SyncPlayback();
        }

        // ---------------- interaction ----------------

        /// <summary>Sneak+right-click toggles power. Plain right-click opens the media menu.</summary>
        public bool OnInteract(IPlayer byPlayer, bool sneaking)
        {
            if (Api.Side != EnumAppSide.Client) return true;

            if (sneaking)
            {
                PixelReelModSystem.ClientChannel.SendPacket(new SetPower
                {
                    X = Pos.X, Y = Pos.Y, Z = Pos.Z, On = !Powered
                });
                return true;
            }

            GuiDialogMediaMenu.OpenFor(capi, Pos);
            return true;
        }

        // ---------------- server-side mutators ----------------

        public void SetPowered(bool on)
        {
            Powered = on;
            MarkDirty(true);
        }

        public void SetVolume(float volume01)
        {
            Volume = GameMath.Clamp(volume01, 0f, 1f);
            MarkDirty(true);
        }

        public void SetMedia(string mediaId, string title, string streamUrl, bool isEpisode,
                             long startSeconds = 0, string subtitleUrl = null)
        {
            SubtitleUrl = subtitleUrl;
            SeekSeconds = startSeconds;
            SeekEpoch++;
            MediaId = mediaId;
            MediaTitle = title;
            StreamUrl = streamUrl;
            IsEpisode = isEpisode;
            Powered = true;
            Paused = false;
            Epoch++;
            MarkDirty(true);
        }

        public void ClearMedia()
        {
            MediaId = null;
            MediaTitle = null;
            StreamUrl = null;
            SubtitleUrl = null;
            IsEpisode = false;
            Paused = false;
            Epoch++;
            MarkDirty(true);
        }

        /// <summary>Server-side: asks every client to move the playhead.</summary>
        public void RequestSeek(long seconds)
        {
            SeekSeconds = Math.Max(0, seconds);
            SeekEpoch++;
            MarkDirty(true);
        }

        public void BumpEpoch()
        {
            Epoch++;
            MarkDirty(true);
        }

        /// <summary>Server-side: records the pause and lets sync push it to every client.</summary>
        public void SetPausedState(bool paused)
        {
            Paused = paused;
            MarkDirty(true);
        }

        // ---------------- client playback ----------------

        /// <summary>
        /// Brings local playback in line with the synced state. Called after every state
        /// change and on load, so a client that joins mid-film starts the same stream
        /// everyone else is watching.
        /// </summary>
        private void SyncPlayback()
        {
            if (capi == null || videoPlayer == null) return;

            bool shouldPlay = Powered && !string.IsNullOrWhiteSpace(StreamUrl);

            if (!shouldPlay)
            {
                if (playingUrl != null)
                {
                    videoPlayer.Stop();
                    playingUrl = null;
                    renderer.Active = false;
                }
                return;
            }

            // Restart when either the URL or the epoch changed. The epoch catches the
            // case of replaying the same item deliberately, and seeking (which the
            // server implements as re-issuing the stream at a new start time).
            if (StreamUrl == playingUrl && Epoch == playingEpoch)
            {
                if (Paused != videoPlayer.IsPaused) videoPlayer.SetPaused(Paused);

                // A seek arrived for media we're already playing: move the playhead
                // rather than restarting the stream.
                if (SeekEpoch != appliedSeekEpoch)
                {
                    appliedSeekEpoch = SeekEpoch;
                    if (!videoPlayer.SeekTo(SeekSeconds))
                    {
                        // Not seekable yet (still opening); retry from the tick loop.
                        pendingStartSeconds = SeekSeconds;
                        pendingStartApplied = false;
                    }
                }
                return;
            }

            if (!VlcRuntime.Available)
            {
                capi.TriggerIngameError(this, "novlc", Lang.Get("pixelreel:vlc-missing"));
                return;
            }

            reportedEnd = false;
            if (videoPlayer.Play(StreamUrl))
            {
                playingUrl = StreamUrl;
                playingEpoch = Epoch;

                // Jellyfin ignores startTimeTicks on a static direct-play URL, so a
                // resume position has to be applied locally once playback is rolling.
                pendingStartSeconds = SeekSeconds;
                pendingStartApplied = SeekSeconds <= 0;
                appliedSeekEpoch = SeekEpoch;

                subtitlesApplied = !Config.SubtitlesEnabled;
                subtitleAttempts = 0;

                renderer.Active = true;
                capi.Logger.Notification("[pixelReel] playing '{0}' at {1}", MediaTitle ?? "?", Pos);
            }
            else
            {
                renderer.Active = false;
            }
        }

        private void OnClientTick(float dt)
        {
            if (capi == null || videoPlayer == null) return;

            SyncPlayback();

            if (!Powered || playingUrl == null) return;

            // Apply a resume or a seek that arrived before the stream was seekable.
            if (!pendingStartApplied && videoPlayer.IsSeekable && videoPlayer.IsPlaying)
            {
                if (videoPlayer.SeekTo(pendingStartSeconds)) pendingStartApplied = true;
            }

            ApplySubtitles();

            // Tell the server once when the stream runs out, so it can autoplay.
            if (!reportedEnd && videoPlayer.HasEnded)
            {
                reportedEnd = true;
                PixelReelModSystem.ClientChannel.SendPacket(new ReportEnded
                {
                    X = Pos.X, Y = Pos.Y, Z = Pos.Z, Epoch = Epoch
                });
                return;
            }

            if (!videoPlayer.IsPlaying) return;

            EntityPlayer player = capi.World.Player?.Entity;
            if (player == null) return;

            // Measured from the centre of the projected image, not the block, so a
            // 14-wide cinema screen isn't quieter at one end than the other.
            // Watching in theatre mode means you hear it properly wherever you sit.
            bool watchingFullscreen = Config.FullscreenFullVolume
                                   && PixelReelModSystem.Theatre != null
                                   && PixelReelModSystem.Theatre.IsWatching(this);

            float gain;
            if (watchingFullscreen)
            {
                gain = 1f;
            }
            else
            {
                double dist = player.Pos.XYZ.DistanceTo(ScreenCentre());
                float range = Type.AudioRange <= 0 ? 16f : Type.AudioRange;
                gain = (float)GameMath.Clamp(1.0 - dist / range, 0.0, 1.0);
            }

            videoPlayer.ApplyGain(gain
                                 * GameMath.Clamp(Volume, 0f, 1f)
                                 * GameMath.Clamp(Config.MasterVolume, 0f, 1f));
        }

        /// <summary>
        /// Attaches subtitles once playback is under way. Tracks aren't enumerable
        /// until libvlc has parsed the stream, so this retries for a few seconds and
        /// then gives up quietly rather than spinning forever.
        /// </summary>
        private void ApplySubtitles()
        {
            if (subtitlesApplied || !videoPlayer.IsPlaying) return;

            subtitleAttempts++;
            if (subtitleAttempts > 50)   // ~5 seconds at a 100ms tick
            {
                subtitlesApplied = true;
                return;
            }

            bool done = false;

            // External file first: if Jellyfin gave us one it's the better match for the
            // requested language than whatever happens to be muxed in.
            if (!string.IsNullOrWhiteSpace(SubtitleUrl))
            {
                done = videoPlayer.AddSubtitleSlave(SubtitleUrl, true);
            }

            if (!done)
            {
                done = videoPlayer.SelectSubtitleTrack(Config.SubtitleLanguage);
            }

            if (done) subtitlesApplied = true;
        }

        /// <summary>Cycles subtitle tracks locally. Subtitles are a per-viewer choice.</summary>
        public string CycleSubtitles()
        {
            return videoPlayer?.CycleSubtitles() ?? "No player";
        }

        private Vec3d ScreenCentre()
        {
            DisplayType t = Type;
            Vec3f n = Facing.Normalf;
            return new Vec3d(
                Pos.X + 0.5 + n.X * Config.ForwardOffsetBlocks,
                Pos.Y + Config.VerticalOffsetBlocks + t.HeightBlocks * 0.5,
                Pos.Z + 0.5 + n.Z * Config.ForwardOffsetBlocks);
        }

        // ---------------- persistence and sync ----------------

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetBool("powered", Powered);
            tree.SetFloat("volume", Volume);
            tree.SetInt("epoch", Epoch);
            tree.SetBool("episode", IsEpisode);
            tree.SetBool("paused", Paused);
            tree.SetLong("seekSeconds", SeekSeconds);
            tree.SetInt("seekEpoch", SeekEpoch);
            if (MediaId != null) tree.SetString("mediaId", MediaId);
            if (MediaTitle != null) tree.SetString("mediaTitle", MediaTitle);
            if (StreamUrl != null) tree.SetString("streamUrl", StreamUrl);
            if (SubtitleUrl != null) tree.SetString("subtitleUrl", SubtitleUrl);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolve)
        {
            base.FromTreeAttributes(tree, worldForResolve);
            Powered = tree.GetBool("powered", false);
            Volume = tree.GetFloat("volume", 1f);
            Epoch = tree.GetInt("epoch", 0);
            IsEpisode = tree.GetBool("episode", false);
            Paused = tree.GetBool("paused", false);
            SeekSeconds = tree.GetLong("seekSeconds", 0);
            SeekEpoch = tree.GetInt("seekEpoch", 0);
            MediaId = tree.GetString("mediaId");
            MediaTitle = tree.GetString("mediaTitle");
            StreamUrl = tree.GetString("streamUrl");
            SubtitleUrl = tree.GetString("subtitleUrl");

            SyncPlayback();
        }

        public override void OnBlockBroken(IPlayer byPlayer = null)
        {
            TearDownClient();
            base.OnBlockBroken(byPlayer);
        }

        public override void OnBlockRemoved()
        {
            TearDownClient();
            base.OnBlockRemoved();
        }

        public override void OnBlockUnloaded()
        {
            TearDownClient();
            base.OnBlockUnloaded();
        }

        private void TearDownClient()
        {
            if (capi == null) return;

            if (renderer != null)
            {
                capi.Event.UnregisterRenderer(renderer, EnumRenderStage.Opaque);
                renderer.Dispose();
                renderer = null;
            }

            videoPlayer?.Dispose();
            videoPlayer = null;

            videoTexture?.Dispose();
            videoTexture = null;
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder sb)
        {
            DisplayType t = Type;
            sb.AppendLine(Lang.Get("pixelreel:info-size", t.WidthBlocks, t.HeightBlocks));
            sb.AppendLine(Powered ? "Power: on" : "Power: off");

            if (MediaTitle != null)
            {
                sb.AppendLine((Paused ? "Paused: " : "Now playing: ") + MediaTitle);
            }

            if (capi == null) return;

            if (!VlcRuntime.Available)
            {
                sb.AppendLine("VLC: not available");
                return;
            }

            if (videoTexture != null && videoTexture.HasFrame)
            {
                sb.AppendLine($"Signal: {videoTexture.FrameWidth}x{videoTexture.FrameHeight}");
            }
        }
    }
}
