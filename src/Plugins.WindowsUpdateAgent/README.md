# Windows Update Agent USO Diagnostics

This plugin now contains a dedicated `USO Diagnostics` section for administrator-focused analysis of Windows Update / USO / WUfB SQLite databases such as `store.db`.

The diagnostics loader is host-oriented:

- local host: reads `ProgramData\\USOPrivate\\UpdateStore\\store.db`
- remote host: prefers SMB/UNC access to the same path
- remote fallback: uses PowerShell/WinRM to transfer `store.db`, `store.db-wal`, and `store.db-shm` when direct SMB access fails

## Database Interpretation Summary

The implementation treats the following tables as primary telemetry sources:

- `VARIABLES`
- `PROVIDERSPROP`
- `UPDATESPROP`
- `COMPLETEDUPDATES`
- `ACTIONRECORDS`
- `DOWNTIMEHISTORY`

Observed type handling used by the parser:

- `TYPE = 3`: Unix epoch milliseconds
- `TYPE = 2`: integer
- `TYPE = 0`: boolean / flag
- `TYPE = 4`: string / enum / JSON / serialized history
- `TYPE = 1`: treated conservatively as integer / result code because the sample uses it for scan error values

The diagnostics section builds these views:

- Overview Dashboard
- Reboot Timeline
- Reboot History
- Scan Diagnostics
- Update Lifecycle
- Downtime Estimation
- Variable Dictionary
- Raw Data Inspector

## Known Assumptions

- The database is a diagnostic telemetry store, not a fully documented relational business schema.
- Raw values are always preserved.
- Derived labels such as `Likely success`, `Reboot pending likely`, or `Blocked: ReadyToReboot` are heuristics, not guaranteed Microsoft semantics.
- Historical reboot arrays are aligned by index when lengths differ.
- Historical scheduled reboot times are inferred from the nearest available schedule-related variables when no dedicated schedule history array exists.
- `UXRebootTimeHistory` is treated as reboot-timestamp history; the exact internal meaning is still labeled as heuristic.
- Empty or missing tables are allowed and do not fail the analysis.

## Confidence Model

Variable explanations and reboot-history associations use these confidence levels:

- `High`: direct naming match plus matching type semantics or strong cross-table correlation
- `Medium`: plausible Windows Update / UX meaning with partial supporting correlation
- `Low`: weak signal, mostly naming-based
- `Unknown`: raw field preserved, semantics not established

Evidence labels shown in the variable dictionary may include:

- Naming convention
- Type semantics
- Correlation with update lifecycle
- Correlation with UX notification history
- Correlation with downtime history
- Official Windows Update concept
- Unknown

## Important Note

Many fields in this database are internal Windows Update / USO / UX values and are not fully or officially documented by Microsoft. This implementation intentionally avoids overclaiming undocumented meanings and keeps raw telemetry visible beside any derived interpretation.
