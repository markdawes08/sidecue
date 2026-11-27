#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Live,
    [switch]$NoBuild
)

$testKeyName = 'SIDECUE_DEEPGRAM_TEST_KEY'
$testKey = [Environment]::GetEnvironmentVariable($testKeyName, 'Process')
if ($Live) {
    foreach ($name in @($testKeyName, 'SIDECUE_DEEPGRAM_MODEL', 'SIDECUE_DEEPGRAM_MODEL_VERSION')) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name, 'Process'))) {
            throw "Live Deepgram smoke requires the $name process variable."
        }
    }
}

function New-SyntheticSpeech {
    param(
        [System.Speech.Synthesis.SpeechSynthesizer]$Synthesizer,
        [System.Speech.AudioFormat.SpeechAudioFormatInfo]$Format,
        [string]$Text
    )

    $maximumBytes = 8 * 16000 * 2
    $silenceBytes = 24000 * 2
    $stream = [System.IO.MemoryStream]::new()
    try {
        $Synthesizer.SetOutputToAudioStream($stream, $Format)
        try {
            $Synthesizer.Speak($Text)
        } finally {
            $Synthesizer.SetOutputToNull()
        }
        # SetOutputToAudioStream produces raw PCM. Append 1.5 seconds of silence for endpointing.
        if ($stream.Length -eq 0 -or $stream.Length % 2 -ne 0 -or $stream.Length + $silenceBytes -gt $maximumBytes) {
            throw 'Local speech synthesis returned an invalid PCM fixture length.'
        }
        $bytes = $stream.GetBuffer()
        if ($stream.Length -ge 4 -and [System.Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ceq 'RIFF') {
            throw 'Local speech synthesis returned a file header instead of raw PCM.'
        }
        $stream.SetLength($stream.Length + $silenceBytes)
        return [Convert]::ToBase64String($stream.GetBuffer(), 0, [int]$stream.Length)
    } finally {
        $stream.Dispose()
    }
}

# Only the live probe process receives the scoped credential. Restore and build never receive it.
try {
    [Environment]::SetEnvironmentVariable($testKeyName, $null, 'Process')
    . (Join-Path $PSScriptRoot 'common.ps1')
    Push-Location $repoRoot
    try {
        Assert-SidecueSdk
        $project = 'tests/Sidecue.DeepgramProbe/Sidecue.DeepgramProbe.csproj'
        if (-not $NoBuild) {
            Invoke-SidecueDotnet @('restore', $project, '--configfile', 'NuGet.Config', '--locked-mode')
            Invoke-SidecueDotnet @('build', $project, '--configuration', $Configuration, '--no-restore')
        }
        $probe = Join-Path $repoRoot "tests/Sidecue.DeepgramProbe/bin/$Configuration/net10.0-windows10.0.26100.0/Sidecue.DeepgramProbe.dll"
        if (-not (Test-Path -LiteralPath $probe -PathType Leaf)) {
            throw 'Build the Deepgram probe before using -NoBuild.'
        }

        $synthesizer = $null
        try {
            Add-Type -AssemblyName System.Speech
            $synthesizer = [System.Speech.Synthesis.SpeechSynthesizer]::new()
            $voice = $synthesizer.GetInstalledVoices() |
                Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.TwoLetterISOLanguageName -eq 'en' } |
                Select-Object -First 1
            if ($null -eq $voice) { throw 'An English voice is unavailable.' }
            $synthesizer.SelectVoice($voice.VoiceInfo.Name)
            $format = [System.Speech.AudioFormat.SpeechAudioFormatInfo]::new(
                16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
            $fixtures = @(
                New-SyntheticSpeech $synthesizer $format 'The blue lantern is by the window.'
                New-SyntheticSpeech $synthesizer $format 'The yellow bicycle is beside the fence.'
            )
        } catch {
            throw 'Could not synthesize the bounded PCM fixtures. Check that a Windows desktop English speech voice is enabled.'
        } finally {
            if ($null -ne $synthesizer) { $synthesizer.Dispose() }
        }

        $mode = '--preflight'
        if ($Live) {
            $mode = '--live'
            [Environment]::SetEnvironmentVariable($testKeyName, $testKey, 'Process')
        }
        $OutputEncoding = [System.Text.Encoding]::ASCII
        $fixtures | & $dotnetExecutable $probe $mode
        if ($LASTEXITCODE -ne 0) {
            throw "The Deepgram probe exited with code $LASTEXITCODE."
        }
    } finally {
        Pop-Location
    }
} finally {
    [Environment]::SetEnvironmentVariable($testKeyName, $testKey, 'Process')
}
