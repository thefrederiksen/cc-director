"""Jittered delay helpers for Spotify automation."""

import random
import time


def jittered_sleep(base: float) -> None:
    """Sleep with random jitter. Tiered by base duration to avoid breaking fast loops.

    Tiers:
        < 1.0s base  ->  0 - 0.5s jitter  (keyboard shortcuts, quick checks)
        1.0 - 2.9s   ->  0 - 1.5s jitter  (click waits, DOM reads)
        >= 3.0s       ->  0 - 2.0s jitter  (navigation, page loads)
    """
    if base < 1.0:
        jitter = random.uniform(0, 0.5)
    elif base < 3.0:
        jitter = random.uniform(0, 1.5)
    else:
        jitter = random.uniform(0, 2.0)
    time.sleep(base + jitter)
