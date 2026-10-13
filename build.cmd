@echo off
setlocal enabledelayedexpansion
rem ============================================================
rem  XAnnotation build helper - .NET SDK only, no Visual Studio
rem
rem  KEEP THIS FILE 7-BIT ASCII ONLY.
rem  NEVER PUT goto OR call LABELS IN THIS FILE.
rem
rem  Two independent cmd.exe defects make those two rules mandatory:
rem
rem  1) cmd.exe reads a .cmd file byte by byte using the console
rem     code page (936 / GBK on a Chinese Windows). A single
rem     non-ASCII character makes the reader lose the CR/LF
rem     boundary: the "rem " prefix is swallowed, the rest of the
rem     line is executed as a command, and the whole script
rem     collapses into a storm of
rem       "'xxx' is not recognized as an internal or external command".
rem     Chinese text belongs in the reply and in build.log, never here.
rem
rem  2) Label lookup assumes CRLF line endings. With LF-only endings
rem     the label scanner skips labels at 512-byte boundaries, which
rem     gives an intermittent "The system cannot find the batch label
rem     specified". This repo stores text files with LF, so this file
rem     uses no labels at all - flow is controlled with flags only.
rem
rem  Never write %ProgramFiles(x86)% here either: the ")" inside the
rem  variable name is parsed as the end of a parenthesised block and
rem  shreds every line after it.
rem
rem  Modes, default = auto:
rem    build.cmd            auto    net8.0-windows + net45
rem                                 AutoCAD 2025-2027 and 2015-2024
rem    build.cmd 2025       net8.0-windows only   AutoCAD 2025-2027
rem    build.cmd legacy     net45 only            AutoCAD 2015-2024
rem    build.cmd 2012       net40 only            AutoCAD 2012-2014,
rem                                 built only when the AutoCAD 2012
rem                                 assemblies are present
rem    build.cmd zwcad      auto + net472         ZWCAD, needs ZWCAD
rem    build.cmd all        everything
rem    build.cmd release    auto, then copy the fresh DLLs into
rem                                 EVERY ..\v0.5* folder next to this
rem                                 source that already holds a
rem                                 XAnnotation build, so whichever
rem                                 one CAD really loads from gets
rem                                 refreshed. A 2nd argument, or
rem                                 DEPLOY_DIR, limits it to 1 folder.
rem
rem  The complete compiler output is written to build.log next to
rem  this script on every run, so the raw errors can be copied out
rem  of the console window or the whole file handed over.
rem
rem  The .NET SDK is located automatically: PATH first, then the
rem  default install folder - so a double-click works even in a cmd
rem  window that has no dotnet in PATH.
rem
rem  net45 / net472 normally need the matching .NET Framework
rem  targeting pack, which ships with Visual Studio. Both projects
rem  reference Microsoft.NETFramework.ReferenceAssemblies, so a
rem  plain .NET SDK is enough - no MSB3644 any more.
rem
rem  NuGet restore needs nuget.org, or your company mirror
rem  configured in a NuGet.config next to this script.
rem
rem  The 2012 target cannot come from NuGet: the AutoCAD.NET package
rem  starts at 20.0.0, which is AutoCAD 2015. It therefore references
rem  acdbmgd.dll and acmgd.dll straight out of an AutoCAD 2012 folder,
rem  default:
rem      C:\Program Files\Autodesk\AutoCAD 2012
rem  For a folder elsewhere, either set X_ACAD2012_DIR before running
rem  this script, or skip the script and call dotnet directly:
rem      dotnet build src\XAnnotation.AutoCAD2012.csproj -c Release
rem          -p:Acad2012Dir="D:\path\to\acad2012"
rem  Only acdbmgd.dll and acmgd.dll are needed there, not a full install.
rem ============================================================

set "ROOT=%~dp0"
set "SRC=%ROOT%src"
set "LOG=%ROOT%build.log"
set "MODE=%~1"
if "%MODE%"=="" set "MODE=auto"

rem  Deploy folder(s) used by "release":
rem    no 2nd argument -> every ..\v0.5* folder that already contains a
rem                       XAnnotation build is refreshed (auto-detect,
rem                       see the deploy step). Writing to one fixed
rem                       folder while CAD loads from another is exactly
rem                       what produces "the build succeeded but the
rem                       plugin did not change".
rem    2nd argument or DEPLOY_DIR -> only that folder, created if missing.
set "OUT=%ROOT%..\v0.5.2"
if not "%~2"=="" set "OUT=%~2"
if not "%~2"=="" if "%~d2"=="" set "OUT=%ROOT%%~2"
if "%~2"=="" if not "%DEPLOY_DIR%"=="" set "OUT=%DEPLOY_DIR%"
set "EXPLICIT=0"
if not "%~2"=="" set "EXPLICIT=1"
if "%~2"=="" if not "%DEPLOY_DIR%"=="" set "EXPLICIT=1"

