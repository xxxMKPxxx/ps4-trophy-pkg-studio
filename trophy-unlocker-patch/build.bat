@echo off
chcp 65001 >nul
REM ============================================================
REM  一键构建打了补丁的 TrophyUnlocker.exe
REM
REM  用法：  build.bat  D:\PS4_Tools
REM  需要：  Visual Studio 2019/2022 的 "生成工具"（MSBuild）
REM          没有的话装 VS Build Tools（免费）：
REM          https://visualstudio.microsoft.com/visual-cpp-build-tools/
REM ============================================================
setlocal enabledelayedexpansion

if "%~1"=="" (
    echo 用法: build.bat ^<PS4_Tools 仓库路径^>
    echo 例如: build.bat D:\PS4_Tools
    exit /b 1
)

set "REPO=%~1"
set "CSPROJ=%REPO%\TrophyUnlocker\TrophyUnlocker.csproj"

if not exist "%CSPROJ%" (
    echo [!] 找不到 %CSPROJ%
    exit /b 1
)

echo [*] 应用补丁...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0apply-patch.ps1" -RepoPath "%REPO%"
if errorlevel 1 (
    echo [!] 打补丁失败
    exit /b 1
)

echo.
echo [*] 查找 MSBuild...
set "MSBUILD="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"

if exist "%VSWHERE%" (
    for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do (
        set "MSBUILD=%%i"
    )
)

if not defined MSBUILD (
    if exist "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" (
        set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
    )
)

if not defined MSBUILD (
    echo [!] 没找到 MSBuild。请安装 VS Build Tools 后重试。
    echo     也可以直接用 Visual Studio 打开:
    echo     %CSPROJ%
    exit /b 1
)

echo [+] MSBuild: !MSBUILD!
echo.
echo [*] 编译（Release / Any CPU）...
"!MSBUILD!" "%CSPROJ%" /t:Restore,Build /p:Configuration=Release /p:Platform="Any CPU" /v:minimal /nologo
if errorlevel 1 (
    echo.
    echo [!] 编译失败。常见原因：缺 NuGet 包，或 VS 里缺 .NET Framework 4.x 目标包。
    exit /b 1
)

echo.
for /r "%REPO%\TrophyUnlocker" %%f in (TrophyUnlocker.exe) do (
    echo [+] 产物: %%f
)

echo.
echo 完成。生成的 exe 会为每个游戏分配唯一的 Title ID（XDPX#####），
echo 多个 Unlocker 可以共存，不再互相覆盖。
endlocal
