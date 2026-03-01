# Test Fixtures: cc-video

## Overview
Small video clips for testing video utility commands including extract-audio
and info metadata retrieval.

## Fixtures
- `sample_h264.mp4` - Short H.264 encoded video with audio track (under 500KB)
- `sample_no_audio.mp4` - Video-only file with no audio stream (tests extract-audio error handling)
- `sample_hevc.mp4` - HEVC/H.265 encoded video for codec compatibility testing
- `sample_with_metadata.mp4` - Video with title, artist, and creation date metadata tags
- `expected_outputs/` - Directory containing reference outputs:
  - `sample_h264_info.json` - Expected info command output for sample_h264.mp4
  - `sample_with_metadata_info.json` - Expected metadata fields and values

## Notes
- ALL fixture files must be small (under 1MB each) to keep the repo lightweight
- Generate minimal test videos with ffmpeg if needed:
  `ffmpeg -f lavfi -i "testsrc=duration=2:size=320x240:rate=15" -f lavfi -i "sine=frequency=440:duration=2" -c:v libx264 -c:a aac -shortest sample_h264.mp4`
- extract-audio tests should verify the output audio file exists and has correct format
- info tests should validate that duration, resolution, codec, and bitrate are parsed
- Test the error path when extract-audio is called on a video with no audio stream

## Last Validated
Date: 2026-02-26
