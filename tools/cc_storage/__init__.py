"""Centralized storage path resolution for cc-director.

All cc-director tools and apps should use CcStorage to resolve storage paths
instead of computing paths themselves. This ensures a single source of truth
for all storage locations.
"""

__version__ = "0.1.0"

from .storage import CcStorage

__all__ = ["CcStorage"]
