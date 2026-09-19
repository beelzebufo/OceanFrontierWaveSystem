@echo off
setlocal

set "GODOT=C:\Users\mcko\Desktop\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"
set "PROJECT=D:\ocean-frontier-wave-system"

echo.
echo ========================================
echo   OceanFrontier - Godot shader reimport
echo ========================================
echo.

if not exist "%GODOT%" (
	echo ERROR: Godot executable not found:
	echo %GODOT%
	exit /b 1
)

if not exist "%PROJECT%\project.godot" (
	echo ERROR: project.godot not found:
	echo %PROJECT%
	exit /b 1
)

echo Godot:
echo   %GODOT%
echo.
echo Project:
echo   %PROJECT%
echo.
echo Importing changed resources...
echo.

"%GODOT%" --path "%PROJECT%" --import

set "RESULT=%ERRORLEVEL%"

echo.

if not "%RESULT%"=="0" (
	echo ERROR: Godot import failed with exit code %RESULT%.
	exit /b %RESULT%
)

echo ========================================
echo   Import completed successfully.
echo ========================================
echo.

exit /b 0
