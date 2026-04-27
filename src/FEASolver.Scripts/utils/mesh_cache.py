"""
mesh_cache.py
-------------
Hash-based mesh cache. Skips re-meshing if STEP file + mesh parameters
are unchanged since the last successful mesh.

Cache state stored in: {output_dir}/.mesh_cache

Key components:
  - SHA-256 of STEP file bytes
  - element_size, order, algorithm (mesh parameters)
"""

from __future__ import annotations
import hashlib
import json
from pathlib import Path


CACHE_FILE = ".mesh_cache"


class MeshCache:
    def __init__(self, work_dir: Path):
        self._path = work_dir / CACHE_FILE

    def make_key(
        self,
        step_path: str,
        element_size: float,
        order: int,
        algorithm: int,
        extra: str = "",
    ) -> str:
        # Format with ":.10g" so the same number produces the same string as
        # the C# side (which uses "G10"). Without this, Python's str(5.0) → "5.0"
        # and C#'s default ToString → "5" diverge and the cache never hits.
        step_hash = _sha256_file(step_path)
        size_str = f"{element_size:.10g}"
        payload = f"{step_hash}|{size_str}|{order}|{algorithm}|{extra}"
        return hashlib.sha256(payload.encode()).hexdigest()

    def is_valid(self, key: str) -> bool:
        if not self._path.exists():
            return False
        try:
            data = json.loads(self._path.read_text())
            return data.get("key") == key
        except Exception:
            return False

    def save(self, key: str) -> None:
        self._path.write_text(json.dumps({"key": key}))

    def invalidate(self) -> None:
        if self._path.exists():
            self._path.unlink()


def _sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()
