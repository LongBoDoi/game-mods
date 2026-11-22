export const meta = {
  name: 'release',
  description: 'Fix errors, commit, push and publish a GitHub release for each game with uncommitted changes',
  phases: [
    { title: 'Detect' },
    { title: 'Fix & Build' },
    { title: 'Release' },
  ],
}

// ── Schemas ───────────────────────────────────────────────────────────────────

const DETECT_SCHEMA = {
  type: 'object',
  properties: {
    games: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          name:             { type: 'string' },
          lastVersion:      { type: 'string' },
          suggestedVersion: { type: 'string' },
          versionReason:    { type: 'string' },
          changedFiles:     { type: 'array', items: { type: 'string' } },
        },
        required: ['name', 'lastVersion', 'suggestedVersion', 'versionReason', 'changedFiles'],
      },
    },
  },
  required: ['games'],
}

const BUILD_SCHEMA = {
  type: 'object',
  properties: {
    game:    { type: 'string' },
    version: { type: 'string' },
    success: { type: 'boolean' },
    summary: { type: 'string' },
  },
  required: ['game', 'version', 'success', 'summary'],
}

const RELEASE_SCHEMA = {
  type: 'object',
  properties: {
    game:       { type: 'string' },
    version:    { type: 'string' },
    releaseUrl: { type: 'string' },
    success:    { type: 'boolean' },
    summary:    { type: 'string' },
  },
  required: ['game', 'version', 'success', 'summary'],
}

// ── Phase 1: find games with uncommitted changes ──────────────────────────────

phase('Detect')
const detection = await agent(
  `Repo: e:\\Projects\\game-mods

Run:
  git -C e:\\Projects\\game-mods status --short
  git -C e:\\Projects\\game-mods tag --list --sort=-version:refname

Game folders are top-level directories that contain an installer/ subdirectory.

Identify which game folders have uncommitted changes (staged M/D, unstaged, or untracked ??).

Skip these files entirely — they are never released:
  • inspect_*.ps1, list_*.ps1 at repo root
  • <Any game>/conversation_checkpoint.md
  • <Any game>/_decompiled/

For each game folder that HAS changes, return:
  name             — folder name, e.g. "Overcooked2"
  lastVersion      — last tag matching <Game>-v<M>.<N>.<P>, just the M.N.P part; "0.0.0" if none
  suggestedVersion — patch bump for fixes/tweaks, minor for new features or new mods, major for breaking changes; "1.0.0" if no prior tag
  versionReason    — one sentence explaining the bump level
  changedFiles     — git-relative paths of all changed/new/deleted files in that folder`,
  { schema: DETECT_SCHEMA },
)

if (!detection || detection.games.length === 0) {
  log('No games with uncommitted changes — nothing to release.')
  return { released: [] }
}

log(`Games to release: ${detection.games.map(g => `${g.name} → v${g.suggestedVersion}`).join(', ')}`)

// ── Phase 2: fix errors / warnings, then build ───────────────────────────────

phase('Fix & Build')
const built = await pipeline(
  detection.games,
  game => agent(
    `Repo: e:\\Projects\\game-mods   Game: ${game.name}   Target version: ${game.suggestedVersion}

Your task: ensure every mod in ${game.name}/ builds with ZERO compiler errors and ZERO warnings.

1. List every subdirectory under ${game.name}/ that has a build.cmd.
2. For each mod, run its build.cmd from the mod's own directory and capture stdout+stderr.
3. For any error or warning, read the offending source file(s) and fix them IN PLACE.
   ABSOLUTE RULES — violating any of these is a critical failure:
   - NEVER delete any mod folder or any source file (.cs, .csproj, build.cmd, README.md).
     If a mod does not build, fix its source code or report success:false. Deleting is never an option.
   - NEVER edit files under ${game.name}/_decompiled/ — read-only game DLLs.
   - NEVER edit conversation_checkpoint.md or inspect_*.ps1/list_*.ps1 at the repo root.
4. Re-run the build after each fix; repeat until 0 errors and 0 warnings.
5. Do NOT stage or commit — the Release phase handles git.

Return:
  game    — "${game.name}"
  version — "${game.suggestedVersion}"
  success — true if every mod built clean, false otherwise
  summary — one sentence: what was built, or what failed and why`,
    { schema: BUILD_SCHEMA, label: `build:${game.name}`, phase: 'Fix & Build' },
  ),
)

const good = built.filter(r => r && r.success)
const bad  = built.filter(r => !r || !r.success)
if (bad.length)   log(`Build failures (skipping release): ${bad.map(r => r ? r.game : '?').join(', ')}`)
if (!good.length) { log('Nothing built cleanly — aborting.'); return { released: [], failed: bad } }

// ── Phase 3: commit → push → release ─────────────────────────────────────────

phase('Release')
const releases = []

for (const b of good) {
  const r = await agent(
    `Repo: e:\\Projects\\game-mods   Game: ${b.game}   Version: ${b.version}

Build is clean. Now commit, push, and publish the GitHub release. Follow these steps exactly:

1. Run git status to see staged/unstaged/untracked files.

2. Stage ONLY files inside ${b.game}/ using explicit paths — never "git add ." or "git add -A".
   - Deleted files in ${b.game}/ need "git rm <path>" to stage the deletion.
   - NEVER stage: ${b.game}/_decompiled/, ${b.game}/conversation_checkpoint.md,
     inspect_*.ps1 or list_*.ps1 at the repo root.

3. Run git status again and confirm no unexpected files are staged.

4. Commit with a message that summarises the changes. Format:
     <Game human name>: <what changed>

   Always end the commit message body with this line:
     Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>

5. Push: git -C e:\\Projects\\game-mods push origin main

6. If any non-game files remain untracked/modified and would block Release.ps1 (which needs a
   clean working tree), stash them first:
     git -C e:\\Projects\\game-mods stash push -u -- <each non-game file>
   Remember to pop the stash after the release script finishes.

7. Run the release script:
     powershell.exe -ExecutionPolicy Bypass -NonInteractive -File e:\\Projects\\game-mods\\scripts\\Release.ps1 ${b.game} ${b.version} -Publish

8. Return the structured result. The last line the script prints on success is "Released: <URL>".

Return:
  game       — "${b.game}"
  version    — "${b.version}"
  releaseUrl — the URL printed by the release script, or empty string
  success    — true/false
  summary    — one sentence`,
    { schema: RELEASE_SCHEMA, label: `release:${b.game}`, phase: 'Release' },
  )
  releases.push(r)
}

const ok   = releases.filter(r => r && r.success)
const fail = releases.filter(r => !r || !r.success)

if (ok.length)
  log('Released: ' + ok.map(r => `${r.game} v${r.version}${r.releaseUrl ? ' → ' + r.releaseUrl : ''}`).join(', '))
if (fail.length)
  log('Release failed: ' + fail.map(r => r ? `${r.game} — ${r.summary}` : 'unknown').join(', '))

return { released: ok, failed: [...bad, ...fail] }
