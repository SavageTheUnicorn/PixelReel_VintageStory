package com.pixelreel.ondemand;

import com.pixelreel.blockentities.DisplayBlockEntity;
import com.pixelreel.config.ConfigManager;
import com.pixelreel.config.PixelReelConfig;
import com.pixelreel.emby.EmbyService;
import com.pixelreel.jellyfin.JellyfinItemSummary;
import com.pixelreel.jellyfin.JellyfinLibrary;
import com.pixelreel.jellyfin.JellyfinService;
import com.pixelreel.jellyfin.JellyfinStatus;
import com.pixelreel.media.SubtitleTrack;
import com.pixelreel.networking.ModNetworkPayloads.BrowseKind;
import com.pixelreel.plex.PlexService;
import java.util.ArrayList;
import java.util.List;
import java.util.Optional;
import java.util.concurrent.CompletableFuture;

/** routes content requests to Jellyfin, Emby, or Plex */
public final class OnDemandCatalog {
	private OnDemandCatalog() {
	}

	public static boolean isConfigured(OnDemandProvider provider) {
		PixelReelConfig config = ConfigManager.get();
		return switch (provider) {
			case JELLYFIN -> config.isJellyfinConfigured();
			case EMBY -> config.isEmbyConfigured();
			case PLEX -> config.isPlexConfigured();
		};
	}

	public static JellyfinStatus lastStatus(OnDemandProvider provider) {
		return switch (provider) {
			case JELLYFIN -> JellyfinService.INSTANCE.lastStatus();
			case EMBY -> EmbyService.INSTANCE.lastStatus();
			case PLEX -> PlexService.INSTANCE.lastStatus();
		};
	}

	public static CompletableFuture<JellyfinStatus> refresh(OnDemandProvider provider, boolean force) {
		return switch (provider) {
			case JELLYFIN -> JellyfinService.INSTANCE.refresh(force);
			case EMBY -> EmbyService.INSTANCE.refresh(force);
			case PLEX -> PlexService.INSTANCE.refresh(force);
		};
	}

	public static void invalidateAll() {
		JellyfinService.INSTANCE.invalidateCache();
		EmbyService.INSTANCE.invalidateCache();
		PlexService.INSTANCE.invalidateCache();
	}

	public static void refreshConfigured(boolean force) {
		PixelReelConfig config = ConfigManager.get();
		if (config.isJellyfinConfigured()) {
			JellyfinService.INSTANCE.refresh(force);
		}
		if (config.isEmbyConfigured()) {
			EmbyService.INSTANCE.refresh(force);
		}
		if (config.isPlexConfigured()) {
			PlexService.INSTANCE.refresh(force);
		}
	}

	public static Page page(OnDemandProvider provider, BrowseKind kind, String search, int page) {
		return switch (provider) {
			case JELLYFIN -> {
				JellyfinService.Page p = kind == BrowseKind.SERIES
					? JellyfinService.INSTANCE.pageSeries(search, page)
					: JellyfinService.INSTANCE.pageMovies(search, page);
				yield new Page(p.items(), p.page(), p.totalCount());
			}
			case EMBY -> {
				EmbyService.Page p = kind == BrowseKind.SERIES
					? EmbyService.INSTANCE.pageSeries(search, page)
					: EmbyService.INSTANCE.pageMovies(search, page);
				yield new Page(p.items(), p.page(), p.totalCount());
			}
			case PLEX -> {
				PlexService.Page p = kind == BrowseKind.SERIES
					? PlexService.INSTANCE.pageSeries(search, page)
					: PlexService.INSTANCE.pageMovies(search, page);
				yield new Page(p.items(), p.page(), p.totalCount());
			}
		};
	}

	public static Optional<JellyfinItemSummary> find(OnDemandProvider provider, String itemId) {
		return switch (provider) {
			case JELLYFIN -> JellyfinService.INSTANCE.find(itemId);
			case EMBY -> EmbyService.INSTANCE.find(itemId);
			case PLEX -> PlexService.INSTANCE.find(itemId);
		};
	}

	public static CompletableFuture<Optional<JellyfinItemSummary>> fetchItem(OnDemandProvider provider, String itemId) {
		return switch (provider) {
			case JELLYFIN -> JellyfinService.INSTANCE.fetchItem(itemId);
			case EMBY -> EmbyService.INSTANCE.fetchItem(itemId);
			case PLEX -> PlexService.INSTANCE.fetchItem(itemId);
		};
	}

