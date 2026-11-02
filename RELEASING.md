# Releasing Solforge

Releases, and any build given to anyone else, are made from a clean `publish\`, produced by:

```powershell
.\publish.ps1 -Output publish-staging
```

Never share, upload or attach anything from `obj\` or `bin\`.

## Build

`publish.ps1` builds the self-contained `Solforge.exe` and `solforge-mcp.exe` into a new or empty output folder (default `publish\`). It refuses a populated folder without deleting anything, including an existing Solforge AI installation. Use a new staging folder for each rebuild, then package Solforge AI into that same folder with `solforge-ai-build/publish-ai.ps1`. It maps source
paths to `/_/` in the embedded PDBs and embeds the license notices.

## Release checklist

1. `.\verify.ps1` passes.
2. `.\publish.ps1 -Output <new folder>` ends with "Published to ...".
3. Upload only files from that clean publish output.
