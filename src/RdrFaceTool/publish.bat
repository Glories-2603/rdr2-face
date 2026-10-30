@echo off
rem Self-contained single-file builds of RdrFaceTool for the facial converter (no .NET needed on the machine)
cd /d "%~dp0"
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=false -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish\win-x64 || exit /b 1
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=false -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish\linux-x64 || exit /b 1
