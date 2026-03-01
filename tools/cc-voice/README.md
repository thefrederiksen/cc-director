# cc-voice

Text-to-speech using OpenAI TTS.

Part of the [CC Tools](../../README.md) suite.

## Features

- **Multiple Voices:** 6 distinct voices to choose from
- **High Quality:** Standard and HD models
- **Long Text Support:** Automatically handles text longer than API limits
- **Markdown Cleaning:** Removes formatting for natural speech

## Installation

Download from the releases page or install from source:

```bash
pip install -e .
```

## Requirements

Set `OPENAI_API_KEY` environment variable.

## Usage

```bash
# Convert text directly
cc-voice "Hello, world!" -o hello.mp3

# Convert from file
cc-voice document.txt -o narration.mp3

# Choose voice
cc-voice "Welcome to the show" -o intro.mp3 --voice nova

# HD quality
cc-voice "Important announcement" -o announcement.mp3 --model tts-1-hd

# Adjust speed
cc-voice "This is slow" -o slow.mp3 --speed 0.75
cc-voice "This is fast" -o fast.mp3 --speed 1.5
```

## Voices

| Voice | Description |
|-------|-------------|
| alloy | Neutral, balanced |
| echo | Warm, conversational |
| fable | Expressive, storytelling |
| nova | Energetic, youthful |
| onyx | Deep, authoritative (default) |
| shimmer | Clear, gentle |

## Options

| Option | Description | Default |
|--------|-------------|---------|
| `-o, --output` | Output file path | Required |
| `-v, --voice` | Voice selection | onyx |
| `-m, --model` | tts-1 or tts-1-hd | tts-1 |
| `-s, --speed` | 0.25 to 4.0 | 1.0 |
| `--raw` | Don't clean markdown | False |

## License

MIT License - see [LICENSE](../../LICENSE)
