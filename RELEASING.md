# Releasing Solforge

Releases, and any build given to anyone else, are made **only** from `publish-public\`, produced by:

```powershell
.\publish.ps1 -Public
```

Never share, upload or attach anything from `publish\`, `obj\` or `bin\`.

## Public and owner builds

| | Public build | Owner build |
|---|---|---|
| Command | `.\publish.ps1 -Public` | `.\publish.ps1` |
| Output | `publish-public\` | `publish\` |
| MSBuild | `SolforgeRelay=false` (the default) | `SolforgeRelay=true`, defines `SOLFORGE_RELAY` |
| Owner-only features | none: their code is not in this repository and is not compiled in | compiled in from the private folder |
| Who uses it | everyone; releases | the owner's own PC only |

- The owner-only features live in a private folder outside this repository, `..\09-solforge-private\relay` by default
  (`SolforgeRelayDir`; `publish.ps1 -RelayDir` overrides it). `Directory.Build.props` pulls them in only when
  `SolforgeRelay=true`, and fails the build with a clear message when the folder is missing.
- The public code has only empty `partial` hooks for them (names start with `Owner`), so it builds and passes every
  check without the private folder. `build.ps1` and `verify.ps1` build and test the public configuration;
  `verify.ps1` also compile-checks the owner configuration when the private folder is present.
- `publish.ps1 -Public` builds into a fresh `publish-public\`, maps source paths to `/_/`, then scans every shipped file
  and the unbundled assemblies (as UTF-8 and UTF-16) for the strings listed in the private folder's `release-scan.txt`.
  Any hit deletes `publish-public\` and fails the script. If the private folder exists but the list does not, it fails
  before building.

## Release checklist

1. `.\verify.ps1` passes.
2. `.\publish.ps1 -Public` ends with "clean" and "Published to ...\publish-public".
3. Upload only files from `publish-public\`.
