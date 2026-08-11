package com.pixelreel.jellyfin;

import java.util.Locale;

public enum JellyfinItemKind {
	MOVIE,
	SERIES,
	SEASON,
	EPISODE,
	UNKNOWN;

	public static JellyfinItemKind fromApi(String type) {
		if (type == null || type.isBlank()) {
			return UNKNOWN;
		}
		return switch (type.trim().toLowerCase(Locale.ROOT)) {
			case "movie" -> MOVIE;
			case "series" -> SERIES;
			case "season" -> SEASON;
			case "episode" -> EPISODE;
			default -> UNKNOWN;
		};
	}
}
