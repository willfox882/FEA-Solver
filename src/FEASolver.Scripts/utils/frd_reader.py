"""
frd_reader.py
-------------
Parses CalculiX ASCII .frd file.
Extracts: displacement (U) and von Mises stress (S).

.frd format key blocks:
  -1 node_id x y z          ← node coordinates (ignored here)
  -4 DISP  4  1             ← result header: name, n_components
  -5 D1                     ← component name
  -1 node_id u1 u2 u3 ...  ← values per node
  -3                         ← end block
"""

import re
import math
import logging
from pathlib import Path
from typing import Any

log = logging.getLogger(__name__)


class FrdReader:
    def __init__(self, frd_path: str):
        self.path = Path(frd_path)

    def parse(self) -> dict[str, Any]:
        raw = self.path.read_bytes()

        # Detect binary .frd — ASCII files start with printable/whitespace chars
        # Binary .frd starts with a Fortran record-length marker (4 bytes)
        if len(raw) > 10:
            head = raw[:80]
            non_text = sum(1 for b in head if b < 9 or (b > 13 and b < 32))
            if non_text > 5:
                raise ValueError(
                    f"Binary .frd file detected: {self.path}\n"
                    "FEA Solver requires ASCII .frd output. "
                    "This may be caused by a CalculiX build that defaults to binary. "
                    "Try a different CalculiX build or add OUTPUT=3D to *NODE FILE / *EL FILE.")

        lines = raw.decode("latin-1").splitlines()
        log.info("FRD file: %d lines from %s", len(lines), self.path.name)

        disp_node_ids: list[int] = []
        disp_x: list[float] = []
        disp_y: list[float] = []
        disp_z: list[float] = []
        stress_node_ids: list[int] = []
        vonmises: list[float] = []
        s11: list[float] = []
        s22: list[float] = []
        s33: list[float] = []
        s12: list[float] = []
        s23: list[float] = []
        s13: list[float] = []
        strain_node_ids: list[int] = []
        e11: list[float] = []
        e22: list[float] = []
        e33: list[float] = []
        e12: list[float] = []
        e23: list[float] = []
        e13: list[float] = []
        strain_vm: list[float] = []
        rf_node_ids: list[int] = []
        rf_x: list[float] = []
        rf_y: list[float] = []
        rf_z: list[float] = []

        i = 0
        while i < len(lines):
            line = lines[i]

            # Result block header
            if line.strip().startswith("-4"):
                parts = line.split()
                block_name = parts[1] if len(parts) > 1 else ""
                log.debug("FRD block header: %r (name=%s)", line.strip(), block_name)

                bn = block_name.upper()
                if "DISP" in bn or bn == "U":
                    i += 1
                    raw_nids, vals = self._read_result_block(lines, i)
                    for nid, v in zip(raw_nids, vals):
                        if len(v) >= 3:
                            disp_node_ids.append(nid)
                            disp_x.append(v[0])
                            disp_y.append(v[1])
                            disp_z.append(v[2])
                    log.info("DISP block: %d nodes parsed (of %d raw)",
                             len(disp_node_ids), len(raw_nids))

                elif "STRESS" in bn or bn == "S":
                    i += 1
                    raw_nids, vals = self._read_result_block(lines, i)
                    for nid, v in zip(raw_nids, vals):
                        if len(v) >= 6:
                            stress_node_ids.append(nid)
                            s11.append(v[0]); s22.append(v[1]); s33.append(v[2])
                            s12.append(v[3]); s23.append(v[4]); s13.append(v[5])
                            vonmises.append(_vonmises(*v[:6]))
                    log.info("STRESS block: %d nodes parsed (of %d raw)",
                             len(stress_node_ids), len(raw_nids))

                elif "TOSTRAIN" in bn or bn in ("E", "STRAIN") or "TOTSTRA" in bn:
                    # CalculiX block name is "TOSTRAIN" (total strain).
                    # Components E11..E13 in same order as S; CCX emits engineering
                    # shears already halved (tensor convention), so we treat them
                    # as tensor components and use the tensor-strain von Mises form.
                    i += 1
                    raw_nids, vals = self._read_result_block(lines, i)
                    for nid, v in zip(raw_nids, vals):
                        if len(v) >= 6:
                            strain_node_ids.append(nid)
                            e11.append(v[0]); e22.append(v[1]); e33.append(v[2])
                            e12.append(v[3]); e23.append(v[4]); e13.append(v[5])
                            strain_vm.append(_strain_vonmises(*v[:6]))
                    log.info("STRAIN block: %d nodes parsed (of %d raw)",
                             len(strain_node_ids), len(raw_nids))

                elif bn in ("FORC", "RF") or "FORC" in bn:
                    # CalculiX writes reaction forces under FORC block name
                    i += 1
                    raw_nids, vals = self._read_result_block(lines, i)
                    for nid, v in zip(raw_nids, vals):
                        if len(v) >= 3:
                            rf_node_ids.append(nid)
                            rf_x.append(v[0]); rf_y.append(v[1]); rf_z.append(v[2])
                    log.info("FORC block: %d nodes parsed (of %d raw)",
                             len(rf_node_ids), len(raw_nids))

            i += 1

        if not disp_node_ids and not stress_node_ids:
            log.warning("FRD parser found NO result data in %s (%d lines). "
                        "Check that CalculiX produced valid output.",
                        self.path.name, len(lines))

        disp_mag = [
            math.sqrt(x*x + y*y + z*z)
            for x, y, z in zip(disp_x, disp_y, disp_z)
        ] if disp_x else []

        return {
            "step": 1,
            "displacement_x":   {"node_ids": disp_node_ids,   "values": disp_x},
            "displacement_y":   {"node_ids": disp_node_ids,   "values": disp_y},
            "displacement_z":   {"node_ids": disp_node_ids,   "values": disp_z},
            "displacement_mag": {"node_ids": disp_node_ids,   "values": disp_mag},
            "vonmises":         {"node_ids": stress_node_ids, "values": vonmises},
            # Full Cauchy stress tensor components (Pa) — needed for principal
            # stresses and any user-defined stress quantity
            "s11": {"node_ids": stress_node_ids, "values": s11},
            "s22": {"node_ids": stress_node_ids, "values": s22},
            "s33": {"node_ids": stress_node_ids, "values": s33},
            "s12": {"node_ids": stress_node_ids, "values": s12},
            "s23": {"node_ids": stress_node_ids, "values": s23},
            "s13": {"node_ids": stress_node_ids, "values": s13},
            # Nodal reaction forces (N) — used post-solve to verify
            # Σ(reactions) ≈ -Σ(applied loads), the primary equilibrium check
            "reaction_x": {"node_ids": rf_node_ids, "values": rf_x},
            "reaction_y": {"node_ids": rf_node_ids, "values": rf_y},
            "reaction_z": {"node_ids": rf_node_ids, "values": rf_z},
            # Strain tensor components (dimensionless). Shears are tensor
            # components (γ_ij / 2), matching CalculiX TOSTRAIN output.
            "e11": {"node_ids": strain_node_ids, "values": e11},
            "e22": {"node_ids": strain_node_ids, "values": e22},
            "e33": {"node_ids": strain_node_ids, "values": e33},
            "e12": {"node_ids": strain_node_ids, "values": e12},
            "e23": {"node_ids": strain_node_ids, "values": e23},
            "e13": {"node_ids": strain_node_ids, "values": e13},
            "strain_vonmises": {"node_ids": strain_node_ids, "values": strain_vm},
        }

    def _read_result_block(
        self, lines: list[str], start: int
    ) -> tuple[list[int], list[list[float]]]:
        """
        Read result data lines until -3 end marker.
        CalculiX .frd result lines use fixed-width columns:
          cols 0-2  : record type (' -1')
          cols 3-12 : node id (10 chars)
          cols 13+  : values in 12-char fields
        Returns (node_ids, [[v1, v2, ...], ...])
        """
        node_ids: list[int] = []
        values: list[list[float]] = []
        i = start
        while i < len(lines):
            line = lines[i]
            stripped = line.strip()
            if stripped.startswith("-3"):
                break
            if stripped.startswith("-1"):
                try:
                    nid = int(line[3:13])
                    val_str = line[13:]
                    vals = _parse_fixed_floats(val_str)
                    node_ids.append(nid)
                    values.append(vals)
                except (ValueError, IndexError):
                    pass
            i += 1
        return node_ids, values


