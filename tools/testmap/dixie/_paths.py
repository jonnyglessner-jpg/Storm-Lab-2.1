"""Puts the test-map tools on the path and selects the Dixie map before map_config is imported."""
import sys
from pathlib import Path

if "--map" not in sys.argv:
    sys.argv[1:1] = ["--map", "dixie"]
sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(1, str(Path(__file__).resolve().parents[1]))
