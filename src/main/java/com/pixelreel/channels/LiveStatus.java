package com.pixelreel.channels;

import net.minecraft.network.RegistryFriendlyByteBuf;
import net.minecraft.network.codec.ByteBufCodecs;
import net.minecraft.network.codec.StreamCodec;

/** need this for channel health */
public record LiveStatus(boolean configured, boolean reachable, int channelCount, String detail) {
	public static final int MAX_DETAIL_LENGTH = 256;
	public static final StreamCodec<RegistryFriendlyByteBuf, LiveStatus> STREAM_CODEC = StreamCodec.composite(
		ByteBufCodecs.BOOL,
		LiveStatus::configured,
		ByteBufCodecs.BOOL,
		LiveStatus::reachable,
		ByteBufCodecs.VAR_INT,
		LiveStatus::channelCount,
		ByteBufCodecs.stringUtf8(MAX_DETAIL_LENGTH),
		LiveStatus::detail,
		LiveStatus::new
	);

	public LiveStatus {
		if (detail == null) {
			detail = "";
		}
		if (detail.length() > MAX_DETAIL_LENGTH) {
			detail = detail.substring(0, MAX_DETAIL_LENGTH);
		}
		if (channelCount < -1) {
			channelCount = -1;
		}
	}

	public static LiveStatus notConfigured() {
		return new LiveStatus(false, false, -1, "No channel playlist configured");
	}

	public static LiveStatus offline(String reason) {
		return new LiveStatus(true, false, -1, reason);
	}

	public static LiveStatus online(int channelCount) {
		return new LiveStatus(true, true, channelCount, channelCount + " channel(s) available");
	}
}
