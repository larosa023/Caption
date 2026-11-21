## Caption 0.3.2

**Ripple subtitles: fewer missed lines, tighter timing, cleaner layout.**

Measured against the official subtitles of Blender's open movie *Tears of Steel* (dialogue over music and effects), with a new automated test that runs on every change:

- **Fewer missed lines.** Speech is now detected on both the dialogue (center) channel and the full mix, with more sensitive thresholds, so lines said over loud music and effects get through. Lines caught went from about 72% to 77–79%.
- **Timing that follows the voice.** Each line now starts when the voice starts (with a tiny lead so your eye catches it) and ends when it stops, instead of hanging on into the pause. Subtitle ends within half a second of the official ones went from 42% to about 72%.
- **Layout like a subtitler's.** One line when it fits; otherwise two lines broken after punctuation or before "and", "but", "that"…, never after "the", "a", "my" and the like. Two short sentences said back to back share a subtitle, one per line, and short gaps between lines are closed so the screen doesn't blink. Subtitles that went by faster than you can read dropped from 15% to 4%.
- **No more "Thanks for watching."** Whisper's habit of inventing YouTube outros and subtitle credits over music is filtered out.
