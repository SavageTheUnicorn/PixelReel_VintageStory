package com.pixelreel.networking;

public enum ScreenAction {
	POWER_TOGGLE,
	POWER_ON,
	POWER_OFF,
	VOLUME_SET,
	STOP,
	RESUME,
	CHANNEL_NEXT,
	CHANNEL_PREVIOUS,
	PAUSE_TOGGLE,
	PAUSE,
	UNPAUSE,
	SEEK,
	SEEK_FORWARD,
	SEEK_BACKWARD,
	RESTART,
	PLAY_NEXT_NOW,
	CANCEL_NEXT,
	CYCLE_SUBTITLE,
	SELECT_SUBTITLE,
	REPORT_DURATION;

	private static final ScreenAction[] VALUES = values();

	public static ScreenAction byIndex(int index) {
		return index >= 0 && index < VALUES.length ? VALUES[index] : POWER_TOGGLE;
	}
}
