package com.pixelreel.media;

import java.util.Locale;

/** What kind of content screen is currently showing. */
public enum MediaSource {
	TUNARR,
	JELLYFIN,
	EMBY,
	PLEX;

	public static MediaSource byName(String raw) {
		if (raw == null || raw.isBlank()) {
			return TUNARR;
		}
		try {
			return MediaSource.valueOf(raw.trim().toUpperCase(Locale.ROOT));
		} catch (IllegalArgumentException e) {
			return TUNARR;
		}
	}

	public boolean isOnDemand() {
		return this == JELLYFIN || this == EMBY || this == PLEX;
	}
}
