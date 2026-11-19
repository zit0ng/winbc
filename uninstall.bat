@echo off
chcp 936 >nul
title WinBC 卸载
cd /d "%~dp0"

echo.
echo   正在卸载 WinBC...
echo.

taskkill /f /im WinBC.exe >nul 2>&1
if errorlevel 1 (
    echo   1/3 WinBC 当前没有在运行
) else (
    echo   1/3 已关闭正在运行的 WinBC
)

reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v WinBC /f >nul 2>&1
if errorlevel 1 (
    echo   2/3 本来就没有设置开机自启
) else (
    echo   2/3 已取消开机自动运行
)

if exist "%LOCALAPPDATA%\WinBC\bin\WinBC.exe" (
    del /f /q "%LOCALAPPDATA%\WinBC\bin\WinBC.exe" >nul 2>&1
    rmdir "%LOCALAPPDATA%\WinBC\bin" >nul 2>&1
    echo   3/3 程序文件已删除
) else (
    echo   3/3 程序文件本来就不在
)

echo.
echo   卸载完成。
echo.
echo   你的历史记录还保留在：
echo     %LOCALAPPDATA%\WinBC\snapshots
echo   想彻底删干净，把上面这个文件夹删掉就行。
echo.
pause