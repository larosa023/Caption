## Caption 0.1.1

Much more accurate captions that keep up with the speaker.

- **Whole sentences, not fragments.** Whisper now always hears the sentence so far, with the previous lines as context, instead of short clips cut at every little pause. Far fewer wrong or made-up words, and better names, casing and punctuation.
- **Words appear as they're spoken.** The line is re-read every 0.3 s. Each word turns solid as soon as two reads agree on it; only the newest few words stay dimmed while they might still change. Lines end at real sentence ends.
- **More accurate on the GPU.** Beam search is on when Caption runs on your GPU.
- **"English (Whisper)" only translates speech that isn't English.** English stays as it was spoken; translating it again made captions worse.
- Your listening settings move to the new defaults (update every 0.3 s, a 0.9 s pause ends a line) unless you had changed them.
