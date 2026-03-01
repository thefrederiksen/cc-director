# cc-photos

Photo organization tool: scan, categorize, detect duplicates and screenshots, AI descriptions.

## Features

- **Source Management**: Configure multiple photo directories with categories and priorities
- **Scanning**: Recursively scan directories for images (JPG, PNG, GIF, BMP, WEBP, HEIC, HEIF)
- **Duplicate Detection**: Find exact duplicates using SHA-256 hashing
- **Screenshot Detection**: Multi-layer detection using filename patterns, dimensions, and metadata
- **AI Analysis**: Generate descriptions using OpenAI Vision or Claude Code CLI
- **Search**: Full-text search across AI-generated descriptions

## Installation

```bash
pip install -e .
```

Or use the pre-built executable `cc-photos` (on PATH, installed to `%LOCALAPPDATA%\cc-director\bin\`).

## Prerequisites

cc-photos requires cc-vault to be installed. All photo data is stored in the central vault database.

```bash
# Ensure cc-vault is available
pip install -e ../cc-vault
```

## Usage

### Quick Start: Discovery and Initialization

The recommended workflow is to first discover where photos are located, then initialize those directories:

```bash
# Step 1: Discover where photos are on a drive (read-only, no database changes)
cc-photos discover D:\
cc-photos discover "C:\Users\me" --top 30 --min 10

# Step 2: Initialize directories you want to track
cc-photos init "D:\Photos" --category private
cc-photos init "D:\Work\Screenshots" --category work --label "Work Screenshots"
```

The `discover` command shows you:
- Total photos found
- Top directories by photo count
- Size information

The `init` command:
- Adds the directory as a source
- Immediately scans and indexes all photos
- Extracts metadata and detects screenshots
- Computes hashes for duplicate detection

**Note:** Scanning is FREE and FAST. AI analysis (the `analyze` command) costs money and is done separately.

### Source Management

```bash
# Add a source (manual way, without scanning)
cc-photos source add "D:\Photos" --category private --label "Family" --priority 1

# List sources
cc-photos source list

# Remove a source
cc-photos source remove "Family"
```

### Scanning

```bash
# Scan all sources (re-scan for changes)
cc-photos scan

# Scan specific source
cc-photos scan --source "Family"
```

### Duplicates

```bash
# List duplicate groups
cc-photos dupes

# Auto-remove duplicates (keeps highest priority)
cc-photos dupes --cleanup

# Interactive review
cc-photos dupes --review

# Dry run (show what would be deleted)
cc-photos dupes --cleanup --dry-run
```

### Search & Filter

```bash
# Search AI descriptions
cc-photos search "beach vacation"

# List by category
cc-photos list --category private

# List by source
cc-photos list --source "Family"

# List screenshots
cc-photos list --screenshots
```

### AI Analysis

```bash
# Analyze unanalyzed images
cc-photos analyze

# Analyze with limit
cc-photos analyze --limit 50

# Use specific provider
cc-photos analyze --provider openai
```

### Statistics

```bash
cc-photos stats
```

## Two Modes: Scanning vs AI Analysis

cc-photos has two distinct modes of operation:

### Mode 1: Scanning (FREE, FAST)

Commands: `discover`, `init`, `scan`, `dupes`, `list`, `stats`

- Find all image files recursively
- Extract EXIF metadata (camera, date, GPS, dimensions)
- Compute SHA-256 hash for duplicate detection
- Detect screenshots using heuristics
- Track file sizes and modification times

Speed: ~100-500 photos/second
Cost: $0

### Mode 2: AI Analysis (EXPENSIVE, SLOW)

Command: `analyze`

- Call OpenAI Vision API for each photo
- Generate natural language descriptions
- Extract keywords for search

Speed: ~1-2 photos/second (API rate limits)
Cost: ~$0.01-0.03 per photo (1000 photos = $10-30)

**Recommendation:** Run scanning first to index everything, then analyze photos in batches:

```bash
cc-photos analyze --limit 100   # Analyze 100 photos
cc-photos analyze --limit 50    # Do another batch later
```

## Categories

- **private**: Personal photos
- **work**: Work-related photos
- **other**: Everything else

## Priority

Lower priority number = higher priority. When duplicates exist, the copy in the highest-priority source is kept.

## Database

All photo data is stored in the central vault database (`~/.vault/vault.db`):

- **photo_sources**: Configured source directories
- **photos**: Image records with file info, SHA-256 hash, screenshot flag
- **photo_metadata**: EXIF metadata (dimensions, camera, GPS, date taken)
- **photo_analysis**: AI analysis results (description, keywords)

This integration allows photos to be cross-referenced with other vault entities (contacts, goals, tasks, etc.) through the entity linking system.
