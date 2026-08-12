using System;
using System.Collections.Generic;
using PixelReel.Displays;
using PixelReel.Video;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace PixelReel.Render
{
    /// <summary>
    /// Draws the video surface for one display, at any of the five sizes, flat or curved.
    ///
    /// The mesh is built in the controller block's local space and rebuilt only when
    /// the video's aspect ratio or the facing changes, so per-frame cost is one texture
    /// bind and one draw call regardless of screen size.
    /// </summary>
    public class DisplayRenderer : IRenderer
    {
        private readonly ICoreClientAPI capi;
        private readonly BlockPos pos;
        private readonly VideoTexture videoTexture;
        private readonly DisplayType type;
        private readonly float brightness;
        private readonly float verticalOffset;
        private readonly float forwardOffset;

        private BlockFacing facing;
        private MeshRef quadRef;
        private readonly Matrixf modelMat = new Matrixf();

        private float builtForAspect = -1f;
        private BlockFacing builtForFacing;

        public bool Active;

        public double RenderOrder => 0.5;

        /// <summary>Cinema screens stay visible from far off; a small TV needn't.</summary>
        public int RenderRange => type != null && type.IsCinema ? 256 : 96;

        public DisplayRenderer(ICoreClientAPI capi, BlockPos pos, VideoTexture videoTexture,
                               DisplayType type, BlockFacing facing, float brightness,
                               float verticalOffset, float forwardOffset)
        {
            this.capi = capi;
            this.pos = pos;
            this.videoTexture = videoTexture;
            this.type = type;
            this.facing = facing;
            this.brightness = brightness;
            this.verticalOffset = verticalOffset;
            this.forwardOffset = forwardOffset;
        }

        public void SetFacing(BlockFacing facing)
        {
            if (this.facing != facing)
            {
                this.facing = facing;
                DisposeQuad();
            }
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (!Active) return;
            if (!videoTexture.UploadIfDirty()) return;

            float aspect = videoTexture.FrameAspect;
            if (aspect <= 0.01f) return;

            if (quadRef == null || Math.Abs(aspect - builtForAspect) > 0.001f || builtForFacing != facing)
            {
                RebuildMesh(aspect);
                if (quadRef == null) return;
            }

            IRenderAPI rpi = capi.Render;
            Vec3d camPos = capi.World.Player.Entity.CameraPos;

            rpi.GlDisableCullFace();
            rpi.GlToggleBlend(false);

            IStandardShaderProgram prog = rpi.PreparedStandardShader(pos.X, pos.Y, pos.Z);
            prog.Tex2D = videoTexture.TextureId;

            // Self-lit: the screen's own colours, not modulated by room light.
            float b = GameMath.Clamp(brightness, 0.1f, 1.0f);
            prog.RgbaLightIn = new Vec4f(b, b, b, 1f);
            prog.RgbaAmbientIn = new Vec3f(b, b, b);
            prog.RgbaTint = new Vec4f(1f, 1f, 1f, 1f);

            // ExtraGlow feeds the bloom pass and smears the picture. Keep it off.
            prog.ExtraGlow = 0;

            // Fog would blend the picture toward the fog colour by camera distance,
            // which washes out a screen you are deliberately looking at.
            prog.FogDensityIn = 0f;
            prog.FogMinIn = 0f;
            prog.RgbaFogIn = new Vec4f(0f, 0f, 0f, 0f);

            prog.NormalShaded = 0;
            prog.DontWarpVertices = 1;
            prog.AlphaTest = 0f;

            prog.ModelMatrix = modelMat
                .Identity()
                .Translate(pos.X - camPos.X, pos.Y - camPos.Y, pos.Z - camPos.Z)
                .Values;
            prog.ViewMatrix = rpi.CameraMatrixOriginf;
            prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;

            rpi.RenderMesh(quadRef);
            prog.Stop();

            rpi.GlEnableCullFace();
        }

        /// <summary>
        /// Builds the screen surface: one quad for flat screens, a strip of
        /// CurveSegments quads for curved ones.
        ///
        /// The video is fitted inside the screen area preserving its own aspect ratio,
        /// letterboxed or pillarboxed as needed, never stretched. For curved screens
        /// the horizontal fit is computed against arc length rather than chord length,
        /// otherwise the curve would visibly squash the picture.
        /// </summary>
        private void RebuildMesh(float videoAspect)
        {
            DisposeQuad();

            float screenH = type.ScreenTop - type.ScreenBottom;
            if (screenH <= 0.01f) return;

            float left = type.ScreenLeft;
            float right = type.ScreenRight;
            float bottom = type.ScreenBottom;
            float top = type.ScreenTop;

            float screenAspect = type.ScreenAspect;

            if (videoAspect > screenAspect)
            {
                // Video is wider than the screen: letterbox by shrinking height.
                float arc = type.ArcLengthPx(left, right);
                float wantedH = arc / videoAspect;
                float inset = (screenH - wantedH) * 0.5f;
                bottom += inset;
                top -= inset;
            }
            else
            {
                // Video is taller: pillarbox by insetting the sides. Solved against arc
                // length so a curved screen doesn't distort the fit.
                float inset = type.PillarboxInsetForAspect(left, right, screenH, videoAspect);
                left += inset;
                right -= inset;
            }

            if (right - left < 0.01f || top - bottom < 0.01f) return;

            Vec3f normal = new Vec3f(facing.Normalf.X, facing.Normalf.Y, facing.Normalf.Z);
            Vec3f rightVec = new Vec3f(normal.Z, 0f, -normal.X);
            Vec3f up = new Vec3f(0f, 1f, 0f);

            // The projected screen is centred horizontally on its own block, so a
            // projector always throws its picture symmetrically about itself
            // regardless of how wide the screen is.
            float centrePx = type.DisplayWidthPx * 0.5f;

            int segments = type.IsCurved ? Math.Max(1, type.CurveSegments) : 1;

            MeshData mesh = new MeshData((segments + 1) * 2, segments * 6, false, true, true, true);
            int white = ColorUtil.WhiteArgb;

            float yBottom = bottom / 16f + verticalOffset;
            float yTop = top / 16f + verticalOffset;
            float displayWidth = type.DisplayWidthPx;

            // Small standoff so the picture sits proud of the block face rather than
            // z-fighting with it.
            const float standoffPx = 0.5f;

            for (int i = 0; i < segments; i++)
            {
                float d0 = left + (right - left) * i / segments;
                float d1 = left + (right - left) * (i + 1) / segments;

                float u0 = (float)i / segments;
                float u1 = (float)(i + 1) / segments;

                float z0 = (type.DepthAt(d0 / displayWidth) + standoffPx) / 16f + forwardOffset;
                float z1 = (type.DepthAt(d1 / displayWidth) + standoffPx) / 16f + forwardOffset;

                float x0 = (d0 - centrePx) / 16f;
                float x1 = (d1 - centrePx) / 16f;

                Vec3f bl = Corner(rightVec, up, normal, x0, yBottom, z0);
                Vec3f br = Corner(rightVec, up, normal, x1, yBottom, z1);
                Vec3f tr = Corner(rightVec, up, normal, x1, yTop, z1);
                Vec3f tl = Corner(rightVec, up, normal, x0, yTop, z0);

                int baseIndex = mesh.VerticesCount;
                mesh.AddVertex(bl.X, bl.Y, bl.Z, u0, 1f, white);
                mesh.AddVertex(br.X, br.Y, br.Z, u1, 1f, white);
                mesh.AddVertex(tr.X, tr.Y, tr.Z, u1, 0f, white);
                mesh.AddVertex(tl.X, tl.Y, tl.Z, u0, 0f, white);
                mesh.AddQuadIndices(baseIndex);
            }

            try
            {
                quadRef = capi.Render.UploadMesh(mesh);
                builtForAspect = videoAspect;
                builtForFacing = facing;
            }
            catch (Exception e)
            {
                capi.Logger.Error("[pixelReel] failed to upload screen mesh for {0}: {1}", type.Id, e);
                quadRef = null;
            }
        }

        /// <summary>
        /// Converts display-local coordinates (across, up, out-from-face) into the
        /// controller block's local space. Centring on the block's mid-line keeps a
        /// screen aligned with the block grid whichever way it faces.
        /// </summary>
        private static Vec3f Corner(Vec3f rightVec, Vec3f up, Vec3f normal,
                                    float across, float upAmount, float outward)
        {
            Vec3f centre = new Vec3f(0.5f, 0f, 0.5f);
            return centre
                 + rightVec * across
                 + up * upAmount
                 + normal * outward;
        }

        private void DisposeQuad()
        {
            quadRef?.Dispose();
            quadRef = null;
            builtForAspect = -1f;
            builtForFacing = null;
        }

        public void Dispose()
        {
            DisposeQuad();
        }
    }
}
