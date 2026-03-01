# Test Fixtures: cc-transcribe

## Overview
Small media files for testing video transcription functionality. Fixtures
include short audio/video clips with known spoken content for validating
transcription accuracy.

## Fixtures
- `short_speech.mp4` - 5-second video clip with clear spoken English
- `short_speech.wav` - Audio-only version of the same speech content
- `silence.mp4` - 3-second clip with no speech (tests empty transcript handling)
- `multi_speaker.mp4` - Short clip with two speakers for diarization testing
- `expected_outputs/` - Directory containing reference transcripts:
  - `short_speech_expected.txt` - Expected transcript for short_speech files
  - `multi_speaker_expected.txt` - Expected transcript with speaker labels

## Notes
- ALL fixture files must be under 1MB to keep the repository lightweight
- Generate test clips with ffmpeg if needed:
  `ffmpeg -f lavfi -i "sine=frequency=440:duration=3" -t 3 silence.mp4`
- Transcription accuracy varies by model; use fuzzy matching for validation
- Test both video (mp4) and audio-only (wav) input paths
- Ensure clips have clear audio with minimal background noise
- Multi-speaker test validates speaker separation, not exact label assignment

## Last Validated
Date: 2026-02-26
