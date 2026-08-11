package com.pixelreel.client.playback.video;

import java.nio.ByteBuffer;
import org.lwjgl.system.MemoryUtil;

/**letterbox detector */
final class MatteDetector {
	private static final int LUMA_LIMIT = 14;
	private static final float BLACK_ROW_RATIO = 0.985F;
	private static final float MIN_BAR_FRAC = 0.04F;
	private static final float MIN_CONTENT_FRAC = 0.55F;
	private static final int SAMPLE_STRIDE = 6;
	private static final int DARK_FRAME_LUMA = 28;

	private MatteDetector() {
	}

	record Bounds(float u0, float v0, float u1, float v1) {
		static final Bounds FULL = new Bounds(0.0F, 0.0F, 1.0F, 1.0F);

		float widthFrac() {
			return this.u1 - this.u0;
		}

		float heightFrac() {
			return this.v1 - this.v0;
		}

		boolean isFull() {
			return this.u0 <= 0.001F && this.v0 <= 0.001F && this.u1 >= 0.999F && this.v1 >= 0.999F;
		}
	}

	static Bounds detect(ByteBuffer rgba, int width, int height) {
		if (rgba == null || !rgba.isDirect() || width < 16 || height < 16) {
			return Bounds.FULL;
		}
		int bytes = width * height * 4;
		if (rgba.remaining() < bytes) {
			return Bounds.FULL;
		}
		long base = MemoryUtil.memAddress(rgba);

		int top = 0;
		while (top < height && isBlackBand(base, width, height, top, true)) {
			top++;
		}
		int bottom = height - 1;
		while (bottom > top && isBlackBand(base, width, height, bottom, true)) {
			bottom--;
		}

		int contentH = bottom - top + 1;
		if (contentH < height * MIN_CONTENT_FRAC) {
			return Bounds.FULL;
		}
		float topFrac = top / (float)height;
		float bottomFrac = (height - 1 - bottom) / (float)height;
		if (topFrac < MIN_BAR_FRAC || bottomFrac < MIN_BAR_FRAC) {
			return Bounds.FULL;
		}
		if (Math.abs(topFrac - bottomFrac) > 0.08F) {
			return Bounds.FULL;
		}

		return new Bounds(0.0F, top / (float)height, 1.0F, (bottom + 1) / (float)height);
	}

	static boolean isTooDark(ByteBuffer rgba, int width, int height) {
		if (rgba == null || !rgba.isDirect() || width < 16 || height < 16 || rgba.remaining() < width * height * 4) {
			return true;
		}
		long base = MemoryUtil.memAddress(rgba);
		int x0 = width / 4;
		int x1 = width - width / 4;
		int y0 = height / 4;
		int y1 = height - height / 4;
		long sum = 0;
		int samples = 0;
		for (int y = y0; y < y1; y += SAMPLE_STRIDE) {
			for (int x = x0; x < x1; x += SAMPLE_STRIDE) {
				long offset = base + ((long)y * width + x) * 4L;
				int r = MemoryUtil.memGetByte(offset) & 0xFF;
				int g = MemoryUtil.memGetByte(offset + 1L) & 0xFF;
				int b = MemoryUtil.memGetByte(offset + 2L) & 0xFF;
				sum += Math.max(r, Math.max(g, b));
				samples++;
			}
		}
		return samples == 0 || (sum / samples) < DARK_FRAME_LUMA;
	}

	private static boolean isBlackBand(long base, int width, int height, int index, boolean horizontal) {
		int samples = 0;
		int black = 0;
		if (horizontal) {
			int y = index;
			for (int x = 0; x < width; x += SAMPLE_STRIDE) {
				samples++;
				if (isBlackPixel(base, width, x, y)) {
					black++;
				}
			}
		} else {
			int x = index;
			for (int y = 0; y < height; y += SAMPLE_STRIDE) {
				samples++;
				if (isBlackPixel(base, width, x, y)) {
					black++;
				}
			}
		}
		return samples > 0 && black >= samples * BLACK_ROW_RATIO;
	}

	private static boolean isBlackPixel(long base, int width, int x, int y) {
		long offset = base + ((long)y * width + x) * 4L;
		int r = MemoryUtil.memGetByte(offset) & 0xFF;
		int g = MemoryUtil.memGetByte(offset + 1L) & 0xFF;
		int b = MemoryUtil.memGetByte(offset + 2L) & 0xFF;
		return Math.max(r, Math.max(g, b)) <= LUMA_LIMIT;
	}
}
