@echo off
chcp 65001 > nul
cd /d "D:\Haruki\Unity\Tentacle Wars"
echo [start] %date% %time%
python -m venv Training\.venv
Training\.venv\Scripts\python -m pip install --upgrade pip
Training\.venv\Scripts\python -m pip install torch --index-url https://download.pytorch.org/whl/cu128
echo [exit] %errorlevel% %date% %time%
