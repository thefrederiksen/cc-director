# Test Fixtures: cc-whisper

## Overview
Small audio clips for testing Whisper-based audio transcription. Fixtures
include short recordings with known spoken content for validating
transcription accuracy and format handling.

## Fixtures
- `clear_speech.wav` - 5-second WAV clip with clearly spoken English sentence
- `clear_speech.mp3` - Same content as WAV, MP3 format for format handling test
- `noisy_speech.wav` - Speech with background noise for robustness testing
- `silence.wav` - 3-second silent audio (tests empty transcript handling)
- `multi_language.wav` - Short clip in non-English language for language detection
- `expected_outputs/` - Directory containing reference transcripts:
  - `clear_speech_expected.txt` - Expected transcript for clear_speech files
  - `noisy_speech_expected.txt` - Expected transcript (may have lower accuracy)

## Notes
- ALL fixture files must be small (under 1MB each) to keep the repo lightweight
- Generate test audio with ffmpeg if needed:
  `ffmpeg -f lavfi -i "sine=frequency=440:duration=3" silence.wav`
- Use text-to-speech to generate clips with known content for deterministic testing
- Whisper output varies slightly between runs; use fuzzy/keyword matching
- Test both WAV and MP3 inputs to validate format conversion
- Silence test should return an empty or near-empty transcript without errors

## Last Validated
Date: 2026-02-26
