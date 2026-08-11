package com.pixelreel.client.texture;

import com.mojang.blaze3d.platform.NativeImage;
import com.pixelreel.PixelReel;
import com.pixelreel.channels.Channel;
import com.pixelreel.channels.ChannelEntry;
import com.pixelreel.config.ConfigManager;
import com.pixelreel.config.PixelReelConfig;
import java.awt.Graphics2D;
import java.awt.image.BufferedImage;
import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.net.URI;
import java.net.URISyntaxException;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.HashMap;
import java.util.HexFormat;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import javax.imageio.ImageIO;
import net.minecraft.client.Minecraft;
import net.minecraft.client.renderer.texture.DynamicTexture;
import net.minecraft.resources.ResourceLocation;
import org.jetbrains.annotations.Nullable;
import org.lwjgl.system.MemoryUtil;

/** loads channel logos asynchronously */
public final class PosterCache {
	public static final PosterCache INSTANCE = new PosterCache();
	public static final ResourceLocation PLACEHOLDER = PixelReel.id("textures/gui/channel_placeholder.png");

	private static final int MAX_ENTRIES = 512;
	private static final long MAX_IMAGE_BYTES = 8 * 1024 * 1024;
	private static final int MAX_DIMENSION = 512;
	private static final long FAILURE_RETRY_MILLIS = 30_000L;
	private static final Set<String> BROKEN_HOSTS = Set.of("localhost", "127.0.0.1", "0.0.0.0", "::1", "[::1]");
	private static final String LOGO_KEY_PREFIX = "chlogo:";

	public enum State {
		LOADING,
		READY,
		FALLBACK
	}

	public record Poster(State state, @Nullable ResourceLocation texture, int width, int height) {
		public ResourceLocation textureOrPlaceholder() {
			return this.texture != null ? this.texture : PLACEHOLDER;
		}
	}

	private static final Poster LOADING_POSTER = new Poster(State.LOADING, null, 0, 0);
	private static final Poster FALLBACK_POSTER = new Poster(State.FALLBACK, null, 0, 0);

	private final Map<String, Entry> entries = new HashMap<>();
	private final Deque<String> order = new ArrayDeque<>();
	private final ExecutorService loader = Executors.newFixedThreadPool(3, runnable -> {
		Thread thread = new Thread(runnable, "pixelreel-poster");
		thread.setDaemon(true);
		return thread;
	});
	private volatile @Nullable HttpClient httpClient;

	private PosterCache() {
	}

	public Poster get(ChannelEntry entry) {
		Channel channel = entry.channel();
		String key = LOGO_KEY_PREFIX + (channel.id().isEmpty() ? "num:" + channel.number() : channel.id());
		return this.getOrLoad(key, buildCandidates(entry));
	}

	public Poster getByUrl(String key, String url) {
		if (key == null || key.isBlank() || url == null || url.isBlank()) {
			return FALLBACK_POSTER;
		}
		return this.getOrLoad("img:" + key, List.of(new Candidate.Remote(url)));
	}

	private Poster getOrLoad(String key, List<Candidate> candidates) {
		Entry cached = this.entries.get(key);
		if (cached != null) {
			if (cached.state == State.READY && cached.textureId != null) {
				return new Poster(State.READY, cached.textureId, cached.width, cached.height);
			}
			if (cached.state == State.LOADING) {
				return LOADING_POSTER;
			}
			if (System.currentTimeMillis() - cached.failedAt < FAILURE_RETRY_MILLIS) {
				return FALLBACK_POSTER;
			}
			this.entries.remove(key);
			this.order.remove(key);
		}

		Entry pending = new Entry();
		this.put(key, pending);
		this.loader.execute(() -> this.loadChain(key, pending, candidates));
		return LOADING_POSTER;
	}

	private sealed interface Candidate {
		record LocalFile(Path path) implements Candidate {
		}

		record Remote(String url) implements Candidate {
		}
	}

