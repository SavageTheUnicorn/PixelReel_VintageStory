package com.pixelreel.client.playback.subtitle;

import java.util.ArrayList;
import java.util.List;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/** on-screen subtitle overlay/parser. */
public final class SubtitleParser {
	private static final Pattern SRT_TIME = Pattern.compile(
		"(\\d{1,2}):(\\d{2}):(\\d{2})[,.](\\d{1,3})\\s*-->\\s*(\\d{1,2}):(\\d{2}):(\\d{2})[,.](\\d{1,3})"
	);
	private static final Pattern VTT_TIME = Pattern.compile(
		"(?:(\\d{1,2}):)?(\\d{1,2}):(\\d{2})\\.(\\d{1,3})\\s*-->\\s*(?:(\\d{1,2}):)?(\\d{1,2}):(\\d{2})\\.(\\d{1,3})"
	);

	private SubtitleParser() {
	}

	public record Cue(long startMs, long endMs, String text) {
	}

	public static List<Cue> parse(String raw) {
		if (raw == null || raw.isBlank()) {
			return List.of();
		}
		String body = raw.replace("\r\n", "\n").replace('\r', '\n').strip();
		if (body.regionMatches(true, 0, "WEBVTT", 0, 6)) {
			return parseVtt(body);
		}
		return parseSrt(body);
	}

	private static List<Cue> parseSrt(String body) {
		String[] blocks = body.split("\\n\\s*\\n");
		List<Cue> cues = new ArrayList<>();
		for (String block : blocks) {
			String[] lines = block.strip().split("\\n");
			if (lines.length < 2) {
				continue;
			}
			int timeLine = 0;
			Matcher matcher = SRT_TIME.matcher(lines[0]);
			if (!matcher.find() && lines.length >= 2) {
				timeLine = 1;
				matcher = SRT_TIME.matcher(lines[1]);
			}
			if (!matcher.find()) {
				continue;
			}
			long start = toMs(matcher.group(1), matcher.group(2), matcher.group(3), matcher.group(4));
			long end = toMs(matcher.group(5), matcher.group(6), matcher.group(7), matcher.group(8));
			StringBuilder text = new StringBuilder();
			for (int i = timeLine + 1; i < lines.length; i++) {
				if (!text.isEmpty()) {
					text.append('\n');
				}
				text.append(stripTags(lines[i]));
			}
			String cleaned = text.toString().strip();
			if (!cleaned.isEmpty() && end > start) {
				cues.add(new Cue(start, end, cleaned));
			}
		}
		return List.copyOf(cues);
	}

	private static List<Cue> parseVtt(String body) {
		String[] blocks = body.split("\\n\\s*\\n");
		List<Cue> cues = new ArrayList<>();
		for (String block : blocks) {
			String stripped = block.strip();
			if (stripped.isEmpty() || stripped.regionMatches(true, 0, "WEBVTT", 0, 6) || stripped.startsWith("NOTE")) {
				continue;
			}
			String[] lines = stripped.split("\\n");
			int timeLine = -1;
			Matcher matcher = null;
			for (int i = 0; i < lines.length; i++) {
				Matcher m = VTT_TIME.matcher(lines[i]);
				if (m.find()) {
					timeLine = i;
					matcher = m;
					break;
				}
			}
			if (timeLine < 0 || matcher == null) {
				continue;
			}
			long start = toMsVtt(matcher.group(1), matcher.group(2), matcher.group(3), matcher.group(4));
			long end = toMsVtt(matcher.group(5), matcher.group(6), matcher.group(7), matcher.group(8));
			StringBuilder text = new StringBuilder();
			for (int i = timeLine + 1; i < lines.length; i++) {
				if (!text.isEmpty()) {
					text.append('\n');
				}
				text.append(stripTags(lines[i]));
			}
			String cleaned = text.toString().strip();
			if (!cleaned.isEmpty() && end > start) {
				cues.add(new Cue(start, end, cleaned));
			}
		}
		return List.copyOf(cues);
	}

	private static long toMs(String h, String m, String s, String frac) {
		return (Long.parseLong(h) * 3600L + Long.parseLong(m) * 60L + Long.parseLong(s)) * 1000L + fracToMs(frac);
	}

	private static long toMsVtt(String h, String m, String s, String frac) {
		long hours = h == null || h.isBlank() ? 0L : Long.parseLong(h);
		return (hours * 3600L + Long.parseLong(m) * 60L + Long.parseLong(s)) * 1000L + fracToMs(frac);
	}

	private static long fracToMs(String frac) {
		String padded = (frac + "000").substring(0, 3);
		return Long.parseLong(padded);
	}

	private static String stripTags(String line) {
		return line.replaceAll("<[^>]+>", "").replace("&nbsp;", " ").replace("&amp;", "&").replace("&lt;", "<").replace("&gt;", ">").strip();
	}
}
