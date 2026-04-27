"""
write_inp.py
------------
Convert FEAModel JSON → CalculiX .inp file.

Usage:
  python write_inp.py --model fea_model.json --output model.inp
"""

import argparse
import sys
import logging
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from utils.mesh_io import fea_model_from_json
from utils.inp_writer import InpWriterV2

logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s", stream=sys.stderr)
log = logging.getLogger(__name__)


def parse_args():
    p = argparse.ArgumentParser()
    p.add_argument("--model", required=True)
    p.add_argument("--output", required=True)
    return p.parse_args()


if __name__ == "__main__":
    args = parse_args()
    with open(args.model) as f:
        model = fea_model_from_json(f.read())

    writer = InpWriterV2(model)
    writer.write(args.output)
    log.info(f"Wrote: {args.output}")
