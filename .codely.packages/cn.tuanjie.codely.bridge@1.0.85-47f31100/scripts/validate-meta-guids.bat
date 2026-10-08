@echo off
node "%~dp0release-tools\validate.cjs" --guids-only
exit /b %ERRORLEVEL%
