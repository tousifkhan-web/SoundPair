@echo off
echo ===================================================
echo   Building SoundPair (Standalone Single-File)
echo ===================================================

echo.
echo Building SoundPair Standalone (Includes .NET 10 Runtime)...
dotnet publish SoundPair.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:AssemblyName=SoundPair -o ./publish -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true

if %errorlevel% neq 0 (
    echo.
    echo [ERROR] Failed to build SoundPair Standalone!
    goto error
)

echo.
echo ===================================================
echo   Build Completed Successfully!
echo   - Output Executable: publish\SoundPair.exe
echo ===================================================
goto end

:error
echo.
echo [ERROR] The build process encountered an error.

:end
pause
