# pcb1-golden

The goldens of the configuration code PCB1 come from the OLD code (parts-life 0.7.4, pl-cloud commit `25e4571`), not from the new one.
The harness runs the old `BuildCollector.Collect` and `BuildCode.Make` on synthetic material (`tests/PcMieru.Checks.Tests/Fixtures/pcb1/*.material.json`; no real name, serial, path or label).

1. `powershell -File tools\pcb1-golden\build-harness.ps1 -OldRepo <pl-cloud>` builds the harness in `%TEMP%\pcmieru-pcb1-harness` from a `git archive` (never inside a working tree).
2. `Pcb1Golden.exe golden <Fixtures\pcb1> <Fixtures\pcb1>` writes `<case>.code.txt`, `<case>.json` and `<case>.notes.txt` next to each material.
3. `replay <material> <code>` and `windows <code>` are for `Pcb1RealMachineTests` (old code on a replayed material, and on this machine).

Change a material only with a reason: the goldens are the contract of the code format.