	private static List<Candidate> buildCandidates(ChannelEntry entry) {
		Channel channel = entry.channel();
		PixelReelConfig config = ConfigManager.get();
		LinkedHashSet<String> remoteUrls = new LinkedHashSet<>();
		List<Candidate> candidates = new ArrayList<>();

		String override = firstOverride(config, channel);
		if (override != null) {
			if (override.regionMatches(true, 0, "http://", 0, 7) || override.regionMatches(true, 0, "https://", 0, 8)) {
				remoteUrls.add(override);
			} else {
				Path path = Path.of(override);
				candidates.add(new Candidate.LocalFile(path.isAbsolute() ? path : ConfigManager.posterOverrideDir().resolve(override)));
			}
		}

		Path posterDir = ConfigManager.posterOverrideDir();
		for (String base : new String[]{String.valueOf(channel.number()), sanitizeFileName(channel.id()), sanitizeFileName(channel.name())}) {
			if (base.isEmpty()) {
				continue;
			}
			for (String extension : new String[]{".png", ".jpg", ".jpeg"}) {
				candidates.add(new Candidate.LocalFile(posterDir.resolve(base + extension)));
			}
		}

		for (String url : new String[]{channel.logoUrl(), channel.guideIconUrl()}) {
			String rewritten = rewriteBrokenHost(url, config);
			if (!rewritten.isEmpty()) {
				remoteUrls.add(rewritten);
			}
		}
		for (String guessed : guessTunarrChannelIcons(channel.streamUrl(), config)) {
			remoteUrls.add(guessed);
		}

		for (String url : remoteUrls) {
			candidates.add(new Candidate.Remote(url));
		}
		return candidates;
	}

	private static List<String> guessTunarrChannelIcons(String streamUrl, PixelReelConfig config) {
		if (streamUrl == null || streamUrl.isBlank()) {
			return List.of();
		}
		try {
			URI uri = URI.create(streamUrl.trim());
			String path = uri.getPath();
			if (path == null) {
				return List.of();
			}
			int marker = path.indexOf("/stream/channels/");
			if (marker < 0) {
				return List.of();
			}
			String uuid = path.substring(marker + "/stream/channels/".length());
			int slash = uuid.indexOf('/');
			if (slash >= 0) {
				uuid = uuid.substring(0, slash);
			}
			if (uuid.isBlank()) {
				return List.of();
			}
			String host = config.mediaServerHost();
			if (host.isEmpty() && uri.getHost() != null) {
				host = uri.getHost();
			}
			if (host.isEmpty()) {
				return List.of();
			}
			int port = uri.getPort();
			String base = (uri.getScheme() == null ? "http" : uri.getScheme()) + "://" + host + (port > 0 ? ":" + port : "");
			return List.of(
				base + "/images/uploads/" + uuid + "_icon.png",
				base + "/images/uploads/" + uuid + "_icon.jpeg",
				base + "/images/uploads/" + uuid + "_icon.jpg"
			);
		} catch (IllegalArgumentException e) {
			return List.of();
		}
	}

	private static @Nullable String firstOverride(PixelReelConfig config, Channel channel) {
		for (String key : new String[]{String.valueOf(channel.number()), channel.id(), channel.name()}) {
			String value = config.posterOverrides.get(key);
			if (value != null && !value.isBlank()) {
				return value.trim();
			}
		}
		return null;
	}

	public static String sanitizeFileName(String value) {
		if (value == null) {
			return "";
		}
		return value.toLowerCase(Locale.ROOT).replaceAll("[^a-z0-9._ -]", "_").strip();
	}

