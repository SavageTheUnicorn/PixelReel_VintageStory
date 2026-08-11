package com.pixelreel.ondemand;

/** connection settings shared Jellyfin and Emby */
public record EmbyStyleConnection(
	String serverName,
	String baseUrl,
	String apiKey,
	String userId
) {
	public boolean isConfigured() {
		return this.baseUrl != null && !this.baseUrl.isBlank()
			&& this.apiKey != null && !this.apiKey.isBlank();
	}
}
