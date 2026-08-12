using System;
using System.Runtime.InteropServices;
using Vintagestory.API.Client;

namespace PixelReel.Video
{
    /// <summary>
    /// The bridge between the libvlc decode thread and the GL thread.
    ///
    /// Decode thread calls <see cref="Submit"/> with a native BGRA buffer.
    /// Render thread calls <see cref="UploadIfDirty"/> once per frame.
    ///
    /// We ask libvlc for BGRA specifically because that is exactly what
    /// IRenderAPI.LoadOrUpdateTextureFromBgra wants, so there is no channel
    /// swizzle anywhere in the hot path. (The Fabric mod uses RGBA and pays for it.)
    /// </summary>
    public class VideoTexture : IDisposable
    {
        private readonly ICoreClientAPI capi;
        private readonly object sync = new object();

        // Staging buffer, one int per pixel, reused across frames. Never allocate per frame.
        private int[] staging;
        private int stagingWidth;
        private int stagingHeight;
        private bool dirty;
        private bool disposed;
        private long framesSubmitted;
        private int filterAppliedCount;

        private LoadedTexture texture;
        private int textureWidth;
        private int textureHeight;

        public VideoTexture(ICoreClientAPI capi)
        {
            this.capi = capi;
        }

        public int FrameWidth { get { lock (sync) return stagingWidth; } }
        public int FrameHeight { get { lock (sync) return stagingHeight; } }
        public bool HasFrame { get { lock (sync) return stagingWidth > 0 && stagingHeight > 0; } }

        /// <summary>Total frames handed over by the decoder. If this stops climbing, playback stalled.</summary>
        public long FramesSubmitted { get { lock (sync) return framesSubmitted; } }

        /// <summary>True once texture filtering was successfully forced to non-mipmapped.</summary>
        public bool FilterApplied => filterAppliedCount > 0 && !GlCompat.Failed;

        public float FrameAspect
        {
            get
            {
                lock (sync)
                {
                    if (stagingWidth <= 0 || stagingHeight <= 0) return 0f;
                    return (float)stagingWidth / stagingHeight;
                }
            }
        }

        /// <summary>GL texture id, or 0 if nothing has been uploaded yet.</summary>
        public int TextureId => texture?.TextureId ?? 0;

        /// <summary>Called from the libvlc decode thread. Must be cheap and must not touch GL.</summary>
        public void Submit(IntPtr source, int width, int height)
        {
            if (source == IntPtr.Zero || width <= 0 || height <= 0) return;

            lock (sync)
            {
                if (disposed) return;

                int pixels = width * height;
                if (staging == null || stagingWidth != width || stagingHeight != height)
                {
                    staging = new int[pixels];
                    stagingWidth = width;
                    stagingHeight = height;
                }

                // Marshal.Copy on int[] copies 4 bytes per element, so this is a
                // straight memcpy of the whole BGRA frame.
                Marshal.Copy(source, staging, 0, pixels);
                dirty = true;
                framesSubmitted++;
            }
        }

        /// <summary>Called on the render thread. Returns true when a texture is ready to bind.</summary>
        public bool UploadIfDirty()
        {
            int w, h;
            lock (sync)
            {
                if (disposed || staging == null) return texture != null && texture.TextureId != 0;
                w = stagingWidth;
                h = stagingHeight;
            }

            if (texture == null || textureWidth != w || textureHeight != h)
            {
                Recreate(w, h);
                if (texture == null) return false;
            }

            lock (sync)
            {
                if (disposed || staging == null) return texture.TextureId != 0;
                if (!dirty) return texture.TextureId != 0;

                // linearMag: true gives smooth scaling, which is what you want for
                // video. clampMode 1 = clamp to edge, avoids edge bleed on the quad.
                capi.Render.LoadOrUpdateTextureFromBgra(staging, true, 1, ref texture);
                dirty = false;
            }

            // Must happen after the first upload, because that's when VS actually
            // creates the GL texture object (and its mip chain). Re-applied for a
            // few frames in case an update regenerates mipmaps and resets filtering.
            if (filterAppliedCount < 5 && texture.TextureId != 0)
            {
                GlCompat.MakeVideoFiltered(texture.TextureId);
                filterAppliedCount++;
            }

            return texture.TextureId != 0;
        }

        private void Recreate(int w, int h)
        {
            Release();
            try
            {
                // LoadOrUpdateTextureFromBgra reads Width/Height off the LoadedTexture,
                // so the dimensions have to be set here rather than passed in.
                texture = new LoadedTexture(capi, 0, w, h);
                textureWidth = w;
                textureHeight = h;
                filterAppliedCount = 0;
            }
            catch (Exception e)
            {
                capi.Logger.Error("[pixelReel] failed to allocate {0}x{1} video texture: {2}", w, h, e);
                texture = null;
            }
        }

        private void Release()
        {
            texture?.Dispose();
            texture = null;
            textureWidth = 0;
            textureHeight = 0;
        }

        public void Dispose()
        {
            lock (sync)
            {
                disposed = true;
                staging = null;
                stagingWidth = 0;
                stagingHeight = 0;
                dirty = false;
            }
            Release();
        }
    }
}