	public static CompletableFuture<List<JellyfinItemSummary>> seasons(OnDemandProvider provider, String seriesId, boolean force) {
		return switch (provider) {
			case JELLYFIN -> JellyfinService.INSTANCE.seasons(seriesId, force);
			case EMBY -> EmbyService.INSTANCE.seasons(seriesId, force);
			case PLEX -> PlexService.INSTANCE.seasons(seriesId, force);
		};
	}

	public static CompletableFuture<List<JellyfinItemSummary>> episodes(OnDemandProvider provider, String seasonId, boolean force) {
		return switch (provider) {
			case JELLYFIN -> JellyfinService.INSTANCE.episodes(seasonId, force);
			case EMBY -> EmbyService.INSTANCE.episodes(seasonId, force);
			case PLEX -> PlexService.INSTANCE.episodes(seasonId, force);
		};
	}

	public static CompletableFuture<Optional<PlaybackStart>> resolvePlayback(
		OnDemandProvider provider,
		String itemId,
		long startPositionMs
	) {
		return switch (provider) {
			case JELLYFIN -> JellyfinService.INSTANCE.resolvePlayback(itemId, startPositionMs)
				.thenApply(opt -> opt.map(p -> new PlaybackStart(
					p.streamUrl(), p.playSessionId(), p.mediaSourceId(), p.startPositionTicks(), p.hdr(), p.subtitles(), 0, 0, ""
				)));
			case EMBY -> EmbyService.INSTANCE.resolvePlayback(itemId, startPositionMs)
				.thenApply(opt -> opt.map(p -> new PlaybackStart(
					p.streamUrl(), p.playSessionId(), p.mediaSourceId(), p.startPositionTicks(), p.hdr(), p.subtitles(), 0, 0, ""
				)));
			case PLEX -> PlexService.INSTANCE.resolvePlayback(itemId, startPositionMs)
				.thenApply(opt -> opt.map(p -> new PlaybackStart(
					p.streamUrl(),
					p.playSessionId(),
					p.mediaSourceId(),
					p.startPositionTicks(),
					p.hdr(),
					p.subtitles(),
					p.mediaIndex(),
					p.partIndex(),
					p.partKey()
				)));
		};
	}

	public static String subtitleFetchUrl(DisplayBlockEntity display, int subtitleIndex) {
		if (display == null || !display.isOnDemand() || subtitleIndex < 0 || display.getJellyfinItemId().isEmpty()) {
			return "";
		}
		OnDemandProvider provider = display.onDemandProvider();
		if (!isConfigured(provider)) {
			return "";
		}
		try {
			return switch (provider) {
				case JELLYFIN -> JellyfinService.INSTANCE.buildSubtitleUrl(
					display.getJellyfinItemId(), display.getJellyfinMediaSourceId(), subtitleIndex
				);
				case EMBY -> EmbyService.INSTANCE.buildSubtitleUrl(
					display.getJellyfinItemId(), display.getJellyfinMediaSourceId(), subtitleIndex
				);
				case PLEX -> PlexService.INSTANCE.buildSubtitleUrl(subtitleIndex);
			};
		} catch (Exception e) {
			return "";
		}
	}

	public static String rebuildStreamUrl(DisplayBlockEntity display, int subtitleIndex) {
		if (display == null || !display.isOnDemand() || display.getJellyfinItemId().isEmpty()) {
			return "";
		}
		OnDemandProvider provider = display.onDemandProvider();
		if (!isConfigured(provider)) {
			return "";
		}
		try {
			return switch (provider) {
				case JELLYFIN -> JellyfinService.INSTANCE.buildStreamUrl(
					display.getJellyfinItemId(),
					display.getJellyfinMediaSourceId(),
					display.getPlaySessionId(),
					subtitleIndex
				);
				case EMBY -> EmbyService.INSTANCE.buildStreamUrl(
					display.getJellyfinItemId(),
					display.getJellyfinMediaSourceId(),
					display.getPlaySessionId(),
					subtitleIndex
				);
				case PLEX -> PlexService.INSTANCE.buildStreamUrl(
					display.getJellyfinItemId(),
					display.getPlexMediaIndex(),
					display.getPlexPartIndex(),
					display.getPlexPartKey(),
					display.getPlaySessionId(),
					subtitleIndex,
					display.currentPlaybackPositionMs()
				);
			};
		} catch (Exception e) {
			return "";
		}
	}

