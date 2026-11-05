@echo off
setlocal
cd /d "%~dp0"

docker build -t mrgaton/dsfiles_server:latest -f DOCKERFILE .
if errorlevel 1 exit /b 1

docker push mrgaton/dsfiles_server:latest
if errorlevel 1 exit /b 1
