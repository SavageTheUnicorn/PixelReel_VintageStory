package com.pixelreel.jellyfin;

import net.minecraft.network.RegistryFriendlyByteBuf;
import net.minecraft.network.codec.ByteBufCodecs;
import net.minecraft.network.codec.StreamCodec;

/** jellyfin folder */
public record JellyfinLibrary(String id, String name, String collectionType) {
	public static final int MAX_TEXT = 128;

	public static final StreamCodec<RegistryFriendlyByteBuf, JellyfinLibrary> STREAM_CODEC = StreamCodec.composite(
		ByteBufCodecs.stringUtf8(MAX_TEXT),
		JellyfinLibrary::id,
		ByteBufCodecs.stringUtf8(MAX_TEXT),
		JellyfinLibrary::name,
		ByteBufCodecs.stringUtf8(MAX_TEXT),
		JellyfinLibrary::collectionType,
		JellyfinLibrary::new
	);

	public boolean isMovies() {
		return "movies".equalsIgnoreCase(this.collectionType);
	}

	public boolean isTvShows() {
		return "tvshows".equalsIgnoreCase(this.collectionType);
	}
}