	public static String rewriteBrokenHost(String url, PixelReelConfig config) {
		if (url == null || url.isBlank()) {
			return "";
		}
		String trimmed = url.trim();
		if (!trimmed.regionMatches(true, 0, "http://", 0, 7) && !trimmed.regionMatches(true, 0, "https://", 0, 8)) {
			return "";
		}
		try {
			URI uri = URI.create(trimmed);
			String host = uri.getHost();
			if (host == null) {
				return "";
			}
			if (!BROKEN_HOSTS.contains(host.toLowerCase(Locale.ROOT))) {
				return trimmed;
			}
			String replacement = config.mediaServerHost();
			if (replacement.isEmpty()) {
				return trimmed;
			}
			int port = uri.getPort();
			if (port < 0) {
				try {
					port = URI.create(config.m3uUrl).getPort();
				} catch (IllegalArgumentException ignored) {
				}
			}
			return new URI(uri.getScheme(), null, replacement, port, uri.getPath(), uri.getQuery(), uri.getFragment()).toString();
		} catch (IllegalArgumentException | URISyntaxException e) {
			return "";
		}
	}

	private void loadChain(String key, Entry entry, List<Candidate> candidates) {
		try {
			for (Candidate candidate : candidates) {
				try {
					byte[] bytes = switch (candidate) {
						case Candidate.LocalFile localFile -> readLocal(localFile.path());
						case Candidate.Remote remote -> this.fetchRemote(remote.url());
					};
					if (bytes == null) {
						continue;
					}
					// Prefer AWT prepare on the worker: downscale + PNG encode so the render thread
					// only uploads a small buffer. NativeImage.read(byte[]) uses LWJGL's 64KB
					// MemoryStack and crashes with "Out of stack space" on normal posters.
					byte[] preparedBytes = prepareForUpload(bytes);
					Minecraft minecraft = Minecraft.getInstance();
					if (minecraft == null) {
						continue;
					}
					String source = switch (candidate) {
						case Candidate.LocalFile localFile -> localFile.path().toString();
						case Candidate.Remote remote -> remote.url();
					};
					minecraft.execute(() -> this.installBytes(key, entry, preparedBytes, source));
					return;
				} catch (Throwable t) {
					PixelReel.LOGGER.warn("Channel thumbnail candidate failed for {} ({}): {}", key, candidate, t.toString());
				}
			}
			entry.state = State.FALLBACK;
			entry.failedAt = System.currentTimeMillis();
			PixelReel.LOGGER.info("No usable channel thumbnail for {}; using fallback card", key);
		} catch (Throwable t) {
			entry.state = State.FALLBACK;
			entry.failedAt = System.currentTimeMillis();
			PixelReel.LOGGER.warn("Artwork load failed for {}", key, t);
		}
	}

	/** Downscale with AWT when possible so upload work stays small. Falls back to original bytes. */
	private static byte[] prepareForUpload(byte[] bytes) throws IOException {
		BufferedImage awt = ImageIO.read(new ByteArrayInputStream(bytes));
		if (awt == null) {
			return bytes;
		}
		int width = awt.getWidth();
		int height = awt.getHeight();
		int targetWidth = width;
		int targetHeight = height;
		if (width > MAX_DIMENSION || height > MAX_DIMENSION) {
			float scale = (float) MAX_DIMENSION / Math.max(width, height);
			targetWidth = Math.max(1, Math.round(width * scale));
			targetHeight = Math.max(1, Math.round(height * scale));
		}
		BufferedImage rgba = new BufferedImage(targetWidth, targetHeight, BufferedImage.TYPE_INT_ARGB);
		Graphics2D graphics = rgba.createGraphics();
		graphics.drawImage(awt, 0, 0, targetWidth, targetHeight, null);
		graphics.dispose();
		ByteArrayOutputStream png = new ByteArrayOutputStream();
		ImageIO.write(rgba, "png", png);
		return png.toByteArray();
	}

