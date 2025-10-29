@echo off
chcp 65001 > nul
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Требуются права администратора. Перезапуск...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)


netsh advfirewall reset >nul 2>&1

route -f >nul 2>&1

echo. > %SystemRoot%\System32\drivers\etc\hosts

echo Команды для решения проблем с игрой успешно выполнены, перезапустите свой ПК!
pause
