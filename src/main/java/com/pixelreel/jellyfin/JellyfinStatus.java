package com.pixelreel.jellyfin;

import net.minecraft.network.RegistryFriendlyByteBuf;
import net.minecraft.network.codec.ByteBufCodecs;
import net.minecraft.network.codec.StreamCodec;

/** connection / library status */
public record JellyfinStatus(
	boolean configured,
	boolean reachable,
	boolean authenticated,
	int movieCount,
	int seriesCount,
	String detail
) {
	public static final StreamCodec<RegistryFriendlyByteBuf, JellyfinStatus> STREAM_CODEC = StreamCodec.composite(
		ByteBufCodecs.BOOL,
		JellyfinStatus::configured,
		ByteBufCodecs.BOOL,
		JellyfinStatus::reachable,
		ByteBufCodecs.BOOL,
		JellyfinStatus::authenticated,
		ByteBufCodecs.VAR_INT,
		JellyfinStatus::movieCount,
		ByteBufCodecs.VAR_INT,
		JellyfinStatus::seriesCount,
		ByteBufCodecs.stringUtf8(256),
		JellyfinStatus::detail,
		JellyfinStatus::new
	);

	public static JellyfinStatus notConfigured() {
		return new JellyfinStatus(false, false, false, 0, 0, "Jellyfin is not configured");
	}

	public static JellyfinStatus offline(String detail) {
		return new JellyfinStatus(true, false, false, 0, 0, detail == null ? "Unavailable" : detail);
	}

	public static JellyfinStatus authFailed(String detail) {
		return new JellyfinStatus(true, true, false, 0, 0, detail == null ? "Authentication failed" : detail);
	}

	public static JellyfinStatus online(int movies, int series, String detail) {
		return new JellyfinStatus(true, true, true, movies, series, detail == null ? "" : detail);
	}
}