	/**
	 * Decode via a direct buffer. Do not call {@link NativeImage#read(byte[])} — that copies the
	 * entire payload onto the thread-local LWJGL MemoryStack (~64KB) and OOMs on typical artwork.
	 */
	private static NativeImage decodeImage(byte[] bytes) throws IOException {
		ByteBuffer buffer = MemoryUtil.memAlloc(bytes.length);
		try {
			buffer.put(bytes);
			buffer.flip();
			return NativeImage.read(buffer);
		} catch (Exception nativeFailure) {
			BufferedImage awt = ImageIO.read(new ByteArrayInputStream(bytes));
			if (awt == null) {
				throw nativeFailure instanceof IOException io ? io : new IOException(nativeFailure);
			}
			int width = awt.getWidth();
			int height = awt.getHeight();
			BufferedImage rgba = new BufferedImage(width, height, BufferedImage.TYPE_INT_ARGB);
			Graphics2D graphics = rgba.createGraphics();
			graphics.drawImage(awt, 0, 0, null);
			graphics.dispose();
			ByteArrayOutputStream png = new ByteArrayOutputStream();
			ImageIO.write(rgba, "png", png);
			byte[] pngBytes = png.toByteArray();
			ByteBuffer fallback = MemoryUtil.memAlloc(pngBytes.length);
			try {
				fallback.put(pngBytes);
				fallback.flip();
				return NativeImage.read(fallback);
			} finally {
				MemoryUtil.memFree(fallback);
			}
		} finally {
			MemoryUtil.memFree(buffer);
		}
	}

	private static byte @Nullable [] readLocal(Path path) throws IOException {
		if (!Files.isRegularFile(path)) {
			return null;
		}
		byte[] bytes = Files.readAllBytes(path);
		return looksLikeImage(bytes) ? bytes : null;
	}

	private byte @Nullable [] fetchRemote(String url) throws Exception {
		Path cacheFile = cachePathFor(url);
		PixelReelConfig config = ConfigManager.get();
		if (cacheFile != null && Files.isRegularFile(cacheFile)) {
			Instant expiry = Files.getLastModifiedTime(cacheFile).toInstant().plus(Duration.ofHours(config.artworkCacheHours));
			if (Instant.now().isBefore(expiry)) {
				byte[] cached = Files.readAllBytes(cacheFile);
				if (looksLikeImage(cached) && !looksLikeText(cached)) {
					return cached;
				}
			}
		}

		HttpResponse<byte[]> response = this.client().send(
			HttpRequest.newBuilder(URI.create(url))
				.timeout(Duration.ofSeconds(config.networkTimeoutSeconds))
				.header("User-Agent", "pixelReel/1.0")
				.header("Accept", "image/png,image/jpeg,image/*;q=0.8,*/*;q=0.5")
				.GET()
				.build(),
			HttpResponse.BodyHandlers.ofByteArray()
		);
		if (response.statusCode() / 100 != 2) {
			return null;
		}
		byte[] body = response.body();
		if (body.length == 0 || body.length > MAX_IMAGE_BYTES) {
			return null;
		}
		String contentType = response.headers().firstValue("Content-Type").orElse("").toLowerCase(Locale.ROOT);
		if (contentType.contains("text/html") || contentType.contains("application/json") || contentType.contains("text/xml") || contentType.contains("application/xml")) {
			return null;
		}
		boolean declaredImage = contentType.startsWith("image/");
		if ((!declaredImage && !looksLikeImage(body)) || looksLikeText(body)) {
			return null;
		}
		if (cacheFile != null) {
			try {
				Files.createDirectories(cacheFile.getParent());
				Files.write(cacheFile, body);
			} catch (IOException e) {
				PixelReel.LOGGER.debug("Could not cache poster to disk: {}", e.toString());
			}
		}
		return body;
	}

	private static boolean looksLikeImage(byte[] bytes) {
		if (bytes.length < 12) {
			return false;
		}
		if ((bytes[0] & 0xFF) == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) {
			return true;
		}
		if ((bytes[0] & 0xFF) == 0xFF && (bytes[1] & 0xFF) == 0xD8 && (bytes[2] & 0xFF) == 0xFF) {
			return true;
		}
		// GIF
		if (bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F') {
			return true;
		}
		// WebP: RIFF....WEBP
		if (bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
			&& bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P') {
			return true;
		}
		return false;
	}

