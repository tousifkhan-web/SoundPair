@echo off
echo ===================================================
echo   Building SoundPair (Standalone & Lightweight)
echo ===================================================

echo.
echo [1/2] Building SoundPair_standalone (With .NET 10 Runtime)...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:AssemblyName=SoundPair_standalone -o ./publish/standalone

if %errorlevel% neq 0 (
    echo.
    echo [ERROR] Failed to build SoundPair_standalone!
    goto error
)

echo.
echo [2/2] Building Soundpair_lightweight (Without Runtime)...
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:AssemblyName=Soundpair_lightweight -o ./publish/light

if %errorlevel% neq 0 (
    echo.
    echo [ERROR] Failed to build Soundpair_lightweight!
    goto error
)

echo.
echo ===================================================
echo   Build Completed Successfully!
echo   - Standalone: publish\standalone\
echo   - Lightweight: publish\light\
echo ===================================================
goto end

:error
echo.
echo [ERROR] The build process encountered an error.

:end
pause