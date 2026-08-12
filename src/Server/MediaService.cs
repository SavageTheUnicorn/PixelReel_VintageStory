using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PixelReel.BlockEntities;
using PixelReel.Config;
using PixelReel.Jellyfin;
using PixelReel.Network;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace PixelReel.Server
{
    /// <summary>
    /// Server-side authority for everything media related.
    ///
    /// The server owns the credentials and decides what plays; clients only ever ask.
    /// All Jellyfin I/O happens off the main thread, and results are marshalled back
    /// before touching any world state.
    /// </summary>
    public class MediaService
    {
        private readonly ICoreServerAPI sapi;
        private readonly IServerNetworkChannel channel;
        private JellyfinClient jellyfin;

        private static PixelReelConfig Config => PixelReelModSystem.Config;

        public MediaService(ICoreServerAPI sapi, IServerNetworkChannel channel)
        {
            this.sapi = sapi;
            this.channel = channel;
            RebuildClient();
        }

        public void RebuildClient()
        {
            jellyfin = new JellyfinClient(
                Config.JellyfinUrl, Config.JellyfinApiKey, Config.JellyfinUserId, Config.RequestTimeoutSeconds);
        }

        public bool IsConfigured => jellyfin != null && jellyfin.IsConfigured;

        // ---------------- request handlers ----------------

        public void OnRequestProviderStatus(IServerPlayer player, RequestProviderStatus _)
        {
            if (!IsConfigured)
            {
                channel.SendPacket(new ProviderStatus
                {
                    Configured = false,
                    Error = "Jellyfin is not configured. Set JellyfinUrl, JellyfinApiKey and JellyfinUserId in the server's ModConfig/pixelreel.json."
                }, player);
                return;
            }

            RunAsync(player, async () =>
            {
                string name = await jellyfin.PingAsync();
                channel.SendPacket(new ProviderStatus
                {
                    Configured = true,
                    Reachable = true,
                    ServerName = name
                }, player);
            }, err => channel.SendPacket(new ProviderStatus
            {
                Configured = true,
                Reachable = false,
                Error = err
            }, player));
        }

        public void OnRequestBrowse(IServerPlayer player, RequestBrowse req)
        {
            if (!IsConfigured)
            {
                channel.SendPacket(new BrowseResult
                {
                    Kind = req.Kind,
                    Error = "Jellyfin is not configured on the server."
                }, player);
                return;
            }

            RunAsync(player, async () =>
            {
                List<BrowseEntry> entries;
                switch (req.Kind)
                {
                    case BrowseKind.Libraries: entries = await jellyfin.GetLibrariesAsync(); break;
                    case BrowseKind.LibraryItems: entries = await jellyfin.GetLibraryItemsAsync(req.ParentId); break;
                    case BrowseKind.Seasons: entries = await jellyfin.GetSeasonsAsync(req.ParentId); break;
                    case BrowseKind.Episodes: entries = await jellyfin.GetEpisodesAsync(req.ParentId); break;
                    case BrowseKind.Recent: entries = await jellyfin.GetRecentAsync(); break;
                    default: entries = new List<BrowseEntry>(); break;
                }

                channel.SendPacket(new BrowseResult
                {
                    Kind = req.Kind,
                    ParentId = req.ParentId,
                    Entries = entries.ToArray()
                }, player);
            }, err => channel.SendPacket(new BrowseResult
            {
                Kind = req.Kind,
                ParentId = req.ParentId,
                Error = err
            }, player));
        }

        public void OnPlayItem(IServerPlayer player, PlayItem req)
        {
            BlockEntityDisplay display = DisplayAt(req.X, req.Y, req.Z);
            if (display == null) return;

            if (!IsConfigured)
            {
                Tell(player, "Jellyfin is not configured on the server.");
                return;
            }

            RunAsync(player, async () =>
            {
                BrowseEntry item = await jellyfin.GetItemAsync(req.ItemId);
                if (item == null)
                {
                    Tell(player, "That item could not be found on the Jellyfin server.");
                    return;
                }

                // Ask for the plain file: Jellyfin ignores startTimeTicks on a static
                // URL anyway, so the resume position is carried in state and applied by
                // each client seeking locally once the stream opens.
                string url = jellyfin.StreamUrl(req.ItemId, 0);
                string subs = await SafeSubtitleUrl(req.ItemId);
                long start = req.StartSeconds;
                OnMainThread(() => display.SetMedia(req.ItemId, item.Title, url,
                                                    item.Kind == EntryKind.Episode, start, subs));
            }, err => Tell(player, "Could not start playback: " + err));
        }

        public void OnSeekTo(IServerPlayer player, SeekTo req)
        {
            DisplayAt(req.X, req.Y, req.Z)?.RequestSeek(req.Seconds);
        }

        public void OnSetPower(IServerPlayer player, SetPower req)
        {
            DisplayAt(req.X, req.Y, req.Z)?.SetPowered(req.On);
        }

        public void OnSetVolume(IServerPlayer player, SetVolume req)
        {
            DisplayAt(req.X, req.Y, req.Z)?.SetVolume(req.Volume);
        }

        public void OnPlaybackControl(IServerPlayer player, PlaybackControl req)
        {
            BlockEntityDisplay display = DisplayAt(req.X, req.Y, req.Z);
            if (display == null) return;

            switch (req.Action)
            {
                case 0: display.SetPausedState(true); break;
                case 1: display.SetPausedState(false); break;
                case 2: display.ClearMedia(); break;
                case 3: display.BumpEpoch(); break;          // restart / retry
                case 4: PlayNextEpisode(player, display); break;
            }
        }

        /// <summary>Skips to the next episode on demand, same path autoplay uses.</summary>
        private void PlayNextEpisode(IServerPlayer player, BlockEntityDisplay display)
        {
            if (!IsConfigured || !display.IsEpisode || display.MediaId == null)
            {
                Tell(player, "Nothing to skip to.");
                return;
            }

            string currentId = display.MediaId;
            RunAsync(player, async () =>
            {
                BrowseEntry next = await jellyfin.GetNextEpisodeAsync(currentId);
                if (next == null)
                {
                    Tell(player, "That was the last episode.");
                    return;
                }
                string url = jellyfin.StreamUrl(next.Id, 0);
                string subs = await SafeSubtitleUrl(next.Id);
                OnMainThread(() => display.SetMedia(next.Id, next.Title, url, true, 0, subs));
            }, err => Tell(player, "Could not skip: " + err));
        }

        /// <summary>
        /// A client reported its stream finished. Only the first such report for a given
        /// epoch is acted on, so ten clients watching the same screen don't advance the
        /// episode ten times.
        /// </summary>
        public void OnReportEnded(IServerPlayer player, ReportEnded req)
        {
            BlockEntityDisplay display = DisplayAt(req.X, req.Y, req.Z);
            if (display == null) return;
            if (req.Epoch != display.Epoch) return;
            if (!display.IsEpisode || !Config.AutoplayNextEpisode)
            {
                display.ClearMedia();
                return;
            }

            string finishedId = display.MediaId;
            display.BumpEpoch();

            RunAsync(player, async () =>
            {
                BrowseEntry next = await jellyfin.GetNextEpisodeAsync(finishedId);
                if (next == null)
                {
                    OnMainThread(display.ClearMedia);
                    return;
                }

                string url = jellyfin.StreamUrl(next.Id, 0);
                string subs = await SafeSubtitleUrl(next.Id);
                OnMainThread(() => display.SetMedia(next.Id, next.Title, url, true, 0, subs));
            }, err =>
            {
                sapi.Logger.Warning("[pixelReel] autoplay failed: " + err);
                display.ClearMedia();   // already on the main thread: onError is marshalled
            });
        }

        // ---------------- helpers ----------------

        /// <summary>
        /// Subtitles are a nice-to-have: a failure here must never stop the film from
        /// playing, so this swallows errors and returns null.
        /// </summary>
        private async Task<string> SafeSubtitleUrl(string itemId)
        {
            if (!Config.SubtitlesEnabled) return null;
            try
            {
                return await jellyfin.GetSubtitleUrlAsync(itemId, Config.SubtitleLanguage);
            }
            catch (Exception e)
            {
                sapi.Logger.Debug("[pixelReel] no external subtitles for {0}: {1}", itemId, e.Message);
                return null;
            }
        }

        private BlockEntityDisplay DisplayAt(int x, int y, int z)
        {
            return sapi.World.BlockAccessor.GetBlockEntity(new BlockPos(x, y, z)) as BlockEntityDisplay;
        }

        private void Tell(IServerPlayer player, string message)
        {
            channel.SendPacket(new Notice { Message = message }, player);
        }

        /// <summary>
        /// Runs network I/O off the main thread, then hops back onto it before touching
        /// world state. Every await in this file relies on that: block entities are not
        /// thread safe.
        /// </summary>
        private void RunAsync(IServerPlayer player, Func<Task> work, Action<string> onError)
        {
            Task.Run(async () =>
            {
                try
                {
                    await work();
                }
                catch (Exception e)
                {
                    string message = e is AggregateException agg && agg.InnerException != null
                        ? agg.InnerException.Message
                        : e.Message;
                    sapi.Logger.Warning("[pixelReel] Jellyfin request failed: " + e);
                    sapi.Event.EnqueueMainThreadTask(() => onError(message), "pixelreel-error");
                }
            });
        }

        /// <summary>Wraps a continuation so it lands back on the main thread.</summary>
        public void OnMainThread(Action action)
        {
            sapi.Event.EnqueueMainThreadTask(action, "pixelreel");
        }
    }
}
