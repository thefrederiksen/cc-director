# Runtime hook to add src modules to path
import sys
import os

# Get the directory where the frozen app is running
if getattr(sys, 'frozen', False):
    base_path = sys._MEIPASS
else:
    base_path = os.path.dirname(os.path.abspath(__file__))

# Add the base path to sys.path so modules can be imported
if base_path not in sys.path:
    sys.path.insert(0, base_path)
