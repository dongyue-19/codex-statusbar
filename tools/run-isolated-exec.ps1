# Runs one real Codex turn in the isolated CODEX_HOME so the monitor's live path can be
# exercised without touching the user's own Codex sessions.
$ErrorActionPreference = 'Continue'
$home2 = Join-Path $env:TEMP 'codex-tps-test'
$env:CODEX_HOME = $home2
$bin = Join-Path $env:USERPROFILE '.codex\plugins\.plugin-appserver\codex.exe'
$prompt = 'Write about 700 words explaining how write-ahead logging works in SQLite, including the WAL file, checkpointing and the -shm index. Prose only, do not call any tools.'
& $bin exec --json --skip-git-repo-check --cd $home2 $prompt 2> (Join-Path $home2 'exec.err') |
    Out-File -FilePath (Join-Path $home2 'exec.jsonl') -Encoding utf8
"exec exit code: $LASTEXITCODE" | Out-File -FilePath (Join-Path $home2 'exec.done') -Encoding utf8