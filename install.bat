@echo off
chcp 936 >nul
title WinBC 安装
cd /d "%~dp0"

echo.
echo   正在安装 WinBC...
echo.

if not exist "%~dp0WinBC.exe" if not exist "%~dp0WinBC-standalone.exe" (
    echo   [错误] 找不到 WinBC.exe
    echo   请把本文件和 WinBC.exe 放在同一个文件夹里再运行。
    echo.
    pause
    exit /b 1
)

set "SRC=%~dp0WinBC.exe"
if not exist "%SRC%" set "SRC=%~dp0WinBC-standalone.exe"

set "DIR=%LOCALAPPDATA%\WinBC\bin"
if not exist "%DIR%" mkdir "%DIR%"
copy /y "%SRC%" "%DIR%\WinBC.exe" >nul
if errorlevel 1 (
    echo   [错误] 复制失败，可能是 WinBC 正在运行。请先在托盘图标上右键退出。
    pause
    exit /b 1
)
echo   1/3 程序已复制到 %DIR%

reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v WinBC /t REG_SZ /d "\"%DIR%\WinBC.exe\" --tray" /f >nul 2>&1
if errorlevel 1 (
    echo   2/3 [提示] 开机自启没设置成功，你仍然可以手动启动使用
) else (
    echo   2/3 已设置开机自动运行
)

echo   3/3 正在启动...
start "" "%DIR%\WinBC.exe" --tray
timeout /t 2 >nul

echo.
echo   安装完成！
echo.
echo   WinBC 已经在屏幕右下角托盘里运行了（可能藏在 ^ 箭头里）。
echo   以后每次关机前会自动记录桌面，开机后问你要不要恢复。
echo.
echo   想立刻试一下：右键托盘图标，点「立即记录一次」
echo   卸载：运行「卸载.bat」
echo.
pause