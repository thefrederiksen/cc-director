"""Entry point for cc_director_service executable."""

import sys
from pathlib import Path

# Add the scheduler directory to path for imports
scheduler_dir = Path(__file__).parent
if str(scheduler_dir) not in sys.path:
    sys.path.insert(0, str(scheduler_dir))

from cc_director.service import main

if __name__ == "__main__":
    main()
