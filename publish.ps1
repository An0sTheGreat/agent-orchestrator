# Publishes the launcher to dist\. dist\*.exe is stored with Git LFS (.gitattributes), so commit and push it to ship it:
# other machines get it with git pull (Git LFS must be installed there; Git for Windows includes it).
if (Get-Process 'Agent Orchestrator' -ErrorAction SilentlyContinue) {
    Write-Error 'Close Agent Orchestrator first: a running exe cannot be overwritten.'
    exit 1
}
dotnet publish "$PSScriptRoot\launcher\AgentOrchestrator\AgentOrchestrator.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "$PSScriptRoot\dist"
exit $LASTEXITCODE
