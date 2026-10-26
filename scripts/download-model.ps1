<#
.SYNOPSIS
    Downloads the GGUF weights the API expects.

.DESCRIPTION
    Defaults to Qwen2.5-1.5B-Instruct at Q4_K_M (~1.02 GiB), chosen for a CPU-only
    machine. Resumable: re-run it after an interrupted download and it continues
    rather than starting over.

    To use a different size, pass -Model. Remember to update Llama:ModelPath in
    backend/ChatBot.Api/appsettings.json to match.

    The RAG pipeline also needs the embedding model: -Model bge-small (~36 MB).

.EXAMPLE
    ./scripts/download-model.ps1
    ./scripts/download-model.ps1 -Model bge-small
    ./scripts/download-model.ps1 -Model qwen2.5-3b
#>
[CmdletBinding()]
param(
    [ValidateSet('qwen2.5-0.5b', 'qwen2.5-1.5b', 'qwen2.5-3b', 'bge-small')]
    [string]$Model = 'qwen2.5-1.5b',

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\models')
)

$ErrorActionPreference = 'Stop'

$catalogue = @{
    'qwen2.5-0.5b' = @{
        File = 'qwen2.5-0.5b-instruct-q4_k_m.gguf'
        Url  = 'https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf'
        Size = '~0.4 GB  - fastest, noticeably weaker at follow-ups'
    }
    'qwen2.5-1.5b' = @{
        File = 'qwen2.5-1.5b-instruct-q4_k_m.gguf'
        Url  = 'https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf'
        Size = '~1.0 GB  - default; the balance point on a CPU-only box'
    }
    'qwen2.5-3b'   = @{
        File = 'qwen2.5-3b-instruct-q4_k_m.gguf'
        Url  = 'https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf'
        Size = '~2.0 GB  - better answers, roughly half the speed'
    }
    # Embedding model for RAG (Rag:Embedding:ModelPath), not a chat model.
    'bge-small'    = @{
        File = 'bge-small-en-v1.5-q8_0.gguf'
        Url  = 'https://huggingface.co/CompendiumLabs/bge-small-en-v1.5-gguf/resolve/main/bge-small-en-v1.5-q8_0.gguf'
        Size = '~36 MB   - embeddings for the report index (384 dims)'
    }
}

$entry = $catalogue[$Model]
$target = Join-Path $OutputDirectory $entry.File

if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
}

Write-Host "Model : $Model  ($($entry.Size))"
Write-Host "Target: $target"

if (Test-Path $target) {
    $existing = (Get-Item $target).Length / 1GB
    Write-Host ("Already present ({0:N2} GB). Delete it to re-download." -f $existing) -ForegroundColor Green
    return
}

# curl.exe ships with Windows 10+ and handles resume (-C -) and redirects, which
# Invoke-WebRequest does poorly for multi-gigabyte files.
& curl.exe -L --fail --retry 3 --retry-delay 5 -C - -o $target $entry.Url

if ($LASTEXITCODE -ne 0) {
    throw "Download failed with exit code $LASTEXITCODE. Re-run to resume."
}

Write-Host ("Done: {0:N2} GB" -f ((Get-Item $target).Length / 1GB)) -ForegroundColor Green
$setting = if ($Model -eq 'bge-small') { 'Rag:Embedding:ModelPath' } else { 'Llama:ModelPath' }
Write-Host "Ensure $setting points at '../../models/$($entry.File)'."
