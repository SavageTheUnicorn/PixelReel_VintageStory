using System;
using System.Collections.Generic;
using PixelReel.BlockEntities;
using PixelReel.Network;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace PixelReel.Gui
{
    /// <summary>
    /// The media menu. Opens on "Now Playing" with transport controls when the display
    /// has something loaded, otherwise straight into the library browser.
    ///
    /// Controls are sent to the server rather than applied locally, so a pause is a
    /// pause for everyone in the room. Seeking is implemented as re-issuing the stream
    /// at a new start time, which keeps every client in step without a sync protocol.
    /// </summary>
    public class GuiDialogMediaMenu : GuiDialog
    {
        private static GuiDialogMediaMenu current;

        private readonly BlockPos displayPos;

        private bool browsing;
        private BrowseKind kind = BrowseKind.Libraries;
        private string parentId;
        private string title = "Media";
        private string status;
        private BrowseEntry[] entries = new BrowseEntry[0];

        private readonly List<(BrowseKind kind, string id, string title)> history =
            new List<(BrowseKind, string, string)>();

        private int scrollRow;
        private const int RowsPerPage = 10;
        private const double Width = 470;

        private long lastRefreshMs;

        public override string ToggleKeyCombinationCode => null;

        private GuiDialogMediaMenu(ICoreClientAPI capi, BlockPos displayPos) : base(capi)
        {
            this.displayPos = displayPos;
        }

        public static void OpenFor(ICoreClientAPI capi, BlockPos pos)
        {
            if (capi == null) return;

            current?.TryClose();
            current = new GuiDialogMediaMenu(capi, pos.Copy());

            BlockEntityDisplay display = current.Display;
            if (display != null && display.MediaTitle != null)
            {
                current.browsing = false;
                current.TryOpen();
                current.Compose();
            }
            else
            {
                current.browsing = true;
                current.TryOpen();
                current.Request(BrowseKind.Libraries, null, "Libraries", pushHistory: false);
            }
        }

        public static void HandleBrowseResult(BrowseResult result)
        {
            current?.OnBrowseResult(result);
        }

        private BlockEntityDisplay Display =>
            capi.World.BlockAccessor.GetBlockEntity(displayPos) as BlockEntityDisplay;

        // ---------------- browsing ----------------

        private void Request(BrowseKind newKind, string newParentId, string newTitle, bool pushHistory = true)
        {
            if (pushHistory) history.Add((kind, parentId, title));

            browsing = true;
            kind = newKind;
            parentId = newParentId;
            title = newTitle;
            entries = new BrowseEntry[0];
            status = "Loading...";
            scrollRow = 0;
            Compose();

            PixelReelModSystem.ClientChannel.SendPacket(new RequestBrowse
            {
                Kind = newKind,
                ParentId = newParentId
            });
        }

        private void OnBrowseResult(BrowseResult result)
        {
            if (result.Error != null)
            {
                status = result.Error;
                entries = new BrowseEntry[0];
            }
            else
            {
                entries = result.Entries ?? new BrowseEntry[0];
                status = entries.Length == 0 ? "Nothing here." : null;
            }
            Compose();
        }

        // ---------------- layout ----------------

        private void Compose()
        {
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            GuiComposer composer = capi.Gui
                .CreateCompo("pixelreel-media", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(browsing ? title : "Now Playing", () => TryClose())
                .BeginChildElements(bgBounds);

            if (browsing) ComposeBrowser(composer);
            else ComposeNowPlaying(composer);

            SingleComposer = composer.EndChildElements().Compose();
        }

        private void ComposeNowPlaying(GuiComposer composer)
        {
            BlockEntityDisplay display = Display;
            double y = 30;

            string heading = display?.MediaTitle ?? "Nothing loaded";
            composer.AddStaticText(heading, CairoFont.WhiteSmallishText(),
                ElementBounds.Fixed(0, y, Width, 28));
            y += 30;

            long pos = display?.PositionSeconds ?? 0;
            long len = display?.LengthSeconds ?? 0;
            string timeText = len > 0
                ? $"{FormatTime(pos)} / {FormatTime(len)}"
                : FormatTime(pos);
            if (display != null && display.Paused) timeText += "   (paused)";

            composer.AddStaticText(timeText, CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(0, y, Width, 24));
            y += 30;

            // Transport row.
            bool paused = display?.Paused ?? false;
            composer.AddSmallButton(paused ? "Resume" : "Pause", OnPauseToggle,
                ElementBounds.Fixed(0, y, 90, 26));
            composer.AddSmallButton("-30s", () => OnSeek(-30),
                ElementBounds.Fixed(95, y, 60, 26));
            composer.AddSmallButton("+30s", () => OnSeek(30),
                ElementBounds.Fixed(160, y, 60, 26));
            composer.AddSmallButton("Restart", () => OnSeekTo(0),
                ElementBounds.Fixed(225, y, 80, 26));

            if (display != null && display.IsEpisode)
            {
                composer.AddSmallButton("Next Ep", OnNextEpisode,
                    ElementBounds.Fixed(310, y, 80, 26));
            }

            composer.AddSmallButton("Stop", OnStop,
                ElementBounds.Fixed(Width - 70, y, 70, 26));
            y += 34;

            // Volume row.
            composer.AddStaticText("Volume", CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(0, y + 4, 70, 24));
            float vol = display?.Volume ?? 1f;
            composer.AddSlider(OnVolumeChanged, ElementBounds.Fixed(75, y, 240, 26), "vol");
            composer.AddSmallButton("Theatre Mode", OnFullscreen,
                ElementBounds.Fixed(Width - 140, y, 140, 26));
            y += 36;

            composer.AddSmallButton("Browse Library", OnBrowse,
                ElementBounds.Fixed(0, y, 160, 26));
            composer.AddSmallButton("Subtitles", OnCycleSubtitles,
                ElementBounds.Fixed(170, y, 110, 26));

            composer.GetSlider("vol").SetValues((int)Math.Round(vol * 100), 0, 100, 1, "%");
        }

        private void ComposeBrowser(GuiComposer composer)
        {
            double y = 30;
            const double rowH = 30;

            if (history.Count > 0)
            {
                composer.AddSmallButton("< Back", OnBack, ElementBounds.Fixed(0, y, 90, 24));
            }

            composer.AddSmallButton("Recently Added", OnRecent,
                ElementBounds.Fixed(history.Count > 0 ? 100 : 0, y, 150, 24));

            if (Display?.MediaTitle != null)
            {
                composer.AddSmallButton("Now Playing", OnNowPlaying,
                    ElementBounds.Fixed(Width - 120, y, 120, 24));
            }
            y += 34;

            if (status != null)
            {
                composer.AddStaticText(status, CairoFont.WhiteSmallText(),
                    ElementBounds.Fixed(0, y, Width, 60));
                return;
            }

            int shown = Math.Min(RowsPerPage, entries.Length - scrollRow);
            for (int i = 0; i < shown; i++)
            {
                int index = scrollRow + i;
                BrowseEntry entry = entries[index];

                string label = entry.Title;
                if (!string.IsNullOrEmpty(entry.Detail)) label += "   [" + entry.Detail + "]";
                if (entry.ResumeSeconds > 0) label += "   (resume " + FormatTime(entry.ResumeSeconds) + ")";

                composer.AddSmallButton(label, () => OnEntryClicked(index),
                    ElementBounds.Fixed(0, y, Width, 26), EnumButtonStyle.Normal);
                y += rowH;
            }

            if (entries.Length > RowsPerPage)
            {
                composer.AddSmallButton("Prev", OnPrevPage, ElementBounds.Fixed(0, y, 80, 24));
                composer.AddStaticText($"{scrollRow + 1}-{scrollRow + shown} of {entries.Length}",
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(90, y + 4, 200, 24));
                composer.AddSmallButton("Next", OnNextPage, ElementBounds.Fixed(Width - 80, y, 80, 24));
            }
        }

        // ---------------- actions ----------------

        private bool OnEntryClicked(int index)
        {
            if (index < 0 || index >= entries.Length) return true;
            BrowseEntry entry = entries[index];

            switch (entry.Kind)
            {
                case EntryKind.Library: Request(BrowseKind.LibraryItems, entry.Id, entry.Title); break;
                case EntryKind.Series: Request(BrowseKind.Seasons, entry.Id, entry.Title); break;
                case EntryKind.Season: Request(BrowseKind.Episodes, entry.Id, entry.Title); break;
                case EntryKind.Movie:
                case EntryKind.Episode: Play(entry.Id, entry.ResumeSeconds); break;
            }
            return true;
        }

        private void Play(string itemId, long startSeconds)
        {
            PixelReelModSystem.ClientChannel.SendPacket(new PlayItem
            {
                X = displayPos.X, Y = displayPos.Y, Z = displayPos.Z,
                ItemId = itemId,
                StartSeconds = startSeconds
            });
            TryClose();
        }

        private bool OnPauseToggle()
        {
            BlockEntityDisplay display = Display;
            SendControl((display?.Paused ?? false) ? 1 : 0);
            return true;
        }

        private bool OnStop()
        {
            SendControl(2);
            TryClose();
            return true;
        }

        private bool OnNextEpisode()
        {
            SendControl(4);
            TryClose();
            return true;
        }

        /// <summary>
        /// Seeking moves the playhead on every client rather than re-fetching the
        /// stream. Re-issuing the URL would restart the film, because Jellyfin ignores
        /// startTimeTicks on a static direct-play link.
        /// </summary>
        private bool OnSeek(long deltaSeconds)
        {
            BlockEntityDisplay display = Display;
            if (display?.MediaId == null) return true;

            SendSeek(Math.Max(0, display.PositionSeconds + deltaSeconds));
            return true;
        }

        private bool OnSeekTo(long seconds)
        {
            if (Display?.MediaId == null) return true;
            SendSeek(seconds);
            return true;
        }

        private void SendSeek(long seconds)
        {
            PixelReelModSystem.ClientChannel.SendPacket(new SeekTo
            {
                X = displayPos.X, Y = displayPos.Y, Z = displayPos.Z, Seconds = seconds
            });
        }

        private bool OnVolumeChanged(int value)
        {
            PixelReelModSystem.ClientChannel.SendPacket(new SetVolume
            {
                X = displayPos.X, Y = displayPos.Y, Z = displayPos.Z,
                Volume = value / 100f
            });
            return true;
        }

        private bool OnFullscreen()
        {
            TryClose();
            PixelReelModSystem.Theatre?.EnterFor(displayPos);
            return true;
        }

        /// <summary>
        /// Subtitles cycle locally rather than through the server: one viewer wanting
        /// captions shouldn't force them on everyone else in the room.
        /// </summary>
        private bool OnCycleSubtitles()
        {
            BlockEntityDisplay display = Display;
            if (display == null) return true;

            capi.ShowChatMessage("[pixelReel] " + display.CycleSubtitles());
            return true;
        }

        private bool OnBrowse()
        {
            history.Clear();
            Request(BrowseKind.Libraries, null, "Libraries", pushHistory: false);
            return true;
        }

        private bool OnNowPlaying()
        {
            browsing = false;
            Compose();
            return true;
        }

        private bool OnBack()
        {
            if (history.Count == 0) return true;
            (BrowseKind k, string id, string t) = history[history.Count - 1];
            history.RemoveAt(history.Count - 1);
            Request(k, id, t, pushHistory: false);
            return true;
        }

        private bool OnRecent()
        {
            Request(BrowseKind.Recent, null, "Recently Added");
            return true;
        }

        private bool OnPrevPage()
        {
            scrollRow = Math.Max(0, scrollRow - RowsPerPage);
            Compose();
            return true;
        }

        private bool OnNextPage()
        {
            if (scrollRow + RowsPerPage < entries.Length) scrollRow += RowsPerPage;
            Compose();
            return true;
        }

        private void SendControl(int action)
        {
            PixelReelModSystem.ClientChannel.SendPacket(new PlaybackControl
            {
                X = displayPos.X, Y = displayPos.Y, Z = displayPos.Z, Action = action
            });
        }

        /// <summary>Refreshes the position readout about once a second while open.</summary>
        public override void OnRenderGUI(float deltaTime)
        {
            base.OnRenderGUI(deltaTime);

            if (browsing) return;

            long now = capi.World.ElapsedMilliseconds;
            if (now - lastRefreshMs < 1000) return;
            lastRefreshMs = now;
            Compose();
        }

        private static string FormatTime(long seconds)
        {
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            return t.Hours > 0
                ? $"{t.Hours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes}:{t.Seconds:00}";
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            if (current == this) current = null;
        }
    }
}
