& (Join-Path $PSScriptRoot 'build_sidecar.ps1')  # stage JVM sidecar assets into RenzoBackend/sidecar/ first

$backendPath = "./RenzoBackend"
$project = "RenzoBackend.csproj"

Push-Location $backendPath
dotnet restore $project
dotnet publish $project -c Release -r linux-x64 --self-contained false -p:PublishAot=false -p:PublishReadyToRun=false -p:DebugSymbols=false -o bin/linux/amd64
dotnet publish $project -c Release -r linux-arm64 --self-contained false -p:PublishAot=false -p:PublishReadyToRun=false -p:DebugSymbols=false -o bin/linux/arm64
docker buildx build -t ghcr.io/levitate0/renzo-shiori:latest --platform linux/amd64,linux/arm64 . --push
Pop-Location

