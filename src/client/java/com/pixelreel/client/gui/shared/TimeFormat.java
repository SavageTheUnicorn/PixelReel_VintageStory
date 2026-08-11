package com.pixelreel.client.gui.shared;

/** added movie duration and where you are in the movie only works for plex at the moment
 * needs work for jellyfin and emby*/
public final class TimeFormat {
	private TimeFormat() {
	}

	public static String format(long ms) {
		long totalSec = Math.max(0L, ms / 1000L);
		long hours = totalSec / 3600L;
		long minutes = (totalSec % 3600L) / 60L;
		long seconds = totalSec % 60L;
		if (hours > 0L) {
			return String.format("%d:%02d:%02d", hours, minutes, seconds);
		}
		return String.format("%d:%02d", minutes, seconds);
	}
}
