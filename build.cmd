@echo off
rem Builds SKALIA.exe and plugins\SkaliaDeps.dll into this folder. Needs the .NET SDK (8 or newer).
rem The plugin only references the Unturned Dedicated Server DLLs (U3DSDir); nothing from the game is copied.
cd /d "%~dp0"
if "%U3DSDir%"=="" set "U3DSDir=%~dp0.."
dotnet build "src\Skalia.Launcher\Skalia.Launcher.csproj" -c Release || goto :error
dotnet build "src\Skalia.Deps\Skalia.Deps.csproj" -c Release "-p:U3DSDir=%U3DSDir%" || goto :error
if not exist plugins mkdir plugins
copy /Y "src\Skalia.Launcher\bin\Release\net48\SKALIA.exe" "SKALIA.exe" >nul
copy /Y "src\Skalia.Launcher\bin\Release\net48\SKALIA.exe.config" "SKALIA.exe.config" >nul
copy /Y "src\Skalia.Deps\bin\Release\netstandard2.1\SkaliaDeps.dll" "plugins\SkaliaDeps.dll" >nul
copy /Y "src\Skalia.Deps\config.yaml" "plugins\Skalia.Deps.config.yaml" >nul
copy /Y "src\Skalia.Deps\translations.en.yaml" "plugins\Skalia.Deps.translations.en.yaml" >nul
echo SKALIA built.
goto :eof
:error
echo Build failed.
exit /b 1
