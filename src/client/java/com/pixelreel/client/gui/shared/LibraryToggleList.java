package com.pixelreel.client.gui.shared;

import com.pixelreel.jellyfin.JellyfinLibrary;
import java.util.List;
import java.util.Set;
import java.util.function.Consumer;
import java.util.function.Function;
import net.minecraft.client.gui.components.Button;
import net.minecraft.network.chat.Component;

public final class LibraryToggleList {
	private LibraryToggleList() {
	}

	public static void rebuild(
		List<Button> buttons,
		Consumer<Button> removeWidget,
		Function<Button, Button> addWidget,
		List<JellyfinLibrary> libraries,
		Set<String> selected,
		int left,
		int startY,
		int maxBottomY,
		Runnable onChanged
	) {
		for (Button button : buttons) {
			removeWidget.accept(button);
		}
		buttons.clear();
		int y = startY;
		for (JellyfinLibrary library : libraries) {
			if (!library.isMovies() && !library.isTvShows()) {
				continue;
			}
			boolean isSelected = selected.isEmpty() || selected.contains(library.id());
			Button button = Button.builder(
				Component.literal((isSelected ? "[x] " : "[ ] ") + library.name() + " (" + library.collectionType() + ")"),
				b -> {
					if (selected.isEmpty()) {
						for (JellyfinLibrary other : libraries) {
							if (other.isMovies() || other.isTvShows()) {
								selected.add(other.id());
							}
						}
					}
					if (selected.contains(library.id())) {
						selected.remove(library.id());
					} else {
						selected.add(library.id());
					}
					onChanged.run();
				}
			).bounds(left, y, 280, 18).build();
			buttons.add(addWidget.apply(button));
			y += 20;
			if (y > maxBottomY) {
				break;
			}
		}
	}
}
