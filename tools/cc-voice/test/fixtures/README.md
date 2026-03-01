# Test Fixtures: cc-voice

## Overview
Text input files for testing text-to-speech (TTS) generation. Fixtures provide
various text samples that exercise different speech synthesis scenarios.

## Fixtures
- `short_sentence.txt` - Single short sentence for basic TTS validation
- `long_paragraph.txt` - Multi-sentence paragraph for continuous speech generation
- `special_characters.txt` - Text with numbers, abbreviations, and punctuation
- `empty_input.txt` - Empty file for error handling validation
- `multilingual.txt` - Text with mixed language segments (if supported)
- `ssml_input.xml` - SSML-formatted input for advanced speech control (if supported)

## Notes
- TTS output is audio, so automated validation focuses on:
  - Output file exists and has non-zero size
  - Output file is a valid audio format (mp3, wav, etc.)
  - Duration is proportional to input text length
- Exact audio content cannot be deterministically compared across runs
- Special characters test validates handling of "$100", "Dr.", "3rd", etc.
- Empty input test should produce a clear error, not a silent/corrupt file
- Keep text inputs short to minimize TTS API costs during testing

## Last Validated
Date: 2026-02-26
