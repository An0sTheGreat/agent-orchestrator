# Publishes the launcher to dist\ and uploads it to the "launcher" GitHub Release. dist\*.exe stays out of git: each
# publish added ~70 MB to the repository.
if (Get-Process 'Agent Orchestrator' -ErrorAction SilentlyContinue) {
    Write-Error 'Close Agent Orchestrator first: a running exe cannot be overwritten.'
    exit 1
}
dotnet publish "$PSScriptRoot\launcher\AgentOrchestrator\AgentOrchestrator.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "$PSScriptRoot\dist"
if ($LASTEXITCODE) { exit $LASTEXITCODE }
$exe = "$PSScriptRoot\dist\Agent Orchestrator.exe"
$notes = "Built from $(git -C $PSScriptRoot rev-parse --short HEAD) on $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
gh release view launcher --json tagName | Out-Null
if ($LASTEXITCODE) {
    gh release create launcher $exe --title 'Launcher' --notes $notes
} else {
    gh release delete-asset launcher 'Agent.Orchestrator.exe' --yes # GitHub's name for it (spaces become dots)
    gh release upload launcher $exe
    if (-not $LASTEXITCODE) { gh release edit launcher --notes $notes }
}
exit $LASTEXITCODE
