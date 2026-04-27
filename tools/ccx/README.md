# CalculiX Binaries

Place the following files here (`tools/ccx/`):

```
tools/ccx/
  ccx.exe          ← CalculiX main executable (Windows x64)
  spooles.dll      ← required by ccx
  arpack.dll       ← required by ccx (for modal; needed even for static)
  ccx_2.21.exe     ← (rename to ccx.exe after download)
```

## Download

Pre-compiled Windows binaries:
- https://www.calculix.de/ → Downloads → Windows
- Or use the unofficial build: https://github.com/calculix/ccx_windows

Tested version: **CalculiX 2.21**

## Verify

```powershell
.\tools\ccx\ccx.exe -v
# Expected: prints version string or usage
```

Or via the Python verify script:
```powershell
python src\FEASolver.Scripts\verify_tools.py --ccx tools\ccx\ccx.exe
```

## Note

`ccx.exe` is NOT included in this repo (binary redistribution).
The path is configured in `src/FEASolver.App/appsettings.json`.
