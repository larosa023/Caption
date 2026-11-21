## Caption 0.1.0

The first release: live captions and translation for anything playing on your PC, running 100% locally on your GPU.

- **Captions for any audio:** the whole PC, one output device, or just some apps (Discord, a browser, Prism…), plus your microphone.
- **Fast:** whisper.cpp on your NVIDIA GPU (CUDA), about half a second behind the speech. The line being spoken updates in place and settles when the sentence ends. Voice detection keeps silence from turning into text. Falls back to the CPU automatically.
- **Translation:** detect the spoken language and show English (Whisper), or translate into any language through your local Ollama.
- **Caption bar:** floating, click-through, always on top. One line, two lines or a scrolling box; your font, size, colors and opacity; snap to the bottom of the screen or the active window. **Win + Alt + C** shows or hides captions, **Win + Alt + P** pauses.
- **Transcript:** the full running transcript with timestamps, search, copy, and save as .txt or .srt.
- **Profiles:** Everything, Streams (translate on) and Calls (Discord + mic) to start, and as many of your own as you like.
- **Suite standards:** 14 themes with Mica, accent colors and Hangar theme files; built-in updater with Stable/Beta channels, SHA-256 checks and automatic rollback; portable mode; tray icon; start with Windows. No accounts, no telemetry, no admin rights.

Models download once on first run (you pick: Small, Medium or Large v3 Turbo) and stay put across updates.
