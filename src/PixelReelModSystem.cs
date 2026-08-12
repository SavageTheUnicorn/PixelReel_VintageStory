using System;
using PixelReel.BlockEntities;
using PixelReel.Blocks;
using PixelReel.Config;
using PixelReel.Gui;
using PixelReel.Network;
using PixelReel.Server;
using PixelReel.Video;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace PixelReel
{
    public class PixelReelModSystem : ModSystem
    {
        public const string ConfigFile = "pixelreel.json";

        public static PixelReelConfig Config { get; private set; } = new PixelReelConfig();
        public static IClientNetworkChannel ClientChannel { get; private set; }
        public static GuiDialogTheatre Theatre { get; private set; }

        private ICoreClientAPI capi;
        private ICoreServerAPI sapi;
        private MediaService media;

        public override void StartPre(ICoreAPI api)
        {
            DependencyResolver.Init();
            LoadConfig(api);
        }

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            api.RegisterBlockClass("BlockDisplay", typeof(BlockDisplay));
            api.RegisterBlockEntityClass("Display", typeof(BlockEntityDisplay));
        }

        // ---------------- client ----------------

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            VlcRuntime.Initialize(api, Config);

            ClientChannel = api.Network.RegisterChannel(Channel.Name)
                .RegisterMessageType<RequestBrowse>()
                .RegisterMessageType<BrowseResult>()
                .RegisterMessageType<PlayItem>()
                .RegisterMessageType<SeekTo>()
                .RegisterMessageType<SetPower>()
                .RegisterMessageType<SetVolume>()
                .RegisterMessageType<PlaybackControl>()
                .RegisterMessageType<ReportEnded>()
                .RegisterMessageType<Notice>()
                .RegisterMessageType<ProviderStatus>()
                .RegisterMessageType<RequestProviderStatus>()
                .SetMessageHandler<BrowseResult>(GuiDialogMediaMenu.HandleBrowseResult)
                .SetMessageHandler<Notice>(OnNotice)
                .SetMessageHandler<ProviderStatus>(OnProviderStatus);

            Theatre = new GuiDialogTheatre(api);

            api.Input.RegisterHotKey("pixelreelfullscreen", "pixelReel: theatre mode",
                                     GlKeys.F6, HotkeyType.GUIOrOtherControls);
            api.Input.SetHotKeyHandler("pixelreelfullscreen", _ => Theatre.ToggleForLookedAtBlock());

            // Escape should leave theatre mode rather than open the pause menu.
            api.Event.KeyDown += OnKeyDown;

            RegisterClientCommands(api);
        }

        private void OnKeyDown(KeyEvent ev)
        {
            if (Theatre != null && Theatre.IsActive && ev.KeyCode == (int)GlKeys.Escape)
            {
                Theatre.Close();
                ev.Handled = true;
            }
        }

        private void OnNotice(Notice packet)
        {
            capi.ShowChatMessage("[pixelReel] " + packet.Message);
        }

        private void OnProviderStatus(ProviderStatus packet)
        {
            if (!packet.Configured)
            {
                capi.ShowChatMessage("[pixelReel] Jellyfin not configured. " + packet.Error);
                return;
            }
            capi.ShowChatMessage(packet.Reachable
                ? $"[pixelReel] Jellyfin OK: {packet.ServerName}"
                : $"[pixelReel] Jellyfin unreachable: {packet.Error}");
        }

        private void RegisterClientCommands(ICoreClientAPI api)
        {
            // Client commands use a dot prefix in Vintage Story: ".tv"
            api.ChatCommands
                .Create("tv")
                .WithDescription("pixelReel client diagnostics")
                .BeginSubCommand("status")
                    .WithDescription("VLC availability and decode settings")
                    .HandleWith(_ =>
                    {
                        string vlc = VlcRuntime.Available
                            ? "available (" + VlcRuntime.Instance.Version + ")"
                            : "UNAVAILABLE: " + (VlcRuntime.FailureReason ?? "unknown");
                        string search = VlcRuntime.Available
                            ? ""
                            : "\nSearched:\n" + VlcRuntime.DescribeSearch(Config);
                        string bits = Environment.Is64BitProcess ? "64-bit" : "32-bit";
                        return TextCommandResult.Success(
                            $"Game process: {bits}\nVLC: {vlc}\nDecode cap: {Config.MaxDecodeHeight}p{search}");
                    })
                .EndSubCommand()
                .BeginSubCommand("jellyfin")
                    .WithDescription("Ask the server whether Jellyfin is reachable")
                    .HandleWith(_ =>
                    {
                        ClientChannel.SendPacket(new RequestProviderStatus());
                        return TextCommandResult.Success("Asking the server...");
                    })
                .EndSubCommand()
                .BeginSubCommand("reload")
                    .WithDescription("Re-read the client config")
                    .HandleWith(_ =>
                    {
                        LoadConfig(api);
                        return TextCommandResult.Success("Client config reloaded.");
                    })
                .EndSubCommand();
        }

        // ---------------- server ----------------

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

            IServerNetworkChannel channel = api.Network.RegisterChannel(Channel.Name)
                .RegisterMessageType<RequestBrowse>()
                .RegisterMessageType<BrowseResult>()
                .RegisterMessageType<PlayItem>()
                .RegisterMessageType<SeekTo>()
                .RegisterMessageType<SetPower>()
                .RegisterMessageType<SetVolume>()
                .RegisterMessageType<PlaybackControl>()
                .RegisterMessageType<ReportEnded>()
                .RegisterMessageType<Notice>()
                .RegisterMessageType<ProviderStatus>()
                .RegisterMessageType<RequestProviderStatus>();

            media = new MediaService(api, channel);

            channel
                .SetMessageHandler<RequestBrowse>(media.OnRequestBrowse)
                .SetMessageHandler<PlayItem>(media.OnPlayItem)
                .SetMessageHandler<SeekTo>(media.OnSeekTo)
                .SetMessageHandler<SetPower>(media.OnSetPower)
                .SetMessageHandler<SetVolume>(media.OnSetVolume)
                .SetMessageHandler<PlaybackControl>(media.OnPlaybackControl)
                .SetMessageHandler<ReportEnded>(media.OnReportEnded)
                .SetMessageHandler<RequestProviderStatus>(media.OnRequestProviderStatus);

            RegisterServerCommands(api);
        }

        private void RegisterServerCommands(ICoreServerAPI api)
        {
            api.ChatCommands
                .Create("pixelreel")
                .WithDescription("pixelReel server administration")
                .RequiresPrivilege(Privilege.controlserver)
                .BeginSubCommand("reload")
                    .WithDescription("Re-read the server config and reconnect to Jellyfin")
                    .HandleWith(_ =>
                    {
                        LoadConfig(api);
                        media.RebuildClient();
                        return TextCommandResult.Success(
                            media.IsConfigured
                                ? "Config reloaded, Jellyfin configured."
                                : "Config reloaded, but Jellyfin is missing URL, API key or user id.");
                    })
                .EndSubCommand()
                .BeginSubCommand("status")
                    .WithDescription("Whether Jellyfin credentials are present")
                    .HandleWith(_ => TextCommandResult.Success(
                        media.IsConfigured
                            ? "Jellyfin is configured. Use .tv jellyfin to test the connection."
                            : "Jellyfin is NOT configured. Set JellyfinUrl, JellyfinApiKey and JellyfinUserId in ModConfig/pixelreel.json."))
                .EndSubCommand();
        }

        public override void Dispose()
        {
            if (capi != null)
            {
                Theatre?.Dispose();
                capi.Event.KeyDown -= OnKeyDown;
                VlcRuntime.Shutdown();
            }
            base.Dispose();
        }

        private static void LoadConfig(ICoreAPI api)
        {
            try
            {
                PixelReelConfig loaded = api.LoadModConfig<PixelReelConfig>(ConfigFile);
                if (loaded == null)
                {
                    loaded = new PixelReelConfig();
                    api.StoreModConfig(loaded, ConfigFile);
                    api.Logger.Notification("[pixelReel] wrote default config to ModConfig/" + ConfigFile);
                }
                Config = loaded;
            }
            catch (Exception e)
            {
                api.Logger.Error("[pixelReel] config unreadable, using defaults: {0}", e);
                Config = new PixelReelConfig();
            }
        }
    }
}