	public static CompletableFuture<Optional<JellyfinItemSummary>> resolveNextEpisode(
		OnDemandProvider provider,
		String seriesId,
		String seasonId,
		int episodeNumber
	) {
		return switch (provider) {
			case JELLYFIN -> JellyfinService.INSTANCE.resolveNextEpisode(seriesId, seasonId, episodeNumber);
			case EMBY -> EmbyService.INSTANCE.resolveNextEpisode(seriesId, seasonId, episodeNumber);
			case PLEX -> PlexService.INSTANCE.resolveNextEpisode(seriesId, seasonId, episodeNumber);
		};
	}

	public static void reportPlaying(
		OnDemandProvider provider,
		String itemId,
		String mediaSourceId,
		String playSessionId,
		long positionMs,
		boolean paused,
		long durationMs
	) {
		switch (provider) {
			case JELLYFIN -> JellyfinService.INSTANCE.reportPlaying(itemId, mediaSourceId, playSessionId, positionMs, paused);
			case EMBY -> EmbyService.INSTANCE.reportPlaying(itemId, mediaSourceId, playSessionId, positionMs, paused);
			case PLEX -> PlexService.INSTANCE.reportTimeline(itemId, paused ? "paused" : "playing", positionMs, durationMs);
		}
	}

	public static void reportProgress(
		OnDemandProvider provider,
		String itemId,
		String mediaSourceId,
		String playSessionId,
		long positionMs,
		boolean paused,
		long durationMs
	) {
		switch (provider) {
			case JELLYFIN -> JellyfinService.INSTANCE.reportProgress(itemId, mediaSourceId, playSessionId, positionMs, paused);
			case EMBY -> EmbyService.INSTANCE.reportProgress(itemId, mediaSourceId, playSessionId, positionMs, paused);
			case PLEX -> PlexService.INSTANCE.reportTimeline(itemId, paused ? "paused" : "playing", positionMs, durationMs);
		}
	}

	public static void reportStopped(
		OnDemandProvider provider,
		String itemId,
		String mediaSourceId,
		String playSessionId,
		long positionMs,
		boolean completed,
		long durationMs
	) {
		switch (provider) {
			case JELLYFIN -> JellyfinService.INSTANCE.reportStopped(itemId, mediaSourceId, playSessionId, positionMs, completed);
			case EMBY -> EmbyService.INSTANCE.reportStopped(itemId, mediaSourceId, playSessionId, positionMs, completed);
			case PLEX -> PlexService.INSTANCE.reportTimeline(itemId, completed ? "stopped" : "stopped", positionMs, durationMs);
		}
	}

	public static List<JellyfinLibrary> libraries(OnDemandProvider provider) {
		return switch (provider) {
			case JELLYFIN -> JellyfinService.INSTANCE.libraries();
			case EMBY -> EmbyService.INSTANCE.libraries();
			case PLEX -> PlexService.INSTANCE.libraries();
		};
	}

	public static CompletableFuture<List<JellyfinLibrary>> discoverLibraries(OnDemandProvider provider) {
		return switch (provider) {
			case JELLYFIN -> JellyfinService.INSTANCE.discoverLibraries();
			case EMBY -> EmbyService.INSTANCE.discoverLibraries();
			case PLEX -> PlexService.INSTANCE.discoverLibraries();
		};
	}

	public static List<OnDemandProvider> availableForMovies(PixelReelConfig.FeatureFlags flags) {
		List<OnDemandProvider> list = new ArrayList<>();
		if (flags.jellyfinMovies()) {
			list.add(OnDemandProvider.JELLYFIN);
		}
		if (flags.embyMovies()) {
			list.add(OnDemandProvider.EMBY);
		}
		if (flags.plexMovies()) {
			list.add(OnDemandProvider.PLEX);
		}
		return list;
	}

	public static List<OnDemandProvider> availableForShows(PixelReelConfig.FeatureFlags flags) {
		List<OnDemandProvider> list = new ArrayList<>();
		if (flags.jellyfinShows()) {
			list.add(OnDemandProvider.JELLYFIN);
		}
		if (flags.embyShows()) {
			list.add(OnDemandProvider.EMBY);
		}
		if (flags.plexShows()) {
			list.add(OnDemandProvider.PLEX);
		}
		return list;
	}

	public record Page(List<JellyfinItemSummary> items, int page, int totalCount) {
	}

	public record PlaybackStart(
		String streamUrl,
		String playSessionId,
		String mediaSourceId,
		long startPositionTicks,
		boolean hdr,
		List<SubtitleTrack> subtitles,
		int plexMediaIndex,
		int plexPartIndex,
		String plexPartKey
	) {
	}
}