rem  Clear the log at the start of every run.
del "%LOG%" >nul 2>nul

rem  Pause at the end only when launched by double-click.
set "DBLCLICK="
echo "%cmdcmdline%" | find /i "%~nx0" >nul 2>nul
if not errorlevel 1 set "DBLCLICK=1"

set "FAILED=0"
set "VALID=0"
set "DEPLOY=0"
set "BUILD_2025=0"
set "BUILD_LEGACY=0"
set "BUILD_2012=0"
set "BUILD_ZWCAD=0"

if /i "%MODE%"=="auto"    (set "BUILD_2025=1" & set "BUILD_LEGACY=1" & set "VALID=1")
if /i "%MODE%"=="2025"    (set "BUILD_2025=1" & set "VALID=1")
if /i "%MODE%"=="legacy"  (set "BUILD_LEGACY=1" & set "VALID=1")
if /i "%MODE%"=="2012"    (set "BUILD_2012=1" & set "VALID=1")
if /i "%MODE%"=="zwcad"   (set "BUILD_2025=1" & set "BUILD_LEGACY=1" & set "BUILD_ZWCAD=1" & set "VALID=1")
if /i "%MODE%"=="all"     (set "BUILD_2025=1" & set "BUILD_LEGACY=1" & set "BUILD_2012=1" & set "BUILD_ZWCAD=1" & set "VALID=1")
if /i "%MODE%"=="release" (set "BUILD_2025=1" & set "BUILD_LEGACY=1" & set "DEPLOY=1" & set "VALID=1")

if "%VALID%"=="0" (
    set "FAILED=1"
    set "BUILD_2025=0"
    set "BUILD_LEGACY=0"
    set "BUILD_2012=0"
    set "BUILD_ZWCAD=0"
    echo.
    echo [ERROR] Unknown mode "%MODE%".
    echo         Use one of: auto ^| 2025 ^| legacy ^| 2012 ^| zwcad ^| all ^| release
    echo.
)

echo.
echo ==== XAnnotation build ====
echo Project root : %ROOT%
echo Mode         : %MODE%
echo Targets      : 2025=%BUILD_2025%  legacy=%BUILD_LEGACY%  2012=%BUILD_2012%  zwcad=%BUILD_ZWCAD%
echo.

rem  Locate the .NET SDK. No parentheses in these paths on purpose.
set "DOTNET="
where dotnet >nul 2>nul
if not errorlevel 1 set "DOTNET=dotnet"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET if exist "%SystemDrive%\Program Files\dotnet\dotnet.exe" set "DOTNET=%SystemDrive%\Program Files\dotnet\dotnet.exe"

if not defined DOTNET (
    set "FAILED=1"
    set "BUILD_2025=0"
    set "BUILD_LEGACY=0"
    set "BUILD_2012=0"
    set "BUILD_ZWCAD=0"
    echo [ERROR] dotnet was not found, neither in PATH nor at
    echo         "%ProgramFiles%\dotnet\dotnet.exe".
    echo         Install the .NET SDK 8.0 or newer from
    echo             https://dotnet.microsoft.com/download
    echo         NOTE: the .NET Runtime alone cannot compile anything.
    echo.
) else (
    echo [OK] dotnet SDK : !DOTNET!
    echo [OK] dotnet SDK version:
    "!DOTNET!" --version
    echo.
)

if "%BUILD_2025%"=="1" (
    echo ------------------------------------------------------------
    echo  XAnnotation.AutoCAD2025   net8.0-windows   AutoCAD 2025-2027
    echo ------------------------------------------------------------
    "!DOTNET!" build "%SRC%\XAnnotation.AutoCAD2025.csproj" -c Release -v minimal --nologo >>"%LOG%" 2>&1
    set "RC=!ERRORLEVEL!"
    type "%LOG%"
    if not "!RC!"=="0" set "FAILED=1"
    echo.
)