# CalculiX FRD numbers are in the format:
#   [-][0-9].[0-9]{5}E[+-][0-9]{2,3}
# Windows ccx builds emit 3-digit exponents (E-007); Linux/Mac builds emit 2-digit
# (E-07). Values are packed with no separators: positive values occupy 12/11 chars,
# negative 13/12 (leading minus). This asymmetry makes fixed-width slicing unreliable;
# the sign-boundary regex also fails when a negative value is immediately followed by a
# positive one (no sign between the trailing exponent digit and the next mantissa).
# Solution: match the literal CCX number format directly.
#
# audit-008: the exponent is {2,3} digits, but the optional 3rd digit must NOT be the
# leading digit of the next packed value. Every CCX mantissa begins "digit.", so the
# (?!\.) lookahead refuses a 3rd exponent digit when it is immediately followed by a
# dot — that digit belongs to the following number. A genuine 3-digit exponent's last
# digit is never followed by '.', so legitimate matches are unaffected.
_CCX_NUM_RE = re.compile(r'-?[0-9]\.[0-9]{5}E[+-][0-9]{2}(?:[0-9](?!\.))?')


def _parse_fixed_floats(s: str) -> list[float]:
    """
    Parse CalculiX FRD result values from a raw (whitespace-stripped) data line suffix.

    CalculiX writes values in a 12E3 format (1 sign char + 1 digit + dot + 5 digits +
    E + 1 sign char + 3 exponent digits).  Positive values consume 12 chars; negative
    values consume 13 chars.  The fixed-width boundary therefore shifts by 1 after every
    negative value, which breaks naive 12-char slicing.  The sign-boundary regex also
    fails when a positive value immediately follows a negative one (no separator sign).

    Strategy:
      1) Match all substrings that conform to the CCX 12E3 format.  This is exact and
         order-preserving — no boundary ambiguity.
      2) If no CCX-format numbers are found (non-standard or older FRD variant), fall
         back to the sign-boundary split, then to 12-char slicing as a last resort.
    """
    if not s or not s.strip():
        return []

    # Primary: direct pattern match — handles all sign combinations without ambiguity.
    matches = _CCX_NUM_RE.findall(s)
    if matches:
        result = []
        for m in matches:
            try:
                result.append(float(m))
            except ValueError:
                pass
        if result:
            return result

    # Fallback 1: sign-boundary split (handles non-CCX scientific notation).
    spaced = re.sub(r'(?<=\d)([-+])(?=\d)', r' \1', s)
    parts = spaced.split()
    result = []
    for p in parts:
        try:
            result.append(float(p))
        except ValueError:
            pass
    if result:
        return result

    # Fallback 2: 12-char fixed-width slicing (last resort, may be inaccurate for
    # negative values but better than returning nothing for unrecognised formats).
    for j in range(0, len(s), 12):
        chunk = s[j:j+12].strip()
        if chunk:
            try:
                result.append(float(chunk))
            except ValueError:
                pass
    return result


