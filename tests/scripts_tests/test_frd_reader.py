"""pytest tests for frd_reader.py"""
import sys
import os
import math
import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "../../src/FEASolver.Scripts"))
from utils.frd_reader import FrdReader, _vonmises


SAMPLE_FRD = """\
    1UNODE                 0
 -1         1 0.00000E+00 0.00000E+00 0.00000E+00
 -1         2 1.00000E+00 0.00000E+00 0.00000E+00
 -3
 -4 DISP       4  1
 -5  D1
 -5  D2
 -5  D3
 -5  ALL
 -1         1 0.00000E+00 0.00000E+00-1.00000E-04
 -1         2 0.00000E+00 0.00000E+00-2.00000E-04
 -3
 -4 STRESS     6  1
 -5  SXX
 -5  SYY
 -5  SZZ
 -5  SXY
 -5  SYZ
 -5  SZX
 -1         1 1.00000E+06 5.00000E+05 5.00000E+05 0.00000E+00 0.00000E+00 0.00000E+00
 -1         2 2.00000E+06 1.00000E+06 1.00000E+06 0.00000E+00 0.00000E+00 0.00000E+00
 -3
"""


def test_vonmises_pure_tension():
    vm = _vonmises(1e6, 0, 0, 0, 0, 0)
    assert abs(vm - 1e6) < 1.0


def test_parse_frd(tmp_path):
    frd_file = tmp_path / "model.frd"
    frd_file.write_text(SAMPLE_FRD)
    reader = FrdReader(str(frd_file))
    result = reader.parse()

    assert result["displacement_z"]["values"][0] == pytest.approx(-1e-4)
    assert result["displacement_z"]["values"][1] == pytest.approx(-2e-4)
    assert len(result["vonmises"]["values"]) == 2
    assert result["vonmises"]["values"][0] > 0
