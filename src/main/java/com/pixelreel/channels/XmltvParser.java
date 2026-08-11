package com.pixelreel.channels;

import com.pixelreel.PixelReel;
import java.io.Reader;
import java.time.OffsetDateTime;
import java.time.format.DateTimeFormatter;
import java.time.format.DateTimeParseException;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import javax.xml.stream.XMLInputFactory;
import javax.xml.stream.XMLStreamConstants;
import javax.xml.stream.XMLStreamReader;

/** streaming XMLTV parser */
public final class XmltvParser {
	private static final DateTimeFormatter XMLTV_TIME = DateTimeFormatter.ofPattern("yyyyMMddHHmmss Z");
	private static final long KEEP_PAST_SECONDS = 3 * 60 * 60;
	private static final long KEEP_FUTURE_SECONDS = 48L * 60 * 60;

	private XmltvParser() {
	}

	public record GuideChannel(String id, List<String> displayNames, String iconUrl) {
	}

	public record GuideData(Map<String, GuideChannel> channels, Map<String, List<Programme>> programmes) {
		public static GuideData empty() {
			return new GuideData(Map.of(), Map.of());
		}
	}

	public static GuideData parse(Reader reader) {
		Map<String, GuideChannel> channels = new HashMap<>();
		Map<String, List<Programme>> programmes = new HashMap<>();
		long now = System.currentTimeMillis() / 1000L;

		try {
			XMLInputFactory factory = XMLInputFactory.newFactory();
			factory.setProperty(XMLInputFactory.SUPPORT_DTD, false);
			factory.setProperty(XMLInputFactory.IS_SUPPORTING_EXTERNAL_ENTITIES, false);
			XMLStreamReader xml = factory.createXMLStreamReader(reader);

			String channelId = null;
			List<String> displayNames = null;
			String channelIcon = "";

			String progChannel = null;
			long progStart = 0L;
			long progEnd = 0L;
			String progTitle = "";
			String progEpisode = "";
			String progIcon = "";
			String textTarget = null;
			StringBuilder text = new StringBuilder();

			while (xml.hasNext()) {
				int event = xml.next();
				if (event == XMLStreamConstants.START_ELEMENT) {
					String tag = xml.getLocalName();
					switch (tag) {
						case "channel" -> {
							channelId = attr(xml, "id");
							displayNames = new ArrayList<>();
							channelIcon = "";
						}
						case "programme" -> {
							progChannel = attr(xml, "channel");
							progStart = parseTime(attr(xml, "start"));
							progEnd = parseTime(attr(xml, "stop"));
							progTitle = "";
							progEpisode = "";
							progIcon = "";
						}
						case "display-name" -> {
							if (channelId != null) {
								textTarget = "display-name";
								text.setLength(0);
							}
						}
						case "title" -> {
							if (progChannel != null) {
								textTarget = "title";
								text.setLength(0);
							}
						}
						case "sub-title" -> {
							if (progChannel != null) {
								textTarget = "sub-title";
								text.setLength(0);
							}
						}
						case "icon" -> {
							String src = attr(xml, "src");
							if (!src.isEmpty()) {
								if (progChannel != null && progIcon.isEmpty()) {
									progIcon = src;
								} else if (channelId != null && channelIcon.isEmpty()) {
									channelIcon = src;
								}
							}
						}
						default -> {
						}
					}
				} else if (event == XMLStreamConstants.CHARACTERS || event == XMLStreamConstants.CDATA) {
					if (textTarget != null) {
						text.append(xml.getText());
					}
				} else if (event == XMLStreamConstants.END_ELEMENT) {
					String tag = xml.getLocalName();
					switch (tag) {
						case "display-name" -> {
							if (displayNames != null && text.length() > 0) {
								displayNames.add(text.toString().strip());
							}
							textTarget = null;
						}
						case "title" -> {
							if (progChannel != null) {
								progTitle = text.toString().strip();
							}
							textTarget = null;
						}
						case "sub-title" -> {
							if (progChannel != null) {
								progEpisode = text.toString().strip();
							}
							textTarget = null;
						}
						case "channel" -> {
							if (channelId != null && !channelId.isEmpty()) {
								channels.put(channelId, new GuideChannel(channelId, List.copyOf(displayNames), channelIcon));
							}
							channelId = null;
							displayNames = null;
						}
						case "programme" -> {
							if (progChannel != null && !progChannel.isEmpty() && progEnd > 0
								&& progEnd >= now - KEEP_PAST_SECONDS && progStart <= now + KEEP_FUTURE_SECONDS) {
								programmes.computeIfAbsent(progChannel, key -> new ArrayList<>())
									.add(new Programme(progTitle, progEpisode, progStart, progEnd, progIcon));
							}
							progChannel = null;
						}
						default -> {
						}
					}
				}
			}
			xml.close();
		} catch (Exception e) {
			PixelReel.LOGGER.warn("Could not parse the channel guide: {}", e.toString());
			return GuideData.empty();
		}

		for (List<Programme> list : programmes.values()) {
			list.sort(Comparator.comparingLong(Programme::startEpochSeconds));
		}
		return new GuideData(channels, programmes);
	}

	private static String attr(XMLStreamReader xml, String name) {
		String value = xml.getAttributeValue(null, name);
		return value == null ? "" : value.strip();
	}

	private static long parseTime(String raw) {
		if (raw == null || raw.isEmpty()) {
			return 0L;
		}
		try {
			return OffsetDateTime.parse(raw, XMLTV_TIME).toEpochSecond();
		} catch (DateTimeParseException e) {
			try {
				return OffsetDateTime.parse(raw.strip() + " +0000", XMLTV_TIME).toEpochSecond();
			} catch (DateTimeParseException e2) {
				return 0L;
			}
		}
	}
}
