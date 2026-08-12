using System;
using PixelReel.BlockEntities;
using PixelReel.Config;
using PixelReel.Video;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace PixelReel.Gui
{
    /// <summary>
    /// Theatre mode, implemented as a GuiDialog rather than an IRenderer.
    ///
    /// The first attempt registered a renderer on the Ortho stage, but the hotbar, chat
    /// and minimap are dialogs drawn by the GUI system, so no RenderOrder could get
    /// above them. Driving the game's own HideGuis flag doesn't work either: it
    /// suppresses the whole Ortho stage, taking the video with it.
    ///
    /// As a dialog with a high DrawOrder we're sorted after the HUD dialogs and simply
    /// paint over them, which is both simpler and uses supported API.
    /// </summary>
    public class GuiDialogTheatre : GuiDialog
    {
        private BlockEntityDisplay target;
        private LoadedTexture backdrop;

        private static PixelReelConfig Config => PixelReelModSystem.Config;

        public GuiDialogTheatre(ICoreClientAPI capi) : base(capi)
        {
        }

        public override string ToggleKeyCombinationCode => null;

        /// <summary>Above the HUD dialogs, so we draw last and cover them.</summary>
        public override double DrawOrder => 1.0;

        /// <summary>Keep the mouse grabbed: this is a video, not something to click.</summary>
        public override bool PrefersUngrabbedMouse => false;

        /// <summary>Don't dim the world behind us; the backdrop already covers it.</summary>
        public override bool DisableMouseGrab => false;

        public bool IsActive => target != null && IsOpened();

        public bool IsWatching(BlockEntityDisplay display)
        {
            return IsActive && target == display;
        }

        /// <summary>Toggles theatre mode for whatever projector the player is looking at.</summary>
        public bool ToggleForLookedAtBlock()
        {
            if (IsActive)
            {
                Close();
                return true;
            }

            BlockSelection sel = capi.World.Player?.CurrentBlockSelection;
            if (sel == null)
            {
                capi.ShowChatMessage("[pixelReel] Look at a projector first.");
                return false;
            }

            return EnterFor(sel.Position);
        }

        public bool EnterFor(BlockPos pos)
        {
            BlockEntityDisplay display =
                capi.World.BlockAccessor.GetBlockEntity(pos) as BlockEntityDisplay;

            if (display == null)
            {
                capi.ShowChatMessage("[pixelReel] That isn't a projector.");
                return false;
            }

            if (!display.HasVideo)
            {
                capi.ShowChatMessage("[pixelReel] That projector isn't playing anything.");
                return false;
            }

            target = display;
            TryOpen();
            return true;
        }

        public void Close()
        {
            target = null;
            TryClose();
        }

        public override void OnRenderGUI(float deltaTime)
        {
            base.OnRenderGUI(deltaTime);

            if (target == null) return;

            // The projector was broken, unloaded or stopped while we were watching.
            if (!target.HasVideo)
            {
                Close();
                return;
            }

            VideoTexture tex = target.Texture;
            if (tex == null || !tex.UploadIfDirty()) return;

            float videoAspect = tex.FrameAspect;
            if (videoAspect <= 0.01f) return;

            float screenW = capi.Render.FrameWidth;
            float screenH = capi.Render.FrameHeight;
            if (screenW <= 0 || screenH <= 0) return;

            float drawW = screenW;
            float drawH = screenW / videoAspect;
            if (drawH > screenH)
            {
                drawH = screenH;
                drawW = screenH * videoAspect;
            }

            float x = (screenW - drawW) * 0.5f;
            float y = (screenH - drawH) * 0.5f;

            IRenderAPI rpi = capi.Render;
            rpi.GlToggleBlend(false);

            if (Config.FullscreenHidesHud)
            {
                EnsureBackdrop();
                if (backdrop != null && backdrop.TextureId != 0)
                {
                    rpi.Render2DTexture(backdrop.TextureId, 0, 0, screenW, screenH, 500f);
                }
            }

            rpi.Render2DTexture(tex.TextureId, x, y, drawW, drawH, 501f);
            rpi.GlToggleBlend(true);
        }

        private void EnsureBackdrop()
        {
            if (backdrop != null && backdrop.TextureId != 0) return;

            try
            {
                int[] pixels = new int[4];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = unchecked((int)0xFF000000);

                backdrop = new LoadedTexture(capi, 0, 2, 2);
                capi.Render.LoadOrUpdateTextureFromBgra(pixels, false, 1, ref backdrop);
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[pixelReel] could not build theatre backdrop: " + e.Message);
                backdrop = null;
            }
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            target = null;
        }

        public override void Dispose()
        {
            base.Dispose();
            backdrop?.Dispose();
            backdrop = null;
        }
    }
}