def _vonmises(s11: float, s22: float, s33: float,
              s12: float, s23: float, s13: float) -> float:
    return math.sqrt(0.5 * (
        (s11 - s22)**2 + (s22 - s33)**2 + (s33 - s11)**2 +
        6.0 * (s12**2 + s23**2 + s13**2)
    ))


def _strain_vonmises(e11: float, e22: float, e33: float,
                     e12: float, e23: float, e13: float) -> float:
    """Equivalent (von Mises) strain for tensor-strain components.

    ε_vm = sqrt( (2/3) · ε_dev:ε_dev )
         = sqrt( (2/9) · [ (ε11-ε22)² + (ε22-ε33)² + (ε33-ε11)² ]
                 + (4/3) · (ε12² + ε23² + ε13²) )         (tensor shears)

    For incompressible uniaxial (ε22=ε33=-ε11/2): ε_vm = |ε_axial|.

    NOTE (audit-003): the normal-difference term coefficient is 2/9, not 1/3.
    The previous (1/3) form over-stated normal strains by sqrt(3/2) ≈ 1.225×
    and contradicted the incompressible-uniaxial identity above.
    """
    dev = (e11 - e22) ** 2 + (e22 - e33) ** 2 + (e33 - e11) ** 2
    shear = e12 * e12 + e23 * e23 + e13 * e13
    return math.sqrt((2.0 / 9.0) * dev + (4.0 / 3.0) * shear)
