package com.pixelreel.ondemand;

import com.pixelreel.media.MediaSource;
import java.util.Locale;

/** library backends */
public enum OnDemandProvider {
	JELLYFIN,
	EMBY,
	PLEX;

	private static final OnDemandProvider[] VALUES = values();

	public static OnDemandProvider byIndex(int index) {
		return index >= 0 && index < VALUES.length ? VALUES[index] : JELLYFIN;
	}

	public static OnDemandProvider byName(String raw) {
		if (raw == null || raw.isBlank()) {
			return JELLYFIN;
		}
		try {
			return OnDemandProvider.valueOf(raw.trim().toUpperCase(Locale.ROOT));
		} catch (IllegalArgumentException e) {
			return JELLYFIN;
		}
	}

	public MediaSource toMediaSource() {
		return switch (this) {
			case JELLYFIN -> MediaSource.JELLYFIN;
			case EMBY -> MediaSource.EMBY;
			case PLEX -> MediaSource.PLEX;
		};
	}

	public static OnDemandProvider fromMediaSource(MediaSource source) {
		return switch (source) {
			case EMBY -> EMBY;
			case PLEX -> PLEX;
			default -> JELLYFIN;
		};
	}

	public String displayName() {
		return switch (this) {
			case JELLYFIN -> "Jellyfin";
			case EMBY -> "Emby";
			case PLEX -> "Plex";
		};
	}
}
