## Caption 0.3.3

**Ripple subtitles stay in step.**

- **Switching audio tracks no longer breaks the subtitles.** Caption now starts over on the new track behind the same subtitle address, so Ripple keeps one "AI subtitles" track that simply fills in again (needs Ripple 1.2.3).
- **No more starting over from the beginning by mistake.** A stray "position 0" from the player while a stream reloads used to make Caption throw away its place and re-do the movie from 0:00. A jump now has to be confirmed by a second report.
- **Accurate "ready to" time.** The status line shows how far subtitles are ready from where you're actually watching.
