# Upstream

Solforge is a fork of Sunrise Studio with the Sunrise Blender add-on merged in as a subtree.

## Pinned commits

| Component | Upstream | Branch | Pinned commit | Local path |
|---|---|---|---|---|
| Sunrise Studio | https://github.com/stanuwu/SunriseStudio | `master` | `007dd7a6f10164243369350e63c42168229c35ab` ("studio and 0.6") | repository root |
| Sunrise Blender add-on | https://github.com/stanuwu/SunriseBlenderPlugin | `master` | `e0b5bc65d23eca65c37217ba6c813767225e1bb3` ("initial commit") | `blender/` |

Both pins were the upstream heads on 2026-10-15. The subtree was merged with full history in
`3dd75dd` (parents `007dd7a` and `e0b5bc6`).

Reference only, not vendored: Parhelion, `KyleThmpsn/sundial` `crates/parhelion`, checked at
`0bda0c53259df6c528086cb555949c5ef1d3ed6e` (2026-10-15). See `THIRD_PARTY.md` for its license.

## Remotes

| Name | URL | Use |
|---|---|---|
| `origin` | https://github.com/wyndxgo777/Solforge | Solforge; the only push target |
| `upstream` | https://github.com/stanuwu/SunriseStudio | fetch only |

The Blender add-on has no remote; it is pulled by URL. To make an accidental upstream push fail:

```powershell
git remote set-url --push upstream DISABLED
```

## Syncing

Studio:

```powershell
git fetch upstream
git log --oneline master..upstream/master     # review what changed
git merge --no-ff upstream/master             # on a sync branch first, see below
```

Blender add-on:

```powershell
git subtree pull --prefix=blender https://github.com/stanuwu/SunriseBlenderPlugin master
```

After either sync, update the pinned commit table above in the same merge, re-run `verify.ps1`, and
re-check `THIRD_PARTY.md` if upstream changed dependencies or licenses.

## Branch policy

- `master` is Solforge. It is never reset to upstream and never pushed to `upstream`.
- Never push to `stanuwu/*`. Changes meant for upstream go as a PR from a personal fork branch,
  following upstream's README rules, and only with Seth's say-so.
- Sync upstream on a `sync/upstream-<short-sha>` branch, fix conflicts and checks there, then merge
  into `master` with `--no-ff` so every sync point stays visible.
- Feature work goes on `phase/<NN>-<name>` branches or directly on `master` for small commits.
- Keep upstream's file layout and `SunriseStudio.*` project names until the rename phase, so
  upstream merges stay cheap.
