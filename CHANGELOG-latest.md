## Caption 0.3.1

**Ripple subtitles keep up from the first minute.**

- **No more gap after the first line.** Caption used to wait for 75 seconds of audio before making the next batch of subtitles, which on big remux files meant playback overtook it. It now works in short pieces while playback is close behind and switches to longer ones once it's comfortably ahead.
- **Clearer log.** Each step of a Ripple subtitle job (how long the audio took to download and transcribe, and how far ahead of playback it is) is written to the log, along with when a job stops.
