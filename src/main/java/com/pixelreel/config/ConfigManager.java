package com.pixelreel.config;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import com.google.gson.JsonSyntaxException;
import com.pixelreel.PixelReel;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.AtomicMoveNotSupportedException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.function.Consumer;
import net.fabricmc.loader.api.FabricLoader;

public final class ConfigManager {
	private static final String FILE_NAME = "pixelreel.json";
	private static final Gson GSON = new GsonBuilder().setPrettyPrinting().disableHtmlEscaping().create();
	private static volatile PixelReelConfig config = new PixelReelConfig().validated();
	private static Path configPath;

	private ConfigManager() {
	}

	public static PixelReelConfig get() {
		return config;
	}

	public static Path path() {
		if (configPath == null) {
			configPath = FabricLoader.getInstance().getConfigDir().resolve(FILE_NAME);
		}
		return configPath;
	}

	public static Path posterOverrideDir() {
		return FabricLoader.getInstance().getConfigDir().resolve("pixelreel-posters");
	}

	public static Path artworkCacheDir() {
		return FabricLoader.getInstance().getConfigDir().resolve(get().artworkCacheLocation);
	}

	public static synchronized void load() {
		Path file = path();
		migrateLegacyFiles(file);
		if (!Files.exists(file)) {
			config = new PixelReelConfig().validated();
			write(file, config);
			PixelReel.LOGGER.info("Created default pixelReel config at {}", file);
		} else {
			try {
				String text = Files.readString(file, StandardCharsets.UTF_8);
				PixelReelConfig loaded = GSON.fromJson(text, PixelReelConfig.class);
				config = (loaded == null ? new PixelReelConfig() : loaded).validated();
				PixelReel.LOGGER.info("Loaded pixelReel config from {}", file);
			} catch (JsonSyntaxException e) {
				PixelReel.LOGGER.error("pixelReel config at {} is not valid JSON; using defaults for this session.", file, e);
				config = new PixelReelConfig().validated();
			} catch (IOException e) {
				PixelReel.LOGGER.error("Could not read pixelReel config at {}; using defaults for this session.", file, e);
				config = new PixelReelConfig().validated();
			}
		}
		try {
			Files.createDirectories(posterOverrideDir());
		} catch (IOException e) {
			PixelReel.LOGGER.debug("Could not create the poster override directory", e);
		}
	}

	public static synchronized PixelReelConfig update(Consumer<PixelReelConfig> mutator) {
		PixelReelConfig working = GSON.fromJson(GSON.toJsonTree(config), PixelReelConfig.class);
		mutator.accept(working);
		config = working.validated();
		write(path(), config);
		return config;
	}

	private static void migrateLegacyFiles(Path file) {
		Path configDir = FabricLoader.getInstance().getConfigDir();
		Path legacyConfig = configDir.resolve("livecinema.json");
		if (!Files.exists(file) && Files.exists(legacyConfig)) {
			try {
				Files.copy(legacyConfig, file);
				PixelReel.LOGGER.info("Migrated legacy config {} -> {}", legacyConfig, file);
			} catch (IOException e) {
				PixelReel.LOGGER.warn("Could not migrate legacy config from {}", legacyConfig, e);
			}
		}
		Path legacyPosters = configDir.resolve("livecinema-posters");
		Path posters = posterOverrideDir();
		if (Files.isDirectory(legacyPosters) && !Files.exists(posters)) {
			try {
				Files.move(legacyPosters, posters);
				PixelReel.LOGGER.info("Migrated legacy posters {} -> {}", legacyPosters, posters);
			} catch (IOException e) {
				PixelReel.LOGGER.warn("Could not migrate legacy posters from {}", legacyPosters, e);
			}
		}
	}

	private static void write(Path file, PixelReelConfig value) {
		try {
			Files.createDirectories(file.getParent());
			JsonObject document = GSON.toJsonTree(value).getAsJsonObject();
			Path temp = file.resolveSibling(file.getFileName() + ".tmp");
			Files.writeString(temp, GSON.toJson(document) + System.lineSeparator(), StandardCharsets.UTF_8);
			try {
				Files.move(temp, file, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
			} catch (AtomicMoveNotSupportedException e) {
				Files.move(temp, file, StandardCopyOption.REPLACE_EXISTING);
			}
		} catch (IOException e) {
			PixelReel.LOGGER.error("Failed to write pixelReel config to {}", file, e);
		}
	}
}
