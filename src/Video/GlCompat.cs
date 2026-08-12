using System;
using System.Runtime.InteropServices;

namespace PixelReel.Video
{
    /// <summary>
    /// Just enough OpenGL to fix texture filtering on the video texture.
    ///
    /// Why this exists: Vintage Story's LoadOrUpdateTextureFromBgra builds a mipmap
    /// chain when it first creates a texture, but subsequent updates only rewrite
    /// mip level 0. For a video texture that means levels 1..n keep the very first
    /// frame forever, and the GPU picks a level based on screen-space texel density.
    /// Result: live video up close, a frozen first frame from a few blocks away, and
    /// a blend of the two in between. Setting the minification filter to LINEAR makes
    /// the sampler always read level 0, so the picture stays live at any distance.
    ///
    /// glBindTexture and glTexParameteri are both OpenGL 1.1, so they're exported
    /// directly from the system GL library on every platform. That means no OpenTK
    /// reference and no version-matching against whatever OpenTK the game ships.
    /// </summary>
    public static class GlCompat
    {
        private const int GL_TEXTURE_2D = 0x0DE1;
        private const int GL_TEXTURE_MIN_FILTER = 0x2801;
        private const int GL_TEXTURE_MAG_FILTER = 0x2800;
        private const int GL_TEXTURE_WRAP_S = 0x2802;
        private const int GL_TEXTURE_WRAP_T = 0x2803;
        private const int GL_LINEAR = 0x2601;
        private const int GL_CLAMP_TO_EDGE = 0x812F;
        private const int GL_TEXTURE_BINDING_2D = 0x8069;

        public static bool Failed { get; private set; }
        public static string FailureReason { get; private set; }

        /// <summary>
        /// Must be called on the render thread, with the GL context current.
        /// Preserves the previously bound texture so we don't disturb the game's state.
        /// </summary>
        public static void MakeVideoFiltered(int textureId)
        {
            if (Failed || textureId == 0) return;

            try
            {
                int previous = 0;
                Win.glGetIntegerv(GL_TEXTURE_BINDING_2D, ref previous);

                Win.glBindTexture(GL_TEXTURE_2D, textureId);
                Win.glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
                Win.glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
                Win.glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
                Win.glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

                Win.glBindTexture(GL_TEXTURE_2D, previous);
            }
            catch (DllNotFoundException e)
            {
                Failed = true;
                FailureReason = "GL library not found: " + e.Message;
            }
            catch (EntryPointNotFoundException e)
            {
                Failed = true;
                FailureReason = "GL entry point missing: " + e.Message;
            }
            catch (Exception e)
            {
                Failed = true;
                FailureReason = e.Message;
            }
        }

        // Windows exports these from opengl32.dll; the mono/Linux and macOS runtimes
        // resolve "opengl32" through their own probing, and DllImport falls back to
        // the platform GL library. If resolution fails we degrade gracefully above.
        private static class Win
        {
            [DllImport("opengl32.dll", EntryPoint = "glBindTexture")]
            private static extern void glBindTextureWin(int target, int texture);

            [DllImport("opengl32.dll", EntryPoint = "glTexParameteri")]
            private static extern void glTexParameteriWin(int target, int pname, int param);

            [DllImport("opengl32.dll", EntryPoint = "glGetIntegerv")]
            private static extern void glGetIntegervWin(int pname, ref int data);

            [DllImport("libGL.so.1", EntryPoint = "glBindTexture")]
            private static extern void glBindTextureNix(int target, int texture);

            [DllImport("libGL.so.1", EntryPoint = "glTexParameteri")]
            private static extern void glTexParameteriNix(int target, int pname, int param);

            [DllImport("libGL.so.1", EntryPoint = "glGetIntegerv")]
            private static extern void glGetIntegervNix(int pname, ref int data);

            private static readonly bool onWindows =
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            public static void glBindTexture(int target, int texture)
            {
                if (onWindows) glBindTextureWin(target, texture);
                else glBindTextureNix(target, texture);
            }

            public static void glTexParameteri(int target, int pname, int param)
            {
                if (onWindows) glTexParameteriWin(target, pname, param);
                else glTexParameteriNix(target, pname, param);
            }

            public static void glGetIntegerv(int pname, ref int data)
            {
                if (onWindows) glGetIntegervWin(pname, ref data);
                else glGetIntegervNix(pname, ref data);
            }
        }
    }
}