	private static boolean looksLikeText(byte[] bytes) {
		int check = Math.min(bytes.length, 64);
		for (int i = 0; i < check; i++) {
			byte value = bytes[i];
			if (value == 0) {
				return false;
			}
		}
		String head = new String(bytes, 0, check, StandardCharsets.US_ASCII).toLowerCase(Locale.ROOT).stripLeading();
		return head.startsWith("<!doctype") || head.startsWith("<html") || head.startsWith("<?xml") || head.startsWith("{") || head.startsWith("[");
	}

	private static @Nullable Path cachePathFor(String url) {
		try {
			MessageDigest digest = MessageDigest.getInstance("SHA-1");
			String name = HexFormat.of().formatHex(digest.digest(url.getBytes(StandardCharsets.UTF_8)));
			return ConfigManager.artworkCacheDir().resolve(name + ".img");
		} catch (Exception e) {
			return null;
		}
	}

	private static NativeImage downscaleIfNeeded(NativeImage source) {
		int width = source.getWidth();
		int height = source.getHeight();
		if (width <= MAX_DIMENSION && height <= MAX_DIMENSION) {
			return source;
		}
		float scale = (float)MAX_DIMENSION / Math.max(width, height);
		int targetWidth = Math.max(1, Math.round(width * scale));
		int targetHeight = Math.max(1, Math.round(height * scale));
		NativeImage target = new NativeImage(targetWidth, targetHeight, false);
		source.resizeSubRectTo(0, 0, width, height, target);
		source.close();
		return target;
	}

	private void installBytes(String key, Entry entry, byte[] bytes, String source) {
		if (this.entries.get(key) != entry) {
			return;
		}
		NativeImage prepared = null;
		try {
			NativeImage image = decodeImage(bytes);
			if (image == null) {
				throw new IOException("Unsupported image data");
			}
			prepared = downscaleIfNeeded(image);
			int width = prepared.getWidth();
			int height = prepared.getHeight();
			DynamicTexture texture = new DynamicTexture(prepared);
			prepared = null; // ownership transferred to DynamicTexture
			texture.upload();
			ResourceLocation id = Minecraft.getInstance().getTextureManager()
				.register("pixelreel_poster", texture);
			entry.textureId = id;
			entry.width = width;
			entry.height = height;
			entry.state = State.READY;
			PixelReel.LOGGER.info("Loaded channel thumbnail for {} from {} -> {}", key, source, id);
		} catch (Exception e) {
			if (prepared != null) {
				prepared.close();
			}
			entry.state = State.FALLBACK;
			entry.failedAt = System.currentTimeMillis();
			PixelReel.LOGGER.warn("Could not upload channel artwork for {} from {}", key, source, e);
		}
	}

	private void put(String key, Entry entry) {
		this.entries.put(key, entry);
		this.order.addLast(key);
		while (this.order.size() > MAX_ENTRIES) {
			String evicted = this.order.pollFirst();
			if (evicted == null) {
				break;
			}
			Entry removed = this.entries.remove(evicted);
			if (removed != null) {
				releaseTexture(removed);
			}
		}
	}

	private static void releaseTexture(Entry entry) {
		if (entry.textureId != null) {
			Minecraft minecraft = Minecraft.getInstance();
			if (minecraft != null) {
				minecraft.getTextureManager().release(entry.textureId);
			}
			entry.textureId = null;
		}
	}

	private HttpClient client() {
		HttpClient existing = this.httpClient;
		if (existing != null) {
			return existing;
		}
		synchronized (this) {
			if (this.httpClient == null) {
				this.httpClient = HttpClient.newBuilder()
					.connectTimeout(Duration.ofSeconds(ConfigManager.get().networkTimeoutSeconds))
					.followRedirects(HttpClient.Redirect.NORMAL)
					.build();
			}
			return this.httpClient;
		}
	}

	public void clear() {
		this.entries.values().forEach(PosterCache::releaseTexture);
		this.entries.clear();
		this.order.clear();
	}

	private static final class Entry {
		volatile State state = State.LOADING;
		@Nullable ResourceLocation textureId;
		int width;
		int height;
		volatile long failedAt;
	}
}
