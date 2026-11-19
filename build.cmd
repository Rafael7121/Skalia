@echo off
rem Builds SKALIA.exe into this folder. Needs the .NET SDK (8 or newer).
cd /d "%~dp0"
dotnet build "src\Skalia.Launcher\Skalia.Launcher.csproj" -c Release || goto :error
copy /Y "src\Skalia.Launcher\bin\Release\net48\SKALIA.exe" "SKALIA.exe" >nul
copy /Y "src\Skalia.Launcher\bin\Release\net48\SKALIA.exe.config" "SKALIA.exe.config" >nul
echo SKALIA.exe built.
goto :eof
:error
echo Build failed.
exit /b 1