if "%BUILD_LEGACY%"=="1" (
    echo ------------------------------------------------------------
    echo  XAnnotation.AutoCAD       net45            AutoCAD 2015-2024
    echo ------------------------------------------------------------
    "!DOTNET!" build "%SRC%\XAnnotation.AutoCAD.csproj" -c Release -v minimal --nologo >>"%LOG%" 2>&1
    set "RC=!ERRORLEVEL!"
    type "%LOG%"
    if not "!RC!"=="0" set "FAILED=1"
    echo.
)

if "%BUILD_2012%"=="1" (
    set "ACAD2012DIR=C:\Program Files\Autodesk\AutoCAD 2012"
    if not "%X_ACAD2012_DIR%"=="" set "ACAD2012DIR=%X_ACAD2012_DIR%"
    if exist "!ACAD2012DIR!\acdbmgd.dll" (
        echo ------------------------------------------------------------
        echo  XAnnotation.AutoCAD2012   net40            AutoCAD 2012-2014
        echo ------------------------------------------------------------
        "!DOTNET!" build "%SRC%\XAnnotation.AutoCAD2012.csproj" -c Release -v minimal --nologo >>"%LOG%" 2>&1
        set "RC=!ERRORLEVEL!"
        type "%LOG%"
        if not "!RC!"=="0" set "FAILED=1"
        echo.
    ) else (
        echo [SKIP] AutoCAD 2012 assemblies not found at
        echo        "!ACAD2012DIR!".
        echo        Install AutoCAD 2012, or copy its acdbmgd.dll and
        echo        acmgd.dll into any folder and build it explicitly:
        echo            dotnet build "%SRC%\XAnnotation.AutoCAD2012.csproj" -c Release -p:Acad2012Dir="D:\path\to\acad2012"
        echo.
    )
)

if "%BUILD_ZWCAD%"=="1" (
    set "ZWCADDIR=C:\Program Files\ZWSOFT\ZWCAD Enterprise"
    if not exist "!ZWCADDIR!\ZwManaged.dll" (
        echo [SKIP] ZWCAD not found at "!ZWCADDIR!".
        echo        Install ZWCAD, or build it explicitly with:
        echo            dotnet build "%SRC%\XAnnotation.csproj" -c Release -p:ZwcadDir="D:\path\to\ZWCAD"
    ) else (
        echo ------------------------------------------------------------
        echo  XAnnotation.ZWCAD         net472           ZWCAD
        echo ------------------------------------------------------------
        "!DOTNET!" build "%SRC%\XAnnotation.csproj" -c Release -v minimal --nologo >>"%LOG%" 2>&1
        set "RC=!ERRORLEVEL!"
        type "%LOG%"
        if not "!RC!"=="0" set "FAILED=1"
        echo.
    )
)

