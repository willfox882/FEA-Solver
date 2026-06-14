"""pytest tests for frd_reader.py"""
import sys
import os
import math
import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "../../src/FEASolver.Scripts"))
from utils.frd_reader import (
    FrdReader, _vonmises, _strain_vonmises, _parse_fixed_floats,
)


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


# ── audit-003: equivalent (von Mises) strain ────────────────────────────────

def _eq_strain_reference(e11, e22, e33, e12, e23, e13):
    """Independent reference: ε_eq = sqrt(2/3 · ε_dev:ε_dev), tensor shears."""
    tr = (e11 + e22 + e33) / 3.0
    d11, d22, d33 = e11 - tr, e22 - tr, e33 - tr
    devdev = d11*d11 + d22*d22 + d33*d33 + 2.0*(e12*e12 + e23*e23 + e13*e13)
    return math.sqrt(2.0 / 3.0 * devdev)


def test_strain_vonmises_incompressible_uniaxial_equals_axial():
    # Incompressible uniaxial: e11=e, e22=e33=-e/2 → ε_vm should equal |e|.
    e = 1.0e-3
    assert _strain_vonmises(e, -e/2, -e/2, 0, 0, 0) == pytest.approx(e, rel=1e-9)


@pytest.mark.parametrize("comps", [
    (1e-3, -5e-4, -5e-4, 0, 0, 0),
    (2e-3, 1e-3, -4e-4, 3e-4, -2e-4, 1e-4),
    (0, 0, 0, 7e-4, 0, 0),          # pure shear
    (-1e-3, -1e-3, -1e-3, 0, 0, 0), # hydrostatic → 0
])
def test_strain_vonmises_matches_deviatoric_reference(comps):
    assert _strain_vonmises(*comps) == pytest.approx(_eq_strain_reference(*comps), abs=1e-15)


def test_strain_vonmises_hydrostatic_is_zero():
    assert _strain_vonmises(5e-4, 5e-4, 5e-4, 0, 0, 0) == pytest.approx(0.0, abs=1e-15)


# ── audit-008: 2-digit (Linux/Mac) vs 3-digit (Windows) FRD exponents ────────

def test_parse_floats_two_digit_exponent():
    # Linux/Mac ccx emits 2-digit exponents; values packed (negative→positive, no space).
    vals = _parse_fixed_floats("0.00000E+00-1.00000E-041.00000E+00")
    assert vals == pytest.approx([0.0, -1e-4, 1.0])


def test_parse_floats_three_digit_exponent():
    # Windows ccx emits 3-digit exponents; the optional 3rd digit must be kept.
    vals = _parse_fixed_floats("-2.94935E-0071.23456E+002")
    assert vals == pytest.approx([-2.94935e-7, 1.23456e2])


def test_parse_floats_mixed_exponent_widths():
    # A 2-digit value packed before a 3-digit value must not steal a digit.
    vals = _parse_fixed_floats("1.50000E-05-3.00000E+012")
    assert vals == pytest.approx([1.5e-5, -3.0e12])


# 2-digit-exponent twin of SAMPLE_FRD — exercises full FrdReader.parse path.
SAMPLE_FRD_2DIGIT = """\
    1UNODE                 0
 -1         1 0.00000E+00 0.00000E+00 0.00000E+00
 -3
 -4 DISP       4  1
 -5  D1
 -5  D2
 -5  D3
 -5  ALL
 -1         1 0.00000E+00-1.00000E-041.00000E+00
 -1         2 0.00000E+00 0.00000E+00-2.00000E-04
 -3
"""


def test_parse_frd_two_digit_exponents(tmp_path):
    frd_file = tmp_path / "model.frd"
    frd_file.write_text(SAMPLE_FRD_2DIGIT)
    result = FrdReader(str(frd_file)).parse()
    assert result["displacement_y"]["values"][0] == pytest.approx(-1e-4)
    assert result["displacement_z"]["values"][0] == pytest.approx(1.0)
    assert result["displacement_z"]["values"][1] == pytest.approx(-2e-4)
