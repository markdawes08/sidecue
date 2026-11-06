#requires -Version 5.1
<#
Prints one actionable task and its parent acceptance without loading the whole
backlog into an agent's context. Read-only; never claims or changes task status.
BacklogPath is for checking proposals; omit it when selecting actual work.
#>
[CmdletBinding()]
param(
    [string]$Id,
    [string]$Owner,
    [string]$BacklogPath = 'tasks/backlog.json'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$backlog = Get-Content -LiteralPath (Join-Path $repoRoot $BacklogPath) -Raw -Encoding UTF8 | ConvertFrom-Json
$tasks = @{}
$gates = @{}
foreach ($task in $backlog.tasks) { $tasks[$task.id] = $task }
foreach ($gate in $backlog.phase_gates) { $gates[$gate.id] = $gate }

function Test-Group($Task) {
    return $Task.PSObject.Properties['children'] -and @($Task.children).Count -gt 0
}

function Get-UnmetDependencies($Task) {
    foreach ($dependency in $Task.depends_on) {
        if ($tasks[$dependency].status -ne 'done') { "$dependency ($($tasks[$dependency].status))" }
    }
    foreach ($gateId in $Task.requires_gates) {
        if ($gates[$gateId].status -ne 'passed') { "$gateId ($($gates[$gateId].status))" }
    }
}

$candidates = @($backlog.tasks | Where-Object { -not (Test-Group $_) })
$requestedGroup = $null
$selected = $null
if ($Id) {
    if (-not $tasks.ContainsKey($Id)) { throw "Unknown task: $Id" }
    if (Test-Group $tasks[$Id]) {
        $requestedGroup = $tasks[$Id]
        $candidates = @($requestedGroup.children | ForEach-Object { $tasks[$_] })
    } else {
        $selected = $tasks[$Id]
    }
}
if (-not $selected) {
    $selected = $candidates | Where-Object {
        $_.status -in @('planned', 'ready', 'in_progress', 'implemented_unverified') -and
        (-not $_.owner -or ($Owner -and $_.owner -eq $Owner)) -and
        (Get-UnmetDependencies $_ | Measure-Object).Count -eq 0
    } | Sort-Object @{ Expression = { if ($Owner -and $_.owner -eq $Owner) { 0 } else { 1 } } }, @{ Expression = {
        if ($_.status -eq 'in_progress') { 0 }
        elseif ($_.status -eq 'implemented_unverified') { 1 }
        else { 2 }
    } }, id | Select-Object -First 1
}
if (-not $selected) {
    [ordered]@{
        backlog_path = $BacklogPath
        message = 'No dependency-ready leaf task. Inspect blockers and required evidence; do not bypass a gate.'
        requested_group = $(if ($requestedGroup) { $requestedGroup.id } else { $null })
        tasks = @($candidates | Where-Object status -ne 'done' | ForEach-Object {
            [ordered]@{ id = $_.id; status = $_.status; owner = $_.owner; unmet_dependencies = @(Get-UnmetDependencies $_); blockers = $_.blockers }
        })
    } | ConvertTo-Json -Depth 8
    exit 0
}
$parent = $null
if ($selected.PSObject.Properties['parent_id'] -and $selected.parent_id) { $parent = $tasks[$selected.parent_id] }
$ownershipConflict = [bool]($selected.owner -and $selected.owner -ne $Owner)
[ordered]@{
    backlog_path = $BacklogPath
    instruction = $(if ($selected.status -eq 'done') {
        'This task is already done. Inspect its evidence; do not restart it unless the user requests that work.'
    } elseif ($ownershipConflict) {
        'This task is owned by another agent or owner identity was omitted. Coordinate ownership before working; this command does not reassign it.'
    } else {
        'Complete only this leaf and its focused checks, then hand off. Check prerequisites before claiming; selection is not proof the environment is available.'
    })
    ownership_conflict = $ownershipConflict
    task = $selected
    unmet_dependencies = @(Get-UnmetDependencies $selected)
    parent = $(if ($parent) {
        [ordered]@{ id = $parent.id; title = $parent.title; status = $parent.status; children = $parent.children;
            acceptance = $parent.acceptance; validation = $parent.validation }
    } else { $null })
} | ConvertTo-Json -Depth 12
