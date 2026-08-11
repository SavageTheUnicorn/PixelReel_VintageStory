package com.pixelreel.client.playback.subtitle;

import java.awt.Color;
import java.awt.Font;
import java.awt.FontMetrics;
import java.awt.Graphics2D;
import java.awt.RenderingHints;
import java.awt.image.BufferedImage;
import java.nio.ByteBuffer;
import org.lwjgl.system.MemoryUtil;

/** burns subtitle. */
public final class SubtitleRasterizer {
	private SubtitleRasterizer() {
	}

	public static void burnIn(ByteBuffer rgba, int width, int height, String text) {
		if (rgba == null || !rgba.isDirect() || width < 32 || height < 32 || text == null || text.isBlank()) {
			return;
		}
		int bytes = width * height * 4;
		if (rgba.remaining() < bytes) {
			return;
		}
		try {
			int fontSize = Math.max(16, Math.min(48, height / 14));
			BufferedImage image = new BufferedImage(width, height, BufferedImage.TYPE_INT_ARGB);
			Graphics2D g = image.createGraphics();
			try {
				g.setRenderingHint(RenderingHints.KEY_TEXT_ANTIALIASING, RenderingHints.VALUE_TEXT_ANTIALIAS_ON);
				g.setFont(new Font("SansSerif", Font.BOLD, fontSize));
				FontMetrics metrics = g.getFontMetrics();
				String[] lines = text.split("\\n");
				int lineHeight = metrics.getHeight();
				int blockHeight = lineHeight * lines.length;
				int y = height - Math.max(height / 12, 16) - blockHeight + metrics.getAscent();
				for (String line : lines) {
					String trimmed = line.strip();
					if (trimmed.isEmpty()) {
						y += lineHeight;
						continue;
					}
					int x = Math.max(4, (width - metrics.stringWidth(trimmed)) / 2);
					g.setColor(Color.BLACK);
					g.drawString(trimmed, x - 2, y);
					g.drawString(trimmed, x + 2, y);
					g.drawString(trimmed, x, y - 2);
					g.drawString(trimmed, x, y + 2);
					g.setColor(Color.WHITE);
					g.drawString(trimmed, x, y);
					y += lineHeight;
				}
			} finally {
				g.dispose();
			}
			long base = MemoryUtil.memAddress(rgba);
			int[] pixels = new int[width];
			for (int row = Math.max(0, height - blockBottom(height, fontSize, text)); row < height; row++) {
				image.getRGB(0, row, width, 1, pixels, 0, width);
				boolean any = false;
				for (int px : pixels) {
					if (((px >>> 24) & 0xFF) > 8) {
						any = true;
						break;
					}
				}
				if (!any) {
					continue;
				}
				for (int col = 0; col < width; col++) {
					int argb = pixels[col];
					int a = (argb >>> 24) & 0xFF;
					if (a < 8) {
						continue;
					}
					int sr = (argb >>> 16) & 0xFF;
					int sg = (argb >>> 8) & 0xFF;
					int sb = argb & 0xFF;
					long p = base + ((long)row * width + col) * 4L;
					if (a >= 250) {
						MemoryUtil.memPutByte(p, (byte)sr);
						MemoryUtil.memPutByte(p + 1, (byte)sg);
						MemoryUtil.memPutByte(p + 2, (byte)sb);
					} else {
						int dr = MemoryUtil.memGetByte(p) & 0xFF;
						int dg = MemoryUtil.memGetByte(p + 1) & 0xFF;
						int db = MemoryUtil.memGetByte(p + 2) & 0xFF;
						MemoryUtil.memPutByte(p, (byte)((sr * a + dr * (255 - a)) / 255));
						MemoryUtil.memPutByte(p + 1, (byte)((sg * a + dg * (255 - a)) / 255));
						MemoryUtil.memPutByte(p + 2, (byte)((sb * a + db * (255 - a)) / 255));
					}
				}
			}
		} catch (Throwable ignored) {
			// i know i know left this catch empty leaving this empty so it doesnt break the video 
		}
	}

	private static int blockBottom(int height, int fontSize, String text) {
		int lines = Math.max(1, text.split("\\n").length);
		return Math.max(height / 12, 16) + (fontSize + 8) * lines;
	}
}
