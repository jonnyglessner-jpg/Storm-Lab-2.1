"""Puts the test-map tools on the path. The real-data tools need --map dixie or --map midwest (map_config reads it)."""
import sys
from pathlib import Path

if "--map" not in sys.argv:
    raise SystemExit("which map? add --map dixie or --map midwest")
sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(1, str(Path(__file__).resolve().parents[1]))
