## Caption 0.3.0

**Real subtitles for Ripple, timed to the word.**

- **Subtitles made ahead of playback.** When a movie or episode starts in Ripple, Ripple hands Caption the stream. Caption reads the audio ahead of the playhead and runs Whisper on each stretch of speech with up to 25 seconds of context, with word timing taken from Whisper's own attention. The result is laid out like professional subtitles: one sentence per subtitle, two balanced lines of up to 42 characters, no overlaps. It shows up in Ripple's subtitle menu as "AI subtitles" and keeps filling in a few minutes ahead of where you are. Seeking works.
- **Music and silence are never transcribed.** Voice detection picks out the speech first, so no invented lines over the score or the credits. Dialogue focus (center channel / center extraction) is used here too.
- **Uses your most accurate model.** Pick it in Speech model → Subtitles for Ripple. Large v3 is the best choice here, since subtitles don't need to be live.
- **Live captions step aside** while Ripple shows Caption's subtitles, so you never get two sets.
- The first time, Caption downloads FFmpeg (about 80 MB) to read stream audio.
- Everything stays on your PC: the subtitle service only answers apps on this computer.
