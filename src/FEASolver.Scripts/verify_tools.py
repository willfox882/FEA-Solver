"""
verify_tools.py
---------------
Run before first use to confirm the Python environment is complete.
Usage: python verify_tools.py [--ccx path/to/ccx.exe]
"""

import sys
import subprocess
import importlib
import argparse


def check(label: str, ok: bool, msg: str = "") -> bool:
    status = "OK  " if ok else "FAIL"
    print(f"  [{status}] {label}" + (f": {msg}" if msg else ""))
    return ok


def check_import(module: str) -> bool:
    try:
        importlib.import_module(module)
        return True
    except ImportError as e:
        return check(f"import {module}", False, str(e)) and False


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--ccx", default=None, help="Path to ccx.exe")
    args = p.parse_args()

    failures = 0
    print("\n=== FEA Solver — Tool Verification ===\n")

    # Python version
    major, minor = sys.version_info[:2]
    ok = major == 3 and minor >= 11
    failures += 0 if check(f"Python {major}.{minor}", ok,
                            "" if ok else "Need Python 3.11+") else 1

    # Required packages
    for pkg in ["gmsh", "numpy"]:
        if not check(f"package: {pkg}", check_import(pkg)):
            failures += 1

    # Optional: pythonocc
    try:
        from OCC.Core.BRep import BRep_Builder  # noqa
        check("package: pythonocc-core", True)
    except ImportError:
        check("package: pythonocc-core", False,
              "Not required for basic meshing; install via conda for STEP pre-processing")

    # Gmsh functional test
    try:
        import gmsh
        gmsh.initialize()
        gmsh.model.add("test")
        gmsh.model.occ.addBox(0, 0, 0, 1, 1, 1)
        gmsh.model.occ.synchronize()
        gmsh.model.mesh.generate(3)
        nt, _, _ = gmsh.model.mesh.getNodes()
        gmsh.finalize()
        check(f"gmsh mesh test ({len(nt)} nodes)", True)
    except Exception as e:
        failures += 1
        check("gmsh mesh test", False, str(e))

    # CalculiX
    if args.ccx:
        try:
            result = subprocess.run([args.ccx, "-v"], capture_output=True, timeout=5)
            # ccx may not support -v; any launch is a pass
            check(f"ccx executable ({args.ccx})", True)
        except FileNotFoundError:
            failures += 1
            check(f"ccx executable ({args.ccx})", False, "File not found")
        except Exception as e:
            failures += 1
            check(f"ccx executable ({args.ccx})", False, str(e))
    else:
        check("ccx executable", True, "Skipped (use --ccx path/to/ccx.exe to test)")

    print()
    if failures == 0:
        print("All checks passed.\n")
        sys.exit(0)
    else:
        print(f"{failures} check(s) failed. See messages above.\n")
        sys.exit(1)


if __name__ == "__main__":
    main()
