@echo off
rem Stanza 发布入口：双击或命令行运行均可，参数原样透传给 publish.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
if errorlevel 1 (
    echo.
    echo 发布失败，详见上方输出。
)
pause
