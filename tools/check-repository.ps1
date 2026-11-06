#requires -Version 5.1
<#
Checks repository documentation only: JSON syntax, backlog references/statuses,
dependency cycles, task cards, inline Markdown file links, and source hashes.
Does not validate Markdown anchors, reference-style links, external URLs, or
application behavior. Requires Git; no SDK, network, or package restore is used.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

try {
    $inventoryText = (& git -C $repoRoot ls-files --cached --others --exclude-standard -z) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Git file inventory failed.' }
    $files = @($inventoryText -split "`0" | Where-Object { $_ } | Sort-Object -Unique)
    $jsonFiles = @($files | Where-Object { $_ -match '\.json$' })
    $json = @{}
    foreach ($file in $jsonFiles) {
        $json[$file] = Get-Content -LiteralPath (Join-Path $repoRoot $file) -Raw -Encoding UTF8 | ConvertFrom-Json
    }

    $backlog = $json['tasks/backlog.json']
    $taskIds = @($backlog.tasks | ForEach-Object { $_.id })
    $gateIds = @($backlog.phase_gates | ForEach-Object { $_.id })
    $nodes = @{}
    foreach ($task in $backlog.tasks) {
        if (-not $task.id -or $nodes.ContainsKey($task.id)) { throw "Missing or duplicate task ID: $($task.id)" }
        if ($task.status -notin $backlog.allowed_task_statuses) { throw "Invalid task status: $($task.id)" }
        foreach ($dependency in $task.depends_on) {
            if ($dependency -notin $taskIds) { throw "Unknown task dependency: $($task.id) -> $dependency" }
        }
        foreach ($gateId in $task.requires_gates) {
            if ($gateId -notin $gateIds) { throw "Unknown required gate: $($task.id) -> $gateId" }
        }
        $nodes[$task.id] = @($task.depends_on) + @($task.requires_gates)
    }
    foreach ($gate in $backlog.phase_gates) {
        if (-not $gate.id -or $nodes.ContainsKey($gate.id)) { throw "Missing or duplicate gate ID: $($gate.id)" }
        if ($gate.status -notin $backlog.allowed_gate_statuses) { throw "Invalid gate status: $($gate.id)" }
        foreach ($taskId in $gate.required_tasks) {
            if ($taskId -notin $taskIds) { throw "Unknown required task: $($gate.id) -> $taskId" }
        }
        foreach ($dependency in $gate.depends_on_gates) {
            if ($dependency -notin $gateIds) { throw "Unknown gate dependency: $($gate.id) -> $dependency" }
        }
        $nodes[$gate.id] = @($gate.required_tasks) + @($gate.depends_on_gates)
    }
    $visiting = @{}
    $visited = @{}
    function Visit-Node([string]$nodeId) {
        if ($visiting.ContainsKey($nodeId)) { throw "Dependency cycle at $nodeId" }
        if ($visited.ContainsKey($nodeId)) { return }
        $visiting[$nodeId] = $true
        foreach ($dependency in $nodes[$nodeId]) { Visit-Node $dependency }
        $visiting.Remove($nodeId)
        $visited[$nodeId] = $true
    }
    foreach ($nodeId in @($nodes.Keys)) { Visit-Node $nodeId }
    $plan = Get-Content -LiteralPath (Join-Path $repoRoot 'docs/IMPLEMENTATION_PLAN.md') -Raw -Encoding UTF8
    foreach ($taskId in $taskIds) {
        if ($plan -notmatch ('(?m)^### ' + [regex]::Escape($taskId) + '\r?$')) { throw "Missing task card: $taskId" }
    }

    $markdownFiles = @($files | Where-Object { $_ -match '\.md$' })
    $linkCount = 0
    foreach ($file in $markdownFiles) {
        $fullPath = Join-Path $repoRoot $file
        $content = Get-Content -LiteralPath $fullPath -Raw -Encoding UTF8
        foreach ($match in [regex]::Matches($content, '\[[^\]]+\]\((?<target><[^>]+>|[^)\s]+)(?:\s+"[^"]*")?\)')) {
            $target = $match.Groups['target'].Value.Trim('<', '>')
            if ($target -match '^(?:[a-z][a-z0-9+.-]*:|//|#)') { continue }
            $target = [Uri]::UnescapeDataString(($target -split '[?#]', 2)[0])
            $basePath = Split-Path -Parent $fullPath
            if ($target.StartsWith('/')) { $basePath = $repoRoot; $target = $target.TrimStart('/') }
            $resolved = [IO.Path]::GetFullPath((Join-Path $basePath $target))
            if (-not $resolved.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and $resolved -ne $repoRoot) {
                throw "Link leaves repository: $file -> $target"
            }
            if (-not (Test-Path -LiteralPath $resolved)) { throw "Missing local link: $file -> $target" }
            $linkCount++
        }
    }

    # MANIFEST.json describes the original package, not edited living documents.
    $sources = @($json['MANIFEST.json'].files | Where-Object { $_.path -like 'source/*' })
    if ($sources.Count -eq 0) { throw 'Original manifest contains no source snapshots.' }
    foreach ($source in $sources) {
        $sourcePath = Join-Path $repoRoot $source.path
        if ((Get-Item -LiteralPath $sourcePath).Length -ne $source.size_bytes -or
            (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $source.sha256) {
            throw "Original source snapshot changed: $($source.path)"
        }
    }
    Write-Output "PASS: $($jsonFiles.Count) JSON files; $($taskIds.Count) tasks and $($gateIds.Count) gates; acyclic dependencies and task cards."
    Write-Output "PASS: $linkCount local links in $($markdownFiles.Count) Markdown files; $($sources.Count) original source hashes."
    exit 0
} catch {
    [Console]::Error.WriteLine("FAIL: " + $_.Exception.Message)
    exit 1
}
