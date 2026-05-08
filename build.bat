@echo off
set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Release

echo Building StickerApp [%CONFIG%]...
dotnet build StickerApp\StickerApp.csproj -c %CONFIG% --nologo
if %ERRORLEVEL% neq 0 (
    echo Build FAILED
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Output: StickerApp\bin\%CONFIG%\net9.0-windows\StickerApp.exe
echo Done.
