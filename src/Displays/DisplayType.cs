using System;
using System.Collections.Generic;

namespace PixelReel.Displays
{
    /// <summary>
    /// The five display sizes, ported from DisplayType.java. C# enums can't carry
    /// data, so this is a class with static instances looked up by code part.
    ///
    /// All the screen bounds are in 16ths of a block ("pixels"), matching both the
    /// original mod and Vintage Story's own shape convention, so the numbers are
    /// copied across unchanged.
    /// </summary>
    public sealed class DisplayType
    {
        public static readonly DisplayType CompactTelevision =
            new DisplayType("compacttelevision", 3, 2, 0f, 0f, 48f, 32f, 1f, 0f, 1, 16f);

        public static readonly DisplayType WallTelevision =
            new DisplayType("walltelevision", 6, 4, 0f, 0f, 96f, 64f, 1f, 0f, 1, 24f);

        public static readonly DisplayType UltrawideMonitor =
            new DisplayType("ultrawidemonitor", 8, 4, 0f, 0f, 128f, 64f, 1f, 0f, 1, 24f);

        public static readonly DisplayType CinemaScreen =
            new DisplayType("cinemascreen", 14, 8, 0f, 0f, 224f, 128f, 1f, 0f, 1, 80f);

        public static readonly DisplayType CurvedCinemaScreen =
            new DisplayType("curvedcinemascreen", 16, 7, 0f, 0f, 256f, 112f, 15.2f, 22f, 32, 80f);

        public static readonly DisplayType[] All =
        {
            CompactTelevision, WallTelevision, UltrawideMonitor, CinemaScreen, CurvedCinemaScreen
        };

        private static readonly Dictionary<string, DisplayType> byId = BuildIndex();

        private static Dictionary<string, DisplayType> BuildIndex()
        {
            Dictionary<string, DisplayType> map = new Dictionary<string, DisplayType>(StringComparer.OrdinalIgnoreCase);
            foreach (DisplayType t in All) map[t.Id] = t;
            return map;
        }

        public static DisplayType FromId(string id)
        {
            if (id != null && byId.TryGetValue(id, out DisplayType t)) return t;
            return null;
        }

        public string Id { get; }
        public int WidthBlocks { get; }
        public int HeightBlocks { get; }

        public float ScreenLeft { get; }
        public float ScreenBottom { get; }
        public float ScreenRight { get; }
        public float ScreenTop { get; }

        /// <summary>Depth of the screen plane from the block's front face, in 16ths.</summary>
        public float BaseZ { get; }

        /// <summary>How far the centre of a curved screen bows away from the viewer. 0 = flat.</summary>
        public float CurveDepth { get; }

        public int CurveSegments { get; }

        /// <summary>Distance in blocks at which this display's audio reaches zero.</summary>
        public float AudioRange { get; }

        private DisplayType(string id, int widthBlocks, int heightBlocks,
                            float screenLeft, float screenBottom, float screenRight, float screenTop,
                            float baseZ, float curveDepth, int curveSegments, float audioRange)
        {
            Id = id;
            WidthBlocks = widthBlocks;
            HeightBlocks = heightBlocks;
            ScreenLeft = screenLeft;
            ScreenBottom = screenBottom;
            ScreenRight = screenRight;
            ScreenTop = screenTop;
            BaseZ = baseZ;
            CurveDepth = curveDepth;
            CurveSegments = curveSegments;
            AudioRange = audioRange;
        }

        public float DisplayWidthPx => WidthBlocks * 16f;
        public float DisplayHeightPx => HeightBlocks * 16f;
        public bool IsCurved => CurveDepth > 0f;
        public bool IsCinema => this == CinemaScreen || this == CurvedCinemaScreen;

        /// <summary>
        /// Aspect of the usable screen area. For a curved screen this uses arc length,
        /// not chord length, so video isn't horizontally squashed by the curve.
        /// </summary>
        public float ScreenAspect
        {
            get
            {
                float height = ScreenTop - ScreenBottom;
                if (height <= 0f) return 1f;
                return ArcLengthPx(ScreenLeft, ScreenRight) / height;
            }
        }

        /// <summary>
        /// Screen depth at horizontal fraction u (0..1). A parabola, which is a good
        /// enough stand-in for the gentle arc of a real curved screen.
        /// </summary>
        public float DepthAt(float u)
        {
            if (!IsCurved) return BaseZ;
            float t = u * 2f - 1f;
            // Concave: the centre is recessed and the edges wrap toward the viewer,
            // like a real curved cinema screen. (Negating this gives a convex bulge,
            // which is what the first pass did and looked inside-out.)
            return BaseZ - CurveDepth + CurveDepth * t * t;
        }

        /// <summary>Length of the screen surface between two horizontal positions, in 16ths.</summary>
        public float ArcLengthPx(float fromD, float toD)
        {
            float start = Math.Min(fromD, toD);
            float end = Math.Max(fromD, toD);
            if (end <= start) return 0f;
            if (!IsCurved) return end - start;

            int samples = Math.Max(8, (int)Math.Round((end - start) / 2f));
            float width = DisplayWidthPx;
            float length = 0f;
            float prevD = start;
            float prevZ = DepthAt(start / width);

            for (int i = 1; i <= samples; i++)
            {
                float d = start + (end - start) * i / samples;
                float z = DepthAt(d / width);
                float dx = d - prevD;
                float dz = z - prevZ;
                length += (float)Math.Sqrt(dx * dx + dz * dz);
                prevD = d;
                prevZ = z;
            }
            return length;
        }

        /// <summary>
        /// How far in from each edge to inset so the visible area matches targetAspect.
        /// Binary search because arc length has no closed-form inverse on a parabola.
        /// </summary>
        public float PillarboxInsetForAspect(float left, float right, float height, float targetAspect)
        {
            if (height <= 0f || targetAspect <= 0f || right <= left) return 0f;

            float fullArc = ArcLengthPx(left, right);
            float targetWidth = height * targetAspect;
            if (targetWidth >= fullArc) return 0f;

            float lo = 0f;
            float hi = (right - left) * 0.5f;
            for (int i = 0; i < 24; i++)
            {
                float mid = (lo + hi) * 0.5f;
                float arc = ArcLengthPx(left + mid, right - mid);
                if (arc > targetWidth) lo = mid;
                else hi = mid;
            }
            return (lo + hi) * 0.5f;
        }

        public override string ToString() => Id;
    }
}