if "%FAILED%"=="0" if "%DEPLOY%"=="1" (
    echo ---- Deploy ----
    set "LIST=%TEMP%\xannotation_deploy.txt"
    if not defined TEMP set "LIST=%ROOT%build.deploy.txt"
    del "!LIST!" >nul 2>nul
    if "!EXPLICIT!"=="1" (
        >>"!LIST!" echo %OUT%
    ) else (
        rem  Auto-detect: refresh every sibling ..\v0.5* folder that already
        rem  holds a XAnnotation build. Older v0.5.x folders used the
        rem  LAAnnotation.* file names, so they no longer match and are skipped.
        for /d %%D in ("%ROOT%..\v0.5*") do (
            set "HIT=0"
            if exist "%%~fD\XAnnotation.AutoCAD.dll" set "HIT=1"
            if exist "%%~fD\XAnnotation.AutoCAD2025.dll" set "HIT=1"
            if exist "%%~fD\XAnnotation.AutoCAD2012.dll" set "HIT=1"
            if "!HIT!"=="1" >>"!LIST!" echo %%~fD
        )
    )
    set "HITS=0"
    if exist "!LIST!" for /f "usebackq delims=" %%T in ("!LIST!") do (
        set "HITS=1"
        if not exist "%%T" mkdir "%%T"
        if not exist "%%T\net45" mkdir "%%T\net45"
        if not exist "%%T\net40" mkdir "%%T\net40"
        if not exist "%%T\net8.0-windows" mkdir "%%T\net8.0-windows"
        if exist "%ROOT%bin-acad2012\XAnnotation.AutoCAD2012.dll" (
            copy /Y "%ROOT%bin-acad2012\XAnnotation.AutoCAD2012.dll" "%%T\net40\" >nul
            if exist "%ROOT%bin-acad2012\XAnnotation.AutoCAD2012.pdb" copy /Y "%ROOT%bin-acad2012\XAnnotation.AutoCAD2012.pdb" "%%T\net40\" >nul
            if exist "%ROOT%bin-acad2012\System.ValueTuple.dll" copy /Y "%ROOT%bin-acad2012\System.ValueTuple.dll" "%%T\net40\" >nul
            echo   net40  -^> %%T\XAnnotation.AutoCAD2012.dll
        )
        if exist "%ROOT%bin-acad\XAnnotation.AutoCAD.dll" (
            copy /Y "%ROOT%bin-acad\XAnnotation.AutoCAD.dll" "%%T\net45\" >nul
            if exist "%ROOT%bin-acad\XAnnotation.AutoCAD.pdb" copy /Y "%ROOT%bin-acad\XAnnotation.AutoCAD.pdb" "%%T\net45\" >nul
            if exist "%ROOT%bin-acad\System.ValueTuple.dll" copy /Y "%ROOT%bin-acad\System.ValueTuple.dll" "%%T\net45\" >nul
            echo   net45  -^> %%T\XAnnotation.AutoCAD.dll
        )
        if exist "%ROOT%bin-acad2025-2027\XAnnotation.AutoCAD2025.dll" (
            copy /Y "%ROOT%bin-acad2025-2027\XAnnotation.AutoCAD2025.dll" "%%T\net8.0-windows\" >nul
            if exist "%ROOT%bin-acad2025-2027\XAnnotation.AutoCAD2025.pdb" copy /Y "%ROOT%bin-acad2025-2027\XAnnotation.AutoCAD2025.pdb" "%%T\net8.0-windows\" >nul
            if exist "%ROOT%bin-acad2025-2027\XAnnotation.AutoCAD2025.deps.json" copy /Y "%ROOT%bin-acad2025-2027\XAnnotation.AutoCAD2025.deps.json" "%%T\net8.0-windows\" >nul
            if exist "%ROOT%bin-acad2025-2027\System.Drawing.Common.dll" copy /Y "%ROOT%bin-acad2025-2027\System.Drawing.Common.dll" "%%T\net8.0-windows\" >nul
            echo   net8   -^> %%T\XAnnotation.AutoCAD2025.dll
        )
    )
    del "!LIST!" >nul 2>nul
    if "!HITS!"=="0" (
        echo   [WARN] no XAnnotation deploy folder was found next to the
        echo          source, and none was given on the command line.
        echo          Nothing was copied. Pass the folder CAD loads from:
        echo              build.cmd release "D:\path\to\that\folder"
        echo          a relative path is taken from this script's folder:
        echo              build.cmd release ..\v0.5.2.1
        echo.
    ) else (
        echo ---- Deploy done. Restart CAD before testing. ----
    )
    echo.
)

if "%FAILED%"=="1" (
    echo.
    echo ==== BUILD FAILED - read the errors above ====
    echo.
    echo Full build output, all raw errors, has been saved to:
    echo   %LOG%
    echo Send that file if you need the errors diagnosed.
    echo.
    echo Common causes:
    echo   * NuGet cannot reach nuget.org - set up your company mirror in NuGet.config
    echo   * ZWCAD not installed          - use the plain auto mode instead
    echo   * a real compile error         - the error text shows file name and line number
    echo.
) else (
    echo ==== Build succeeded ====
    echo.
    echo Full build log: %LOG%
    echo.
    echo Output DLLs:
    if exist "%ROOT%bin-acad2025-2027\XAnnotation.AutoCAD2025.dll" echo   %ROOT%bin-acad2025-2027\XAnnotation.AutoCAD2025.dll
    if exist "%ROOT%bin-acad\XAnnotation.AutoCAD.dll" echo   %ROOT%bin-acad\XAnnotation.AutoCAD.dll
    if exist "%ROOT%bin-acad2012\XAnnotation.AutoCAD2012.dll" echo   %ROOT%bin-acad2012\XAnnotation.AutoCAD2012.dll
    if exist "%ROOT%bin\XAnnotation.ZWCAD.dll" echo   %ROOT%bin\XAnnotation.ZWCAD.dll
    echo.
    echo Load the DLL in CAD with NETLOAD, then run X_PZ_MENU to rebuild the menu.
    echo.
)

if not defined DBLCLICK exit /b %FAILED%
echo Press any key to close this window...
pause >nul
exit /b %FAILED%
