# Test Fixtures: cc-computer

## Overview

cc-computer is an AI agent requiring live desktop + LLM API. Fixtures provide reference output formats and evidence chain schemas.

## Fixtures

### expected/evidence_chain_schema.json
- **Purpose**: Validate evidence chain JSON structure
- **Tests**: Session logging produces correct schema with steps, actions, screenshots

### expected/activity_log_format.jsonl
- **Purpose**: Validate activity log line format
- **Tests**: Each line is valid JSON with expected event types

## Notes

Full integration tests require: Windows desktop, OPENAI_API_KEY, cc-click on PATH, a known target app (Notepad). Tests should verify the evidence chain file is created and well-formed after a simple task.

## Last Validated
Date: 2026-02-26
Tool Version: 1.0.0
