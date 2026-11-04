# Publishes the test build (the unified conversation view, its own data folder) to dist-test\, beside the regular dist\.
# dist-test\*.exe is stored with Git LFS (.gitattributes) like dist\'s.
if (Get-Process 'Agent Orchestrator' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*\dist-test\*" }) {
    Write-Error 'Close the test build first: a running exe cannot be overwritten.'
    exit 1
}
dotnet publish "$PSScriptRoot\launcher\AgentOrchestrator\AgentOrchestrator.csproj" -c Release -r win-x64 --self-contained true -p:TestBuild=true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "$PSScriptRoot\dist-test"
exit $LASTEXITCODE
