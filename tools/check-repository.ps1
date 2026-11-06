#requires -Version 5.1
<#
Checks repository documentation only: JSON syntax, backlog references/statuses,
dependency cycles, task cards, inline Markdown file links, and source hashes.
Also guards parent/child coverage, inherited prerequisites and completion state.
BacklogPath can point to a proposal; omit it for actual repository validation.
Does not validate Markdown anchors, reference-style links, external URLs, or
application behavior. Requires Git; no SDK, network, or package restore is used.
#>
[CmdletBinding()]
param([string]$BacklogPath = 'tasks/backlog.json')

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

    # An alternate proposal can be checked without replacing the authoritative ledger.
    $backlog = Get-Content -LiteralPath (Join-Path $repoRoot $BacklogPath) -Raw -Encoding UTF8 | ConvertFrom-Json
    $taskIds = @($backlog.tasks | ForEach-Object { $_.id })
    $gateIds = @($backlog.phase_gates | ForEach-Object { $_.id })
    $nodes = @{}
    $taskById = @{}
    $gateById = @{}
    foreach ($task in $backlog.tasks) { $taskById[$task.id] = $task }
    foreach ($gate in $backlog.phase_gates) { $gateById[$gate.id] = $gate }
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
        if ($task.PSObject.Properties['children']) { $nodes[$task.id] += @($task.children) }
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
        if ($gate.status -eq 'passed') {
            foreach ($taskId in $gate.required_tasks) {
                if ($taskById[$taskId].status -ne 'done') { throw "Gate $($gate.id) passed before $taskId is done." }
            }
            foreach ($dependency in $gate.depends_on_gates) {
                if ($gateById[$dependency].status -ne 'passed') { throw "Gate $($gate.id) passed before $dependency." }
            }
        }
    }
    foreach ($task in $backlog.tasks) {
        $isGroup = $task.PSObject.Properties['children'] -and @($task.children).Count -gt 0
        if ($task.PSObject.Properties['task_type'] -and $task.task_type -notin @('group', 'implementation', 'verification')) {
            throw "Invalid task type: $($task.id)"
        }
        if ($isGroup) {
            if ($task.task_type -ne 'group' -or $task.status -eq 'ready') { throw "A group is not an executable ready task: $($task.id)" }
            if (@($task.children | Select-Object -Unique).Count -ne @($task.children).Count) { throw "Duplicate child in $($task.id)" }
            foreach ($childId in $task.children) {
                if ($childId -notin $taskIds) { throw "Unknown child: $($task.id) -> $childId" }
                $child = $taskById[$childId]
                if ($child.parent_id -ne $task.id -or $child.task_type -eq 'group') { throw "Invalid parent/child link: $childId" }
                if ($child.phase -ne $task.phase) { throw "Child phase differs from parent: $childId" }
                foreach ($dependency in $task.depends_on) {
                    if ($dependency -notin $child.depends_on) { throw "Child $childId bypasses parent prerequisite $dependency" }
                }
                foreach ($gateId in $task.requires_gates) {
                    if ($gateId -notin $child.requires_gates) { throw "Child $childId bypasses gate $gateId" }
                }
                foreach ($field in @('scope', 'acceptance', 'validation', 'owned_areas', 'out_of_scope', 'prerequisites')) {
                    if (@($child.$field).Count -eq 0) { throw "Child $childId has no $field" }
                }
                if ($task.status -eq 'done' -and $child.status -ne 'done') { throw "Parent $($task.id) is done before child $childId" }
            }
            foreach ($field in @('scope', 'acceptance', 'validation')) {
                $covered = @()
                foreach ($childId in $task.children) {
                    foreach ($index in $taskById[$childId].parent_coverage.$field) {
                        if ($index -lt 1 -or $index -gt @($task.$field).Count) { throw "Invalid $field coverage in $childId" }
                        $covered += $index
                    }
                }
                for ($index = 1; $index -le @($task.$field).Count; $index++) {
                    if ($index -notin $covered) { throw "Uncovered $field item $index in $($task.id)" }
                }
            }
            if ($task.requires_interactive_windows_evidence -and
                @($task.children | Where-Object { $taskById[$_].requires_interactive_windows_evidence }).Count -eq 0) {
                throw "Parent $($task.id) lost its interactive Windows evidence requirement."
            }
        } elseif ($task.PSObject.Properties['task_type'] -and $task.task_type -eq 'group') {
            throw "Group has no children: $($task.id)"
        }
        if ($task.PSObject.Properties['parent_id'] -and $task.parent_id) {
            if ($task.parent_id -notin $taskIds -or $task.id -notin $taskById[$task.parent_id].children) {
                throw "Orphaned child: $($task.id)"
            }
        }
        if (-not $isGroup -and $task.status -in @('ready', 'in_progress', 'implemented_unverified', 'done')) {
            foreach ($dependency in $task.depends_on) {
                if ($taskById[$dependency].status -ne 'done') { throw "Task $($task.id) started before prerequisite $dependency" }
            }
            foreach ($gateId in $task.requires_gates) {
                if ($gateById[$gateId].status -ne 'passed') { throw "Task $($task.id) started before gate $gateId" }
            }
        }
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
    $plans = @{}
    foreach ($taskId in $taskIds) {
        $task = $taskById[$taskId]
        $instructionFile = 'docs/IMPLEMENTATION_PLAN.md'
        if ($task.PSObject.Properties['instruction_file']) { $instructionFile = $task.instruction_file }
        if (-not $plans.ContainsKey($instructionFile)) {
            $plans[$instructionFile] = Get-Content -LiteralPath (Join-Path $repoRoot $instructionFile) -Raw -Encoding UTF8
        }
        $plan = $plans[$instructionFile]
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
    Write-Output 'PASS: parent/child coverage, inherited prerequisites, and completion guards.'
    Write-Output "PASS: $linkCount local links in $($markdownFiles.Count) Markdown files; $($sources.Count) original source hashes."
    exit 0
} catch {
    [Console]::Error.WriteLine("FAIL: " + $_.Exception.Message)
    exit 1
}
