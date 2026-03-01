---
# Q4 2025 Quarterly Report
## Example Corp

---
# Agenda

- Financial Highlights
- Key Deliverables
- Team Performance
  - Engineering
  - Sales
- Next Quarter Goals

> This slide sets the stage for the quarterly review meeting.

---
# Financial Highlights

---
# Revenue Summary

| Metric | Q3 2025 | Q4 2025 | Change |
|--------|---------|---------|--------|
| Revenue | $2.1M | $2.8M | +33% |
| Expenses | $1.4M | $1.6M | +14% |
| Profit | $700K | $1.2M | +71% |
| Headcount | 42 | 48 | +6 |

> Revenue growth driven primarily by new enterprise contracts.

---
# Key Deliverables

- Launched cc-tools CLI suite (8 tools shipped)
- Completed mindzie Studio v3.0 migration
- Onboarded 12 new enterprise clients
  - 4 in healthcare
  - 5 in financial services
  - 3 in manufacturing
- Achieved 99.9% platform uptime

---
# Architecture Overview

```python
class PipelineOrchestrator:
    """Central pipeline for data processing."""

    def __init__(self, config: Config):
        self.stages = []
        self.config = config

    def add_stage(self, stage: Stage):
        self.stages.append(stage)

    def execute(self):
        for stage in self.stages:
            stage.run(self.config)
```

> This is the core pipeline pattern used across all our tools.

---
# Next Quarter Goals

- Ship cc-powerpoint tool
- Expand to 60+ enterprise clients
- Launch self-service analytics portal
  - Phase 1: Dashboard builder
  - Phase 2: Report automation
- Hire 8 additional engineers

> Focus on scalability and self-service capabilities.

---
# Thank You

---
